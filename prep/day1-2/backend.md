# C#-бэкенд: договорённости (дни 1–2)

Репетиция: Kaggle Store Sales, 1782 ряда (`store_nbr × family`), около 3 млн строк, горизонт 16 дней. Ниже моя позиция. Пункты с пометкой **[согласовать]** обсуждаем на созвоне.

## 1. Стек

| Решение | Выбор | Почему |
| --- | --- | --- |
| Платформа | **.NET 10 (LTS)**, образы `mcr.microsoft.com/dotnet/aspnet:10.0` | Это LTS, и SDK 10.0.111 уже стоит. Версию фиксируем в `global.json` |
| Стиль API | **Minimal API** + `MapGroup` по ресурсам | Меньше кода, а эндпоинтов около десяти. Встроенная валидация и `TypedResults` дают точную схему в OpenAPI |
| БД | **EF Core 10 + Npgsql**, PostgreSQL 16 | EF нужен для CRUD, миграций и запросов. Массовая загрузка идёт мимо EF (см. п. 3) |
| ML-клиент | `AddHttpClient<MlClient>()` + **Microsoft.Extensions.Http.Resilience** | Это официальная обёртка над Polly v8: ретраи, таймауты и circuit breaker настраиваются одной строкой |
| Логи | **Serilog** (`Serilog.AspNetCore`), JSON в stdout, `UseSerilogRequestLogging` | Логи пишем в stdout для Docker и DevOps. Сквозной `X-Correlation-Id` передаём и в ML |
| Health checks | `/health/live` (процесс жив), `/health/ready` (Postgres и ML `/health`) | Нужны для docker-compose `healthcheck`, nginx и демо |
| OpenAPI | **Встроенный `Microsoft.AspNetCore.OpenApi`** + Scalar UI | Swashbuckle больше не входит в шаблоны. Встроенный генератор отдаёт `/openapi/v1.json` в рантайме и при сборке (`Microsoft.Extensions.ApiDescription.Server`), так что файл лежит в репо и фронт генерирует клиент без запуска API |
| Фоновые задачи | `Channel<Guid>` + `BackgroundService` | Без Hangfire/Quartz: для хакатона хватает очереди в памяти, статус хранится в БД |
| CSV | **Sylvan.Data.Csv** (запасной вариант: CsvHelper) | Потоковый и самый быстрый парсер, мало аллокаций |

Ошибки отдаём в формате ProblemDetails (RFC 9457). JSON в camelCase, даты в ISO `yyyy-MM-dd`, id запусков в виде GUID.

## 2. Черновик публичного API (фронт ↔ C#)

Все пути начинаются с `/api`. Фронт ходит только сюда.

| Метод и путь | Ответ |
| --- | --- |
| `POST /datasets` (multipart `file`, `?kind=train`) | `201 {datasetId, rows, seriesCount, dateFrom, dateTo, warnings[]}`. Ответ синхронный: на 3 млн строк уходит около 20 с |
| `GET /datasets/{id}` | `{datasetId, name, rows, seriesCount, dateFrom, dateTo, uploadedAt}` |
| `GET /series?datasetId=&search=&page=&pageSize=` | `{items:[{seriesId, key:"1|GROCERY I", attrs:{store_nbr, family}, lastDate, totalSales}], total}` |
| `GET /series/{id}/history?from=&to=` | `{seriesId, points:[{date, value}]}` |
| `POST /forecasts` `{datasetId, horizon:16, seriesIds?:[...]}` | `202 {runId, status:"queued"}` + заголовок `Location`. Без `seriesIds` считаем все ряды |
| `GET /forecasts/{runId}` | `{runId, status: queued|running|done|failed, horizon, model, createdAt, finishedAt, error?, metrics?}`. Фронт опрашивает раз в 2 с |
| `GET /forecasts/{runId}/series/{seriesId}` | `{seriesId, history:[{date,value}], forecast:[{date, yhat, lo, hi}]}`. Всё для графика «факт / прогноз» одним запросом |
| `GET /forecasts/{runId}/export?format=csv\|kaggle` | `text/csv`: `series_key,date,yhat,lo,hi`, либо `id,sales` для сабмита |
| `GET /health/live`, `/health/ready` | `200 Healthy` / `503` |

**[согласовать с фронтом]** Подходит ли схема «202 + опрос статуса» (SSE не делаем). Достаточно ли одного интервала `lo/hi` (p10/p90). Нужен ли агрегат «все магазины» в `/series` (добавлю псевдоряд `seriesId=0`).

## 3. Загрузка 3 млн строк и модель БД

**Как грузим:** не через `AddRange`/`SaveChanges`, потому что change tracker и INSERT по строке растянут загрузку на минуты.
1. Читаем тело запроса потоком (`MultipartReader`, без буферизации в память). Лимит тела поднимаем до 1 ГБ в Kestrel и через `client_max_body_size` в nginx (задача DevOps).
2. Sylvan парсит строки. Ключ ряда сразу переводим в `series_id` через словарь в памяти: рядов всего 1782, новые вставляем по ходу.
3. Пишем через `NpgsqlBinaryImporter` (`COPY observations FROM STDIN (FORMAT BINARY)`) одной транзакцией.
4. Индекс и PK создаём после COPY, затем `ANALYZE`. Ожидаемое время 10–30 с. Замеряем на репетиции.
5. Резервный путь: `POST /datasets/import-local?path=` для файла, уже лежащего в volume. Он спасает при медленном канале и лимитах прокси.

Валидация при загрузке: обязательные колонки, формат даты, `sales >= 0`, дубли `(ряд, дата)`. Ошибки складываем в `warnings[]` (первые 100), а не роняем всю загрузку.

**Таблицы:**
- `datasets(id, name, kind, rows, date_from, date_to, uploaded_at)`
- `series(id serial, dataset_id, key text, attrs jsonb)` с уникальным `(dataset_id, key)`. Ряд описан универсально, без жёсткой привязки к `store_nbr/family`
- `observations(series_id int, date date, value real, promo int)` с PK `(series_id, date)`. Это основная таблица, около 3 млн строк
- `forecast_runs(id uuid, dataset_id, horizon, model, status, created_at, finished_at, error, metrics jsonb)`
- `forecasts(run_id, series_id, date, yhat real, lo real, hi real)` с PK `(run_id, series_id, date)`. На прогон выходит 1782 × 16 ≈ 28,5 тыс. строк, их тоже пишем через COPY

Справочники Store Sales (`stores`, `oil`, `holidays_events`, `transactions`) на репетиции не грузим в БД: ML читает их как файлы из общего volume. На хакатоне решаем по задаче.

## 4. Стык с ML-сервисом (позиция)

- **Историю хранит C#**: Postgres остаётся единственным источником правды. ML не владеет данными и не пишет в БД.
- **Как ML получает историю.** Для всех рядов сразу 1782 ряда × 400+ дней лагов дают около 700 тыс. точек, это десятки МБ JSON в запросе. Поэтому **основной режим такой: ML сам читает историю** из read-only view `v_history(series_key, date, value, promo)` под отдельным пользователем БД `ml_reader`. Запрос `POST /predict {runId, datasetId, horizon, seriesKeys?, cutoffDate}` возвращает `{model, items:[{seriesKey, points:[{date,yhat,lo,hi}]}]}`. Ответ весит около 2 МБ, это нормально. **Запасной режим** для маленьких данных и тестов: история передаётся прямо в теле (`series:[{key, points}]`). Схема одна, поле `series` необязательное.
- **Синхронно или фоном.** Пользовательский запрос никогда не ждёт ML. `POST /forecasts` создаёт run и кладёт его в очередь, `BackgroundService` вызывает ML, складывает результат в `forecasts` и ставит статус `done/failed`. Сам вызов ML синхронный HTTP внутри фоновой задачи, без брокеров.
- **Таймауты и ретраи.** Для `/predict` настраиваем отдельный pipeline: attempt timeout 120 с, total 300 с, 2 ретрая с экспоненциальной задержкой только на 5xx/408/сетевые ошибки, circuit breaker мягкий. Вызов идемпотентен по `runId`. Для `/health` таймаут 2 с без ретраев. Стандартный `AddStandardResilienceHandler` с total 30 с прогноз не выдержит, это важно.
- **Обучение** идёт офлайн (`train.py`), модель лежит в volume ML. API обучение не запускает. Если останется время, добавим `POST /retrain` тем же фоновым механизмом.

**[согласовать с ML]** Чтение из БД или из тела запроса. Имена полей `seriesKey/yhat/lo/hi` и уровни интервала. `cutoffDate` и как ML отказывает (422 с текстом). Допустимая длительность `/predict` на 1782 рядах. Отдаёт ли ML `metrics` по валидации для слайда.
