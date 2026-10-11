# Forecast template

Шаблон сервиса прогнозирования временных рядов для хакатона: загрузка CSV, прогноз с интервалом 80 %, график и выгрузка. Контракты и правила лежат в `docs/contracts/` (обязательно прочитать `CONVENTIONS.md`).

## Запуск за 3 команды

На VM (Ubuntu, есть sudo):

```bash
sudo infra/scripts/bootstrap-vm.sh   # Docker + compose, прокси демона, .env со случайными паролями, проверка сети
make up                              # сборка и запуск: наружу только nginx :80
make import smoke API_URL=http://localhost
```

Локально (дни 2–4, без образов приложений):

```bash
make setup      # инструменты + сеть (доступность и скорость каждого источника)
make dev        # ft-db в Docker (:55432, переиспользуется) + api :5080, ml :8000, фронт :5173 фоновыми процессами
make smoke      # health -> meta -> прогноз -> опрос -> график all/all
```

`make dev-stop` останавливает процессы (ft-db остаётся), логи в `.run/*.log`, `make help` показывает все цели.

## Архитектура

Браузер ходит только в nginx (:80). nginx отдаёт `/` из контейнера `frontend` (статика Vite), `/api/*` проксирует в `api:8080`, а `/api/health` переписывает в `api:8080/health/ready`. Тело запроса ограничено 500 МБ (загрузка CSV). C# API хранит датасеты, ряды и прогнозы в Postgres (`db:5432`, роль `app`) и вызывает ML (`ml:8000`, FastAPI) для `/predict` и `/train`. ML читает историю из view `ml_observations` под ролью `ml_reader` (только чтение), а справочники берёт из общего каталога `/data`. Модели лежат в volume `/models`. `/data` монтируется в `api` и `ml` только на чтение, `db` и `ml` наружу не публикуются. Корреляция запросов идёт через `X-Correlation-Id` от фронта до ML, nginx пишет её в access-лог.

Деплой: GitLab CI (runner в Docker) ходит на VM по ssh. По умолчанию (`BUILD_MODE=vm`) образы собираются прямо на VM с тегом коммита, потом ручной `deploy` перезапускает стек и проверяет `/api/health`. Если в GitLab есть Container Registry, ставим `BUILD_MODE=registry`: образы собирает kaniko (или DinD при `BUILD_TOOL=dind`) и пушит в `$CI_REGISTRY_IMAGE`, а VM делает `docker compose pull`. Откат: `IMAGE_TAG=<старый sha> make deploy` (или повторный запуск deploy старого пайплайна).

## Структура

```
api/                 C# API (.NET 10), Dockerfile -> :8080          (C#-бэкенд)
ml/                  FastAPI + LightGBM, Dockerfile -> :8000        (ML)
frontend/            Vite + React + Ant Design, Dockerfile -> :80   (фронтенд)
docs/contracts/      CONVENTIONS.md, public-api.yaml, ml-api.yaml   (владелец контрактов)
infra/
  docker-compose.yml db, ml, api, frontend, nginx
  db/init.sql        схема БД (источник правды)
  nginx/nginx.conf   маршруты /api/health, /api/, /
  scripts/
    check-network.sh доступность и скорость Docker Hub, MCR, PyPI, npm, NuGet, GitHub (с прокси и без)
    bootstrap-vm.sh  подготовка Ubuntu VM (идемпотентно, шаги по отдельности, ретраи)
    dev.sh           локальный режим (ft-db + процессы, pid/логи в .run/)
    import.sh        импорт train.csv + test.csv через API
    smoke.sh         сквозная проверка
    deploy.sh        ssh-деплой (Makefile и CI)
    offline-pack.sh / offline-load.sh   офлайн-запас
    db-set-passwords.sh  смена паролей app/ml_reader на значения из .env
Makefile, .env.example, .gitlab-ci.yml
```

## Сеть, прокси и офлайн

Первое действие на любой новой машине: `make check-network`. Скрипт меряет и доступность, и скорость (первые 1 МБ файла), с прокси и без, и печатает итог `online / mirror / offline`. На репетиции демон Docker ходил в Docker Hub напрямую и получал 502, прокси давал 20–40 КБ/с, глобальный `NuGet.Config` указывал на недоступный корпоративный фид, а pip рвал соединения.

| Ситуация | Что делать |
| --- | --- |
| Docker Hub не виден демону | `sudo HTTPS_PROXY=... infra/scripts/bootstrap-vm.sh docker-proxy` (systemd drop-in) или `REGISTRY_MIRROR=...` |
| Прокси только для сборки | `BUILD_HTTP_PROXY`/`BUILD_HTTPS_PROXY` в `.env`. Если прокси на `127.0.0.1` хоста, добавьте `BUILD_NETWORK=host` |
| Есть зеркала (Nexus и т. п.) | `MIRROR_PIP_INDEX`, `MIRROR_NPM_REGISTRY`, `MIRROR_NUGET_SOURCE` в `.env` |
| Сети нет или она медленная | там, где сеть есть: `make offline-pack`; на месте: `make offline-load`, затем `OFFLINE=1` в `.env` и `make up` |

`offline/` (в git не попадает): `images/*.tar.gz` (`docker save`), `wheels/` (linux x86_64, py3.11), `nuget/` + `nuget.offline.config` (`<clear/>`, только локальная папка), `npm/` (кэш для `npm ci --offline`). Каталог можно переопределить: `OFFLINE_DIR=/abs/path`.

## Контракт образов (для авторов Dockerfile)

compose передаёт в каждую сборку build args `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY` (предопределённые, объявлять не нужно), а также `OFFLINE` (`0`/`1`), `MIRROR_PIP_INDEX`, `MIRROR_NPM_REGISTRY`, `MIRROR_NUGET_SOURCE`, для фронта ещё `VITE_API_MODE=live`. Офлайн-запас доступен как именованный контекст `offline` с подкаталогами `wheels/`, `nuget/`, `npm/` (они всегда существуют, но могут быть пустыми). Пример для ML:

```dockerfile
# syntax=docker/dockerfile:1.7
ARG OFFLINE=0
ARG MIRROR_PIP_INDEX=
RUN --mount=type=bind,from=offline,source=wheels,target=/offline/wheels \
    if [ "$OFFLINE" = 1 ]; then pip install --no-index --find-links /offline/wheels -r requirements.txt; \
    else pip install --retries 10 --timeout 60 ${MIRROR_PIP_INDEX:+--index-url $MIRROR_PIP_INDEX} -r requirements.txt; fi
```

Для .NET: `--mount=type=bind,from=offline,source=nuget,target=/offline/nuget` и `dotnet restore --source /offline/nuget` при `OFFLINE=1`. Для npm: `npm ci --offline --cache /offline/npm`. kaniko не поддерживает ни именованные контексты, ни `RUN --mount`: если Dockerfile их использует, в registry-пути ставьте `BUILD_TOOL=dind` (нужен privileged runner) или собирайте на VM (`BUILD_MODE=vm`).

Healthcheck в compose ожидает: `api` отдаёт `GET /health/live` и в образе есть `curl`; в `ml` есть `python` и `GET /health`; во `frontend` (nginx-alpine) есть `wget`. Порты: api 8080, ml 8000, frontend 80.

## Переменные и секреты

Все имена переменных взяты из `CONVENTIONS.md`, compose собирает их из `.env` (образец в `.env.example`). Пароли `app` и `ml_reader` на пустом volume ставит `infra/scripts/db-set-passwords.sh`, на существующем это делает `make db-passwords`. В GitLab CI/CD Variables хранятся `SSH_PRIVATE_KEY`, `VM_HOST`, `VM_USER` и, по желанию, `ENV_FILE` (тип File), который при деплое становится `.env` на VM. Пути к данным: `DATA_HOST_DIR` (на VM `/opt/forecast/data`, внутри контейнеров `/data`), путь импорта считается от него: `store-sales/train.csv`.
