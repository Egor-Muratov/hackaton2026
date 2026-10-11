# Общие договорённости (контракты v0.1)

Владелец: C#-бэкенд. Ниже обязательные правила для всех ролей. Если файл расходится с черновиками, прав этот файл. Контракты: `public-api.yaml` (фронт ↔ API), `ml-api.yaml` (API ↔ ML), схема БД: `infra/db/init.sql`.

## Формат данных

| | Фронт ↔ API | API ↔ ML |
| --- | --- | --- |
| Регистр JSON | camelCase | snake_case |
| Интервал (80 %, p10/p90) | `yhat`, `lo`, `hi` | `yhat`, `lower`, `upper` |
| Ошибки | ProblemDetails (`application/problem+json`), 400 с `errors`, 404, 409 | FastAPI `{"detail": ...}`, 422 (валидация), 503 (модель не загружена) |
| Ключ ряда | `store` (номер или `all`) + `family` (имя или `all`) | `series_key` — произвольный объект `{store_nbr, family}` |

- Даты `YYYY-MM-DD`, моменты времени ISO 8601 UTC. Отсутствующее значение — `null`, никогда не NaN и не 0. Поля из схемы присутствуют всегда.
- `datasetId`/`runId`/`jobId` — UUID. `datasetId` = `dataset_id` у ML = `ml_observations.dataset_id`.
- Долгие операции: `202` + `Location`, опрос раз в 2 с. Статусы: `queued | running | done | failed`.
- Агрегат `all/all` с интервалом присылает ML (в первый день `lo/hi = null`). Частичные агрегаты API считает сам, у них всегда `lo/hi = null`. Если `lo/hi = null`, фронт не рисует полосу.
- Корреляция: заголовок `X-Correlation-Id` (фронт → API → ML), он же `traceId` в ProblemDetails.
- Эндпоинты с `x-stub: false` (`POST /api/datasets` multipart, ML `/train`) в заглушку дней 2–4 не входят.

## Локальный запуск (дни 2–4, без Docker-образов приложений)

| Компонент | Как | Адрес |
| --- | --- | --- |
| Postgres 13 | `docker run -d --name ft-db -p 55432:5432 -e POSTGRES_PASSWORD=postgres -v "$PWD/infra/db:/docker-entrypoint-initdb.d:ro" -v ft-db-data:/var/lib/postgresql/data postgres:13.1-alpine` (из корня `forecast-template`, образ уже есть локально, ничего не тянуть) | `localhost:55432` |
| C# API | `dotnet run` в `api/` | http://localhost:5080 (отвечает и на `/api/health`) |
| ML | `uvicorn app.main:app --port 8000` в `ml/` | http://localhost:8000 |
| Фронт | `npm run dev` в `frontend/`, proxy `/api` → `http://localhost:5080` | http://localhost:5173 |

`init.sql` выполняется только на пустом volume. Сбросить схему: `docker rm -f ft-db && docker volume rm ft-db-data`, затем снова запустить. Импорт репетиционных данных: `POST /api/datasets/import {"path":"store-sales/train.csv"}`, затем `{"path":"store-sales/test.csv","datasetId":"<id>"}`: это будущие `onpromotion`, у них `sales = null`.

## compose на VM

Сервисы: `db:5432` (Postgres 16, тот же `init.sql`), `api:8080`, `ml:8000`, `frontend:80` (статика), `nginx` (наружу только `:80`/`:443`). В nginx `/api/health` → `http://api:8080/health/ready`, `/api/` → `http://api:8080/api/`, `/` → `frontend:80`, `client_max_body_size 500m`. ML и db наружу не публикуются. Общий volume с данными монтируется в `/data` **только на чтение** в `api` и `ml`. Модели ML лежат в отдельном volume `/models`. API отдаёт `/health/live` (процесс жив) и `/health/ready` (БД обязательна; если ML недоступен, статус `Degraded`, но код 200).

## Переменные окружения (точные имена)

| Сервис | Переменная | Локально | compose |
| --- | --- | --- | --- |
| API | `ASPNETCORE_URLS` | `http://localhost:5080` | `http://+:8080` |
| API | `ConnectionStrings__Forecast` | `Host=localhost;Port=55432;Database=forecast;Username=app;Password=app` | `Host=db;Port=5432;Database=forecast;Username=app;Password=${APP_DB_PASSWORD}` |
| API | `Ml__BaseUrl` | `http://localhost:8000` | `http://ml:8000` |
| API | `Ml__PredictAttemptTimeoutSeconds` / `Ml__PredictTotalTimeoutSeconds` / `Ml__PredictRetries` | `120` / `300` / `2` | то же |
| API | `Ml__HealthTimeoutSeconds` | `2` | `2` |
| API | `Import__Root` (корень для `path` в импорте) | `<abs>/hackaton/data` | `/data` |
| ML | `ML_DB_DSN` (только `ml_reader`) | `postgresql://ml_reader:ml_reader@localhost:55432/forecast` | `postgresql://ml_reader:${ML_DB_PASSWORD}@db:5432/forecast` |
| ML | `DATA_DIR` (справочники stores/holidays/oil/transactions, пути в `ml/config.yaml` относительно него) | `<abs>/hackaton/data` | `/data` |
| ML | `MODEL_DIR` | `ml/models` | `/models` |
| Фронт | `VITE_API_MODE` (`live` или `mock`) | `live` | `live` при сборке |
| Фронт | `VITE_PROXY_TARGET` (только dev) | `http://localhost:5080` | не нужна |

Ретраи API → ML: только на 5xx и сетевые ошибки, на 422 не ретраим. Повтор `/predict` безопасен.

## БД

- База `forecast`. Пользователь `app` владеет схемой `public` и всеми объектами, через него работает API. Пользователь `ml_reader` может только `SELECT` из view `ml_observations(dataset_id, store_nbr, family, date, sales, onpromotion)`, его сессии по умолчанию read-only.
- Локальные пароли совпадают с именем пользователя (`app`/`app`, `ml_reader`/`ml_reader`). На VM DevOps меняет их через `ALTER ROLE` и передаёт в `.env`.
- Источник правды схемы — `init.sql`. EF Core маппится на него, `EnsureCreated` и миграции не используем. Изменения схемы идут через MR владельца контрактов.
- В `observations` строка с `sales IS NULL` — будущая дата с известными признаками. В историю для графика попадают только строки с `sales IS NOT NULL`. В `forecasts` строка с `series_id IS NULL` — полный агрегат.
- На хакатоне ключи ряда меняются в трёх местах: `series` (колонки и UNIQUE), `ml_observations` и `ml/config.yaml: series_keys`.
