"""ML-сервис (заглушка дней 2–4): FastAPI по docs/contracts/ml-api.yaml.

Запуск: uvicorn app.main:app --port 8000 (из ml/). История читается из view ml_observations
под ml_reader (ML_DB_DSN) одним запросом, модель — бейзлайн из app/baseline.py.
"""
from __future__ import annotations

import json
import logging
import time
from datetime import date, timedelta
from typing import Any, Dict, List, Optional, Union
from uuid import UUID

import numpy as np
from fastapi import FastAPI, Header, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, ConfigDict, Field

from . import baseline
from .config import SUPPORTED_MODELS, Settings, load_settings

log = logging.getLogger("ml")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")


class State:
    settings: Optional[Settings] = None
    model_loaded: bool = False
    model_version: Optional[str] = None
    error: Optional[str] = None


state = State()


def load_model() -> None:
    try:
        s = load_settings()
        state.settings = s
        state.model_loaded = s.model_kind in SUPPORTED_MODELS
        state.model_version = s.model_version if state.model_loaded else None
        state.error = None if state.model_loaded else f"unknown model.kind {s.model_kind!r}"
    except Exception as e:  # конфиг битый -> живём, но /predict = 503
        log.exception("config load failed")
        state.settings, state.model_loaded, state.model_version, state.error = None, False, None, str(e)


load_model()
app = FastAPI(title="Forecast ML Service", version="0.1.0")

# ---------------------------------------------------------------- схемы -------
KeyValue = Union[int, float, str]


class PredictRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    dataset_id: UUID
    run_id: Optional[UUID] = None
    cutoff_date: date
    horizon: int = Field(ge=1, le=16)
    series_keys: Optional[List[Dict[str, KeyValue]]] = Field(..., min_length=1)
    include_aggregate: bool = False
    interval: float = Field(0.8, gt=0, lt=1)


class Health(BaseModel):
    status: str = "ok"
    model_version: Optional[str]
    model_loaded: bool


class ForecastPoint(BaseModel):
    series_key: Dict[str, KeyValue]
    date: date
    yhat: float
    lower: float
    upper: float


class AggregatePoint(BaseModel):
    date: date
    yhat: float
    lower: Optional[float]
    upper: Optional[float]


class CvMetrics(BaseModel):
    model_config = ConfigDict(extra="allow")
    rmsle: Optional[float]
    mae: Optional[float]
    baseline_rmsle: Optional[float]


class PredictResponse(BaseModel):
    model_version: str
    dataset_id: UUID
    cutoff_date: date
    horizon: int
    interval: float
    forecasts: List[ForecastPoint]
    aggregate: Optional[List[AggregatePoint]]
    cv_metrics: CvMetrics
    warnings: List[str]


# ---------------------------------------------------------------- данные ------
class DbUnavailable(Exception):
    pass


def fetch_rows(s: Settings, dataset_id: UUID, start: date, end: date) -> list[tuple]:
    """Один запрос: (key..., date, target, exog...) за start < date <= end."""
    import psycopg
    from psycopg import sql

    cols = [sql.Identifier(k) for k in s.series_keys] + [sql.Identifier(s.date_col), sql.Identifier(s.target)]
    cols += [sql.Identifier(c) for c in s.exog_future]
    q = sql.SQL("SELECT {cols} FROM {view} WHERE dataset_id = %s AND {d} > %s AND {d} <= %s").format(
        cols=sql.SQL(", ").join(cols), view=sql.Identifier(s.view), d=sql.Identifier(s.date_col)
    )
    try:
        with psycopg.connect(s.dsn, connect_timeout=5) as conn, conn.cursor() as cur:
            cur.execute(q, (dataset_id, start, end))
            return cur.fetchall()
    except psycopg.OperationalError as e:
        raise DbUnavailable(str(e)) from e


def canon(v: Any) -> str:
    if isinstance(v, float) and v.is_integer():
        v = int(v)
    return str(v)


def to_matrix(rows: list[tuple], nk: int, start_ord: int, T: int, cutoff_ord: int):
    """-> keys (список кортежей), Y [S, T] (NaN = нет строки), число строк будущих признаков."""
    kidx: dict[tuple, int] = {}
    si, ti, vals = [], [], []
    n_future_exog = 0
    for r in rows:
        key = r[:nk]
        i = kidx.get(key)
        if i is None:
            i = kidx[key] = len(kidx)
        d = r[nk].toordinal()
        if d > cutoff_ord:
            if any(x is not None for x in r[nk + 2 :]):
                n_future_exog += 1
            continue  # sales после cutoff не используем
        if r[nk + 1] is None:
            continue
        si.append(i)
        ti.append(d - start_ord)
        vals.append(r[nk + 1])
    Y = np.full((len(kidx), T), np.nan)
    if si:
        Y[np.asarray(si), np.asarray(ti)] = np.asarray(vals, dtype=float)
    return list(kidx), Y, n_future_exog


# ---------------------------------------------------------------- API ---------
@app.get("/health", response_model=Health)
def health() -> Health:
    return Health(status="ok", model_version=state.model_version, model_loaded=state.model_loaded)


@app.post(
    "/predict",
    response_model=PredictResponse,
    responses={422: {"description": "Невалидный запрос"}, 503: {"description": "Модель не загружена"}},
)
def predict(req: PredictRequest, x_correlation_id: Optional[str] = Header(None)) -> Response:
    t0 = time.perf_counter()
    cid = x_correlation_id or "-"
    if not state.model_loaded or state.settings is None:
        raise HTTPException(503, detail=f"model not loaded: {state.error}")
    s = state.settings
    if req.horizon > s.horizon:
        raise HTTPException(422, detail=f"horizon {req.horizon} > model horizon {s.horizon}")
    nk = len(s.series_keys)
    if req.series_keys is not None:
        bad = [k for k in req.series_keys if set(k) != set(s.series_keys)]
        if bad:
            raise HTTPException(422, detail=f"series_keys must have exactly fields {s.series_keys}, got {bad[0]}")

    c = s.lookback_days - 1  # индекс колонки cutoff
    start = req.cutoff_date - timedelta(days=s.lookback_days)  # строго больше start
    end = req.cutoff_date + timedelta(days=req.horizon)
    try:
        rows = fetch_rows(s, req.dataset_id, start, end)
    except DbUnavailable as e:
        log.error("cid=%s db unavailable: %s", cid, e)
        raise HTTPException(503, detail="database unavailable")
    t_db = time.perf_counter()
    if not rows:
        raise HTTPException(422, detail=f"unknown dataset_id or no data in ({start}, {end}]")
    keys, Y, n_future = to_matrix(rows, nk, start.toordinal() + 1, s.lookback_days + req.horizon,
                                  req.cutoff_date.toordinal())
    if np.all(np.isnan(Y[:, : c + 1])):
        raise HTTPException(422, detail=f"no history on or before cutoff_date {req.cutoff_date}")

    lookup = {tuple(canon(v) for v in k): i for i, k in enumerate(keys)}
    if req.series_keys is None:
        sel = list(range(len(keys)))
    else:
        sel, missing, seen = [], [], set()
        for k in req.series_keys:
            i = lookup.get(tuple(canon(k[f]) for f in s.series_keys))
            if i is None:
                missing.append(k)
            elif i not in seen:
                seen.add(i)
                sel.append(i)
        if missing:
            raise HTTPException(422, detail=f"unknown series_keys ({len(missing)}): {missing[:5]}")

    groups = None
    if s.group_by:
        gi = s.series_keys.index(s.group_by)
        groups = np.asarray([canon(k[gi]) for k in keys])
    res = baseline.predict(
        Y, c, req.horizon, H=s.horizon, weeks=s.weeks, folds=s.backtest_folds, step=s.backtest_step_days,
        interval=req.interval, groups=groups, min_group_samples=s.min_group_samples, zero_days=7 * s.zero_weeks,
    )
    warnings = list(res.warnings)
    no_hist = int(np.all(np.isnan(Y[:, : c + 1]) | (Y[:, : c + 1] == 0), axis=1).sum())
    if s.exog_future and n_future == 0:
        warnings.append(f"exog_future_missing: {','.join(s.exog_future)} defaulted to {s.exog_future_default}")

    dates = [(req.cutoff_date + timedelta(days=h)).isoformat() for h in range(1, req.horizon + 1)]
    yh, lo, hi = (np.round(a[sel], 4).tolist() for a in (res.yhat, res.lower, res.upper))
    forecasts = []
    for j, i in enumerate(sel):
        sk = dict(zip(s.series_keys, keys[i]))
        for h, d in enumerate(dates):
            forecasts.append({"series_key": sk, "date": d, "yhat": yh[j][h], "lower": lo[j][h], "upper": hi[j][h]})

    aggregate = None
    if req.include_aggregate:
        ay = np.round(res.agg_yhat, 4).tolist()
        al = np.round(res.agg_lower, 4).tolist() if res.agg_lower is not None else [None] * req.horizon
        au = np.round(res.agg_upper, 4).tolist() if res.agg_upper is not None else [None] * req.horizon
        aggregate = [{"date": d, "yhat": ay[h], "lower": al[h], "upper": au[h]} for h, d in enumerate(dates)]

    cv = {k: (round(v, 6) if v is not None else None) for k, v in res.cv.items()}
    body = {
        "model_version": s.model_version,
        "dataset_id": str(req.dataset_id),
        "cutoff_date": req.cutoff_date.isoformat(),
        "horizon": req.horizon,
        "interval": req.interval,
        "forecasts": forecasts,
        "aggregate": aggregate,
        "cv_metrics": cv,
        "warnings": warnings,
    }
    payload = json.dumps(body, allow_nan=False, separators=(",", ":"))  # NaN -> ошибка, а не невалидный JSON
    log.info(
        "cid=%s run=%s dataset=%s series=%d/%d rows=%d db=%.2fs total=%.2fs no_hist_or_zero=%d warnings=%s",
        cid, req.run_id, req.dataset_id, len(sel), len(keys), len(rows), t_db - t0, time.perf_counter() - t0,
        no_hist, warnings,
    )
    return Response(payload, media_type="application/json")
