# DevOps: договорённости (дни 1–2)

Цель DevOps-части: шаблон поднимается на чистой Ubuntu через `make setup && make up` и деплоится из GitLab CI в любое облако, даже если закрыты внешние registry.

Проверено на нашей машине: Docker 29.7 + Compose v5.3, .NET 10.0.111, Node 24.18 / npm 11.16, Python 3.11.15, pip 25.1. `uv`, `kubectl`, `helm` и `gitlab-runner` не установлены. Docker Hub без настроенного прокси демона за 25 с не ответил, а PyPI, NuGet и npm доступны через HTTP-прокси. Вывод: прокси и зеркала для **демона** Docker нужно настраивать отдельно от прокси для shell.

## 1. Вопросы организаторам и наша реакция

| Вопрос | Вариант ответа → что делаем |
| --- | --- |
| Тип облака: VM / Kubernetes / OpenStack? | **VM (в т.ч. OpenStack Nova)** → compose по ssh, наш основной путь. **Только K8s** → план Б: готовые манифесты (Deployment + Service + Ingress на сервис, Postgres как StatefulSet или managed). **OpenStack без готовой VM** → создаём VM сами (CLI `openstack server create`), вопросы о квотах, floating IP и security groups |
| Сколько VM, какие CPU/RAM/диск, есть ли root/sudo, какая ОС? | ≥2 vCPU / 4 ГБ / 30 ГБ хватает на 4 сервиса. Нет sudo → rootless Docker или просим добавить в группу `docker` |
| GitLab Runner есть? executor shell или docker? Общий или свой? | **docker executor** → сборка в `dotnet/sdk`, `python`, `node` образах, docker build через DinD или kaniko. **shell** → на хосте нужны docker/dotnet/node, bootstrap-скрипт ставит то же. **Нет runner** → регистрируем свой на нашей VM (нужен registration token) |
| Есть Container Registry в GitLab? | **Есть** → push образов из CI, на VM `docker compose pull`. **Нет** → `docker save \| ssh docker load` или сборка прямо на VM |
| Доступ к Docker Hub, MCR, PyPI, NuGet, npm, HuggingFace: прямой, через прокси, есть ли внутренние зеркала (Nexus/Artifactory)? | **Прямой** → ничего. **Прокси** → прописываем в `daemon.json`/systemd drop-in, `pip.conf`, `NuGet.Config`, `.npmrc`. **Закрыт** → офлайн-запас (п. 2) и локальные фиды |
| GPU есть? Какой, драйвер, CUDA? | **Нет** (ожидаем) → LightGBM/CatBoost на CPU, этого хватает. **Есть** → nvidia-container-toolkit, отдельный CUDA-образ только для обучения |
| Публичный адрес: floating IP, домен, TLS? Открытые порты? | **IP без домена** → nginx на :80, отдаём `http://IP`. **Домен** → Let's Encrypt (certbot / acme) при открытом :80. **Свой CA или wildcard от организаторов** → монтируем cert в nginx. Порт 80/443 закрыт → спрашиваем, какой открыт |
| Формат сдачи: ссылка, репозиторий, образ? Должен ли сервис жить после хакатона? | Определяет финальный деплой и README |

## 2. Офлайн-запас

Размеры примерные (amd64): несжатый размер в Docker, в скобках `docker save | gzip`.

| Что | Размер |
| --- | --- |
| `postgres:17-alpine` | ~270 МБ (~105) |
| `nginx:1.27-alpine` | ~50 МБ (~20) |
| `mcr.microsoft.com/dotnet/sdk:10.0` | ~900 МБ (~300) |
| `mcr.microsoft.com/dotnet/aspnet:10.0` | ~230 МБ (~90) |
| `python:3.11-slim` | ~130 МБ (~45) |
| `node:24-alpine` | ~160 МБ (~55) |
| **Итого образы** | **~1.7 ГБ (~0.6 ГБ)** |
| Wheelhouse: numpy, pandas, scikit-learn, lightgbm, catboost, statsmodels, holidays, fastapi, uvicorn, pydantic, shap, matplotlib | ~400–500 МБ |
| NuGet: EF Core + Npgsql, Polly, Serilog, Swashbuckle/OpenAPI, HealthChecks | ~150–250 МБ |
| npm: React, Vite, TS, MUI/AntD, Recharts/ECharts | ~300 МБ |

Как выгружаем (скрипт `infra/offline/pack.sh`, хранить на USB и в облачном хранилище):
- **Образы:** `docker pull` → `docker save img1 img2 … | gzip > images.tar.gz`; на месте `docker load -i`. Пин по тегу и digest. Плюс собранные образы нашего шаблона.
- **Python:** `pip download -r requirements.txt -d wheelhouse --platform manylinux2014_x86_64 --python-version 3.11 --only-binary=:all:`; установка `pip install --no-index --find-links wheelhouse -r requirements.txt`. Если поставим uv, то так же через `uv pip`.
- **.NET:** `dotnet restore --packages ./nuget-offline` (или копия `~/.nuget/packages`); на месте `NuGet.Config` с локальным source `./nuget-offline` и `<clear/>`.
- **npm:** `npm ci --cache ./npm-cache` и потом `npm ci --offline --cache ./npm-cache`. Запасной вариант: verdaccio в контейнере (тоже лежит в images.tar.gz) с прогретым storage.
- **Бинарники для bootstrap:** `.deb` пакеты docker-ce/compose-plugin, `dotnet-install.sh` + tarball SDK, Node tarball.

## 3. Скелет `infra/bootstrap.sh` (Ubuntu 22.04/24.04)

1. `set -euo pipefail`; проверка ОС, sudo, свободного места, наличия интернета (curl к Docker Hub, PyPI, NuGet, npm) → режим online/offline.
2. Прокси, если задан `HTTPS_PROXY`: env, `apt.conf`, systemd drop-in для dockerd, `pip.conf`, `.npmrc`, `NuGet.Config`.
3. apt: `ca-certificates curl git make jq unzip`.
4. Docker CE + compose-plugin из официального репозитория (offline: из `.deb`); добавить пользователя в `docker`; `daemon.json` с `registry-mirrors` и `log-opts max-size` по необходимости.
5. Только для dev-машин: .NET 10 SDK (`dotnet-install.sh`), Node 24 (tarball/nvm), Python 3.11 + venv, uv.
6. Offline-режим: `docker load` образов, настройка локальных фидов (wheelhouse, nuget-offline, npm-cache).
7. `git clone` шаблона, `cp .env.example .env`, генерация паролей (`openssl rand`).
8. Опционально: регистрация GitLab Runner (docker executor) на VM.
9. Smoke-тест: `docker compose up -d`, `curl /health` у api и ml через nginx, итог OK/FAIL.

Скрипт идемпотентный, каждый шаг можно запускать отдельно (`bootstrap.sh docker`, `bootstrap.sh offline`).

## 4. Решения

**Compose (`infra/docker-compose.yml`):** `frontend` (сборка Vite → статика в nginx-образе), `api` (ASP.NET Core), `ml` (FastAPI/uvicorn), `db` (Postgres) + `proxy` (nginx, reverse proxy). Одна сеть `app`, healthcheck на каждом сервисе, `depends_on: condition: service_healthy`, volume `pgdata`, логи json-file с ротацией.

| Сервис | Внутренний порт | Наружу (прод) | Локально (dev) |
| --- | --- | --- | --- |
| proxy (nginx) | 80/443 | **80/443 — единственная точка входа** | 8080 |
| frontend | 80 | — (`/` через proxy) | 5173 (vite dev) |
| api | 8080 | — (`/api/` через proxy) | 5000 |
| ml | 8000 | — (только из api) | 8000 |
| db | 5432 | — | 5432 на 127.0.0.1 |

Swagger API доступен по `/api/swagger`, ML-сервис наружу не публикуется (так в архитектуре).

**Секреты:** в репозитории только `.env.example`. Значения хранятся в GitLab CI/CD Variables (masked + protected; `SSH_PRIVATE_KEY` и `ENV_FILE` типа File). На VM лежит `/opt/app/.env` с правами 600, CI кладёт его при деплое. Пароль БД, connection string и ключи никогда не попадают в образы и логи. Список доступов (не значения) хранится у капитана в общем месте.

**Деплой:**
- **По умолчанию: compose по ssh из GitLab CI.** Стадии `build` (dotnet test, pytest, npm build) → `image` (docker build + push в GitLab Registry, тег `$CI_COMMIT_SHORT_SHA`) → `deploy` (manual для main: `ssh vm "cd /opt/app && docker compose pull && docker compose up -d"` + smoke `curl /api/health`). Откат: `IMAGE_TAG=<prev> docker compose up -d`. Без registry: `docker save | ssh docker load`.
- **План Б: k8s-манифесты** (`infra/k8s/`, kustomize, без helm): готовим на днях 2–4, но не полируем. Включаем, только если организаторы дают кластер без VM.
- Почему compose: меньше движущихся частей, деплой за минуты, легко отладить по ssh. Для 2 дней и 4 сервисов k8s даёт мало пользы и много рисков.
