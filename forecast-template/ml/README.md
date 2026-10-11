# ML-сервис (заглушка дней 2–4)

FastAPI по контракту `docs/contracts/ml-api.yaml`: `GET /health`, `POST /predict` (`/train` не входит в заглушку).
История читается одним запросом из view `ml_observations` под `ml_reader` (`ML_DB_DSN`), всё считается на numpy.

**Модель (бейзлайн):** среднее по дню недели за последние `model.weeks` недель до `cutoff_date` в `log1p`,
прогноз `expm1` + `clip(0)`. Ряд из нулей за последние `interval.zero_weeks` недель → прогноз 0, интервал `[0, 0]`
(warning `series_all_zero: N`). Дни без данных у всего датасета (25 декабря) игнорируются, пропуск у отдельного ряда = 0.

**Интервал:** эмпирические квантили `(1-interval)/2` и `1-(1-interval)/2` остатков `log1p(факт) - log1p(прогноз)` на
`backtest_folds` отсечках (шаг `backtest_step_days`, горизонт 16), отдельно по шагу горизонта и группе `group_by`.
Агрегат: остатки суммы по всем рядам, пул по шагам и отсечкам. Любой `interval` из (0, 1) поддерживается.

**cv_metrics:** на последних 16 днях до `cutoff_date`: `rmsle`/`mae` бейзлайна, `baseline_rmsle` — сезонный naive
(значение неделю назад). Считаются на лету при каждом запросе.

## Запуск локально

```bash
cd ml
python3.11 -m venv .venv && . .venv/bin/activate
pip install --no-index --find-links ../../offline/wheels -r requirements.txt   # или без --no-index
export ML_DB_DSN=postgresql://ml_reader:ml_reader@localhost:55432/forecast DATA_DIR=$PWD/../../data
uvicorn app.main:app --port 8000
pytest -q                                   # без БД (fetch_rows подменён)
ML_TEST_DATASET_ID=<uuid ml-fixture> pytest -q   # + тест на живой БД
```

Конфиг: `config.yaml` (путь можно переопределить `ML_CONFIG`): ключи рядов, горизонт, окна, интервал, праздники
(заготовка, бейзлайн их пока не использует). Неизвестный `model.kind` → `model_loaded=false`, `/predict` → 503.
Ограничение: в выдачу при `series_keys=null` попадают ряды, у которых есть строки в окне
`(cutoff - lookback_days, cutoff + horizon]` (lookback = 16 + (K-1)·step + 7·weeks = 93 дня по умолчанию).
