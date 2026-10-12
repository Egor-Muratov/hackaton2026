# Архитектура решения

Пять контейнеров на одной VM в облаке, поднимаются через `make up` (`infra/docker-compose.yml`). Снаружи открыт только nginx. C#-бэкенд владеет публичным API и данными, ML-сервис только считает прогноз и закрыт за API.

## Сервисы

```mermaid
flowchart LR
  user["Браузер<br/>команда, жюри"]

  subgraph vm["VM в облаке · docker compose"]
    nginx["<b>nginx</b><br/>:80, единственный вход"]
    frontend["<b>frontend</b><br/>React + TS, AntD, ECharts<br/>статика :80"]
    api["<b>api</b><br/>ASP.NET Core :8080<br/>EF Core, Polly, Serilog<br/>импорт CSV, фоновые задачи"]
    ml["<b>ml</b><br/>FastAPI :8000<br/>/predict, /train, /health"]
    db[("<b>db</b><br/>PostgreSQL 16 :5432")]
    data[/"том data → /data<br/>CSV и справочники, ro"/]
    models[/"том models → /models"/]
  end

  user -->|"HTTP :80"| nginx
  nginx -->|"/ → статика"| frontend
  nginx -->|"/api/* · public-api.yaml<br/>JSON camelCase"| api
  api -->|"HTTP · ml-api.yaml<br/>JSON snake_case<br/>ретраи на 5xx"| ml
  api -->|"роль app<br/>чтение и запись"| db
  ml -->|"роль ml_reader<br/>SELECT из ml_observations"| db
  api -.->|импорт| data
  ml -.->|справочники| data
  ml -.-> models
```

- Фронт ходит только в C# API (через nginx), про ML не знает. ML можно заменить, не трогая фронт.
- `ml` и `db` наружу не публикуются.
- Заголовок `X-Correlation-Id` идёт сквозь цепочку фронт → API → ML.
- Если ML недоступен, `/api/health` отвечает `Degraded` с кодом 200. Без БД API не готов.

## Сборка и деплой

```mermaid
flowchart LR
  dev["git push / MR"] --> gl["GitLab CI"]
  gl --> lint["lint<br/>compose, образы, .NET,<br/>Python, фронт"]
  lint --> test["test<br/>dotnet, pytest, фронт"]
  test --> build["build"]
  build -->|"BUILD_MODE=vm<br/>по умолчанию"| ssh["ssh на VM<br/>сборка образов там"]
  build -->|"BUILD_MODE=registry"| reg["Container Registry"]
  reg -->|"docker compose pull"| vmc["VM: docker compose up"]
  ssh --> vmc
```

Если внешние реестры закрыты, на VM запускают `OFFLINE=1`: образы и пакеты берутся из заранее выгруженного запаса (`infra/scripts/offline-pack.sh` / `offline-load.sh`).

Контракты и переменные: [`contracts/CONVENTIONS.md`](contracts/CONVENTIONS.md).
