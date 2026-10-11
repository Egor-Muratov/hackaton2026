"""Бейзлайн: среднее по дню недели за последние N недель (в log1p), интервалы по остаткам бэктеста.

Всё на numpy, без pandas. Матрица Y: [S рядов, T дней], колонка c = cutoff_date.
NaN в Y = нет наблюдения ни у одного ряда в этот день (например, 25 декабря): такие дни
игнорируются и в среднем, и в метриках. Пропуск у отдельного ряда в «живой» день = 0 продаж.
"""
from __future__ import annotations

import warnings
from dataclasses import dataclass

import numpy as np


@dataclass
class BaselineResult:
    yhat: np.ndarray            # [S, h]
    lower: np.ndarray           # [S, h]
    upper: np.ndarray           # [S, h]
    zero: np.ndarray            # [S] bool — ряд из нулей, прогноз 0
    agg_yhat: np.ndarray        # [h]
    agg_lower: np.ndarray | None
    agg_upper: np.ndarray | None
    cv: dict
    warnings: list


def fill_series_gaps(Y: np.ndarray, c: int) -> np.ndarray:
    """В днях истории, где есть данные хоть у одного ряда, пропуск ряда = 0."""
    Y = Y.copy()
    hist = Y[:, : c + 1]
    alive = ~np.all(np.isnan(hist), axis=0)
    sub = hist[:, alive]
    sub[np.isnan(sub)] = 0.0
    hist[:, alive] = sub
    Y[:, : c + 1] = hist
    return Y


def dow_profile(L: np.ndarray, c: int, weeks: int, H: int) -> np.ndarray:
    """Прогноз в log1p на шаги 1..H от отсечки c: среднее того же дня недели за `weeks` недель."""
    S = L.shape[0]
    lo = c - 7 * weeks + 1
    W = L[:, max(lo, 0) : c + 1]
    if lo < 0:  # история короче окна: дополняем NaN слева
        W = np.concatenate([np.full((S, -lo), np.nan), W], axis=1)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        P = np.nanmean(W.reshape(S, weeks, 7), axis=1)  # [S, 7]; колонка j = день недели c-6+j
    P = np.nan_to_num(P, nan=0.0)
    return P[:, np.arange(H) % 7]  # шаг h -> колонка (h-1) % 7


def zero_mask(Y: np.ndarray, c: int, days: int) -> np.ndarray:
    W = Y[:, max(c - days + 1, 0) : c + 1]
    return np.nansum(W, axis=1) <= 0


def _nanq(x: np.ndarray, qs, axis):
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        return np.nanquantile(x, qs, axis=axis)


def predict(
    Y: np.ndarray,
    c: int,
    horizon: int,
    *,
    H: int,
    weeks: int,
    folds: int,
    step: int,
    interval: float,
    groups: np.ndarray | None,
    min_group_samples: int,
    zero_days: int,
) -> BaselineResult:
    warns: list[str] = []
    S = Y.shape[0]
    Y = fill_series_gaps(Y, c)
    Y[:, c + 1 :] = np.nan  # будущее не используем
    L = np.log1p(Y)

    def fit(ck: int) -> np.ndarray:
        p = dow_profile(L, ck, weeks, H)
        p[zero_mask(Y, ck, zero_days)] = 0.0
        return p

    pred = fit(c)[:, :horizon]
    zero = zero_mask(Y, c, zero_days)

    # Бэктест: K отсечек c_k = c - H - k*step, остатки в log1p на каждом шаге 1..H.
    R = np.full((folds, S, H), np.nan)
    agg_res = []
    cv = {"rmsle": None, "mae": None, "baseline_rmsle": None}
    for k in range(folds):
        ck = c - H - k * step
        if ck - 7 * weeks + 1 < 0:
            break
        p = fit(ck)
        a = L[:, ck + 1 : ck + H + 1]
        R[k] = a - p
        live = ~np.all(np.isnan(a), axis=0)
        if live.any():
            agg_p = np.expm1(p).sum(axis=0)
            agg_a = np.nansum(np.expm1(a), axis=0)
            agg_res.append((np.log1p(agg_a) - np.log1p(agg_p))[live])
        if k == 0:  # метрики на последних H днях до cutoff
            m = ~np.isnan(a)
            if m.any():
                naive = np.nan_to_num(L[:, ck - 6 + (np.arange(H) % 7)], nan=0.0)
                cv["rmsle"] = float(np.sqrt(np.mean((p - a)[m] ** 2)))
                cv["mae"] = float(np.mean(np.abs(np.expm1(p) - np.expm1(a))[m]))
                cv["baseline_rmsle"] = float(np.sqrt(np.mean((naive - a)[m] ** 2)))
    if cv["rmsle"] is None:
        warns.append(f"cv_unavailable: need {H + 7 * weeks} days of history")

    alpha = (1.0 - interval) / 2.0
    qs = [alpha, 1.0 - alpha]
    n_res = int((~np.isnan(R)).sum())
    if n_res == 0:
        warns.append("interval_fallback: no backtest residuals, lower=upper=yhat")
        Q = np.zeros((2, S, H))
    else:
        Q = np.broadcast_to(_nanq(R, qs, axis=(0, 1))[:, None, :], (2, S, H)).copy()
        if groups is not None:
            for g in np.unique(groups):
                m = groups == g
                Rg = R[:, m, :]
                enough = (~np.isnan(Rg)).sum(axis=(0, 1)) >= min_group_samples
                if enough.any():
                    qg = _nanq(Rg, qs, axis=(0, 1))
                    for j in np.nonzero(enough)[0]:
                        Q[0, m, j] = qg[0, j]
                        Q[1, m, j] = qg[1, j]
        Q = np.nan_to_num(Q, nan=0.0)
    qlo = np.minimum(Q[0, :, :horizon], 0.0)
    qhi = np.maximum(Q[1, :, :horizon], 0.0)

    yhat = np.clip(np.expm1(pred), 0, None)
    lower = np.clip(np.expm1(pred + qlo), 0, None)
    upper = np.clip(np.expm1(pred + qhi), 0, None)
    yhat[zero] = lower[zero] = upper[zero] = 0.0
    lower = np.minimum(lower, yhat)
    upper = np.maximum(upper, yhat)

    agg = yhat.sum(axis=0)
    agg_lo = agg_hi = None
    if agg_res:
        ar = np.concatenate(agg_res)  # пул по шагам и отсечкам: по одной точке на шаг мало
        aq = np.quantile(ar, qs)
        agg_lo = np.clip(np.expm1(np.log1p(agg) + min(aq[0], 0.0)), 0, None)
        agg_hi = np.expm1(np.log1p(agg) + max(aq[1], 0.0))
    if zero.any():
        warns.append(f"series_all_zero: {int(zero.sum())}")
    return BaselineResult(yhat, lower, upper, zero, agg, agg_lo, agg_hi, cv, warns)
