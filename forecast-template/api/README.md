# Forecast API (C#, заглушка дней 2–4)

ASP.NET Core 10 Minimal API между фронтом и ML. Контракты: `docs/contracts/public-api.yaml`, `ml-api.yaml`, `CONVENTIONS.md`; схема БД: `infra/db/init.sql`.

**Запуск локально:** нужен Postgres `ft-db` на `localhost:55432`. Команда `dotnet run` в `api/` поднимает API на http://localhost:5080 (окружение Development, локальные значения лежат в `appsettings.Development.json`). Документация: `/scalar`, `/openapi/v1.json`. Всё остальное задаётся переменными из CONVENTIONS: `ConnectionStrings__Forecast`, `Ml__BaseUrl`, `Ml__Predict*`, `Ml__HealthTimeoutSeconds`, `Import__Root`, `ASPNETCORE_URLS`.

**Docker:** `docker build -t forecast-api api/`. Образ слушает порт 8080. Restore в образе идёт только с nuget.org.

**NuGet:** `nuget.config` очищает глобальные источники (корпоративный фид недоступен) и подключает nuget.org и офлайн-запас `../../offline/nuget`. Если этого каталога нет, удалите строку или запустите `dotnet restore --ignore-failed-sources`. Версии пакетов зафиксированы точно, чтобы restore не ходил в сеть за плавающими версиями.

**Эндпоинты:**
- `/api/health`, `/health/live`, `/health/ready`;
- `POST /api/datasets/import` и `GET /api/jobs/{id}`;
- `GET /api/meta`;
- `POST /api/forecasts` и `GET /api/forecasts/{runId}`;
- `GET /api/forecasts/latest/series`;
- `GET /api/forecasts/{runId}/export.csv`.

Multipart `POST /api/datasets` и ML `/train` пока не реализованы (в контракте помечены `x-stub: false`).

**Устройство:**
- Фоновые задачи работают через `Channel` и `BackgroundService`. Очередь живёт в памяти, статус хранится в БД. Если API перезапустился, незавершённые задачи помечаются `failed`.
- Импорт читает CSV потоково, свой парсер. Первый проход собирает ключи рядов, второй пишет строки через COPY BINARY прямо в `observations`.
- Для файлов больше 10 МБ FK `observations → series` на время COPY снимается внутри той же транзакции и затем возвращается. Пока идёт импорт, таблицы заблокированы.
- Дозапись (`test.csv` с `datasetId`) идёт через временную таблицу и upsert.
- Прогноз: фоновая задача вызывает ML `/predict` с `series_keys: null` и `include_aggregate: true`, затем пишет результат в `forecasts` через COPY.
- Ретраи к ML: только на 5xx и сетевые ошибки, 120 с на попытку, 300 с всего.

**Замер (PG 13 в Docker):** импорт `train.csv` (3 000 888 строк, 1782 ряда) занял 21.8 с. С включённым FK было 106 с. Дозапись `test.csv` (28 512 строк) заняла 2.5 с.

**TODO:** перейти на EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`, маппинг на `init.sql`, без миграций), сделать multipart-загрузку, отдавать `/openapi/v1.json` на этапе сборки.
