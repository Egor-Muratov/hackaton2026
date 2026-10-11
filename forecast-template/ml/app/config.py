"""Загрузка ml/config.yaml и переменных окружения (имена — из CONVENTIONS.md)."""
from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml

ML_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_DSN = "postgresql://ml_reader:ml_reader@localhost:55432/forecast"
SUPPORTED_MODELS = {"dow_mean"}


@dataclass
class Settings:
    view: str = "ml_observations"
    date_col: str = "date"
    target: str = "sales"
    series_keys: list[str] = field(default_factory=lambda: ["store_nbr", "family"])
    horizon: int = 16
    model_kind: str = "dow_mean"
    model_version: str = "baseline-dowmean-4w-v0.1"
    weeks: int = 4
    interval_default: float = 0.8
    backtest_folds: int = 8
    backtest_step_days: int = 7
    group_by: str | None = "family"
    min_group_samples: int = 50
    zero_weeks: int = 4
    exog_future: list[str] = field(default_factory=lambda: ["onpromotion"])
    exog_future_default: float = 0
    holidays: dict[str, Any] = field(default_factory=dict)
    dsn: str = DEFAULT_DSN
    data_dir: str = ""
    model_dir: str = ""

    @property
    def lookback_days(self) -> int:
        """Сколько дней истории до cutoff нужно бейзлайну + бэктесту."""
        return self.horizon + (self.backtest_folds - 1) * self.backtest_step_days + 7 * self.weeks


def load_settings(path: str | os.PathLike | None = None) -> Settings:
    path = Path(path or os.environ.get("ML_CONFIG", ML_ROOT / "config.yaml"))
    raw: dict[str, Any] = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    src, model, itv = raw.get("source", {}), raw.get("model", {}), raw.get("interval", {})
    s = Settings(
        view=src.get("view", "ml_observations"),
        date_col=src.get("date_col", "date"),
        target=src.get("target", "sales"),
        series_keys=list(raw.get("series_keys", ["store_nbr", "family"])),
        horizon=int(raw.get("horizon", 16)),
        model_kind=model.get("kind", "dow_mean"),
        model_version=model.get("version", "baseline-dowmean-4w-v0.1"),
        weeks=int(model.get("weeks", 4)),
        interval_default=float(itv.get("default", 0.8)),
        backtest_folds=int(itv.get("backtest_folds", 8)),
        backtest_step_days=int(itv.get("backtest_step_days", 7)),
        group_by=itv.get("group_by"),
        min_group_samples=int(itv.get("min_group_samples", 50)),
        zero_weeks=int(itv.get("zero_weeks", 4)),
        exog_future=list(raw.get("exog_future", [])),
        exog_future_default=raw.get("exog_future_default", 0),
        holidays=raw.get("holidays", {}) or {},
        dsn=os.environ.get("ML_DB_DSN", DEFAULT_DSN),
        data_dir=os.environ.get("DATA_DIR", ""),
        model_dir=os.environ.get("MODEL_DIR", str(ML_ROOT / "models")),
    )
    if s.group_by is not None and s.group_by not in s.series_keys:
        raise ValueError(f"interval.group_by={s.group_by!r} must be one of series_keys")
    return s
