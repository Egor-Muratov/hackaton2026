# Forecast template: точки входа. `make help` — список целей.
SHELL := /bin/bash
.DEFAULT_GOAL := help

-include .env

ROOT        := $(CURDIR)
OFFLINE_DIR ?=
override OFFLINE_DIR := $(if $(OFFLINE_DIR),$(abspath $(OFFLINE_DIR)),$(ROOT)/offline)
export OFFLINE_DIR
# .env, а если его ещё нет (CI, первая проверка) — .env.example; выбирается в момент запуска
COMPOSE     := docker compose --env-file $$([ -f .env ] && echo .env || echo .env.example) -f infra/docker-compose.yml
API_URL     ?= http://localhost:5080
SVC         ?=

# deploy
VM_HOST    ?=
VM_USER    ?= ubuntu
VM_PORT    ?= 22
VM_DIR     ?= /opt/forecast
BUILD_MODE ?= vm
SYNC       ?= git
export VM_HOST VM_USER VM_PORT VM_DIR BUILD_MODE SYNC

.PHONY: help setup check-network env offline-dirs dev-db dev dev-stop dev-status dev-logs \
        config build up down logs ps import smoke smoke-health db-passwords deploy \
        offline-pack offline-load

help: ## Список целей
	@grep -hE '^[a-zA-Z_-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

# ---------- подготовка ----------
setup: env offline-dirs ## Проверка инструментов + проверка сети (доступность и скорость)
	@echo "== Инструменты"
	@for t in docker git curl jq make python3 dotnet node npm ssh; do \
	  if command -v $$t >/dev/null; then printf '  %-8s %s\n' $$t "$$($$t --version 2>&1 | head -1)"; \
	  else printf '  %-8s НЕТ\n' $$t; fi; done
	@docker compose version >/dev/null 2>&1 && echo "  compose  $$(docker compose version --short)" || echo "  compose  НЕТ (нужен docker compose v2+)"
	@docker info >/dev/null 2>&1 || echo "  ВНИМАНИЕ: демон Docker недоступен текущему пользователю (группа docker?)"
	@echo
	@infra/scripts/check-network.sh

check-network: ## Только проверка сети
	@infra/scripts/check-network.sh

env: ## Создать .env из .env.example, если его нет
	@[ -f .env ] || { cp .env.example .env; chmod 600 .env; echo "создан .env из .env.example: поменяйте пароли"; }

offline-dirs:
	@mkdir -p "$(OFFLINE_DIR)"/{wheels,nuget,npm,images} .run

# ---------- локальный режим (дни 2–4): db в Docker, остальное процессами ----------
dev-db: ## Postgres ft-db на :55432 (переиспользует существующий контейнер)
	@infra/scripts/dev.sh db

dev: ## dev-db + api(:5080) ml(:8000) frontend(:5173) фоном; SVC="api ml" — выборочно
	@infra/scripts/dev.sh start $(SVC)

dev-stop: ## Остановить локальные процессы (ft-db не трогает)
	@infra/scripts/dev.sh stop

dev-status: ## Статус локальных процессов
	@infra/scripts/dev.sh status

dev-logs: ## Хвост логов локальных процессов
	@tail -n 50 -F .run/*.log

# ---------- compose (VM / прод-подобный запуск) ----------
config: ## Проверить и показать итоговый compose
	@$(COMPOSE) config

build: offline-dirs ## Собрать образы (прокси/зеркала/OFFLINE из .env)
	$(COMPOSE) build $(SVC)

up: env offline-dirs ## Поднять стек (сборка на этой машине), наружу nginx :80 (HTTP_PORT)
	$(COMPOSE) up -d --build $(SVC)
	@$(COMPOSE) ps

down: ## Остановить стек (volume сохраняются)
	$(COMPOSE) down

logs: ## Логи стека (SVC=api — один сервис)
	$(COMPOSE) logs -f --tail 200 $(SVC)

ps: ## Статус контейнеров стека
	@$(COMPOSE) ps

db-passwords: ## Применить APP_DB_PASSWORD/ML_DB_PASSWORD к уже инициализированной БД
	$(COMPOSE) exec -T db sh /docker-entrypoint-initdb.d/20-set-passwords.sh

# ---------- данные и проверки (API_URL=http://localhost для compose) ----------
import: ## Импорт store-sales: train.csv, затем test.csv в тот же датасет
	@API_URL=$(API_URL) infra/scripts/import.sh

smoke: ## health -> meta -> прогноз -> опрос -> series (end-to-end)
	@API_URL=$(API_URL) infra/scripts/smoke.sh

smoke-health: ## Только /api/health
	@API_URL=$(API_URL) infra/scripts/smoke.sh --health-only

# ---------- деплой ----------
deploy: ## ssh на VM: git pull + compose build/up (VM_HOST=... [BUILD_MODE=vm|registry] [SYNC=git|tar])
	@infra/scripts/deploy.sh

# ---------- офлайн-запас ----------
offline-pack: ## Собрать запас: образы, wheels, nuget, npm -> $(OFFLINE_DIR)
	@infra/scripts/offline-pack.sh

offline-load: ## Загрузить образы из запаса (docker load) + подсказки
	@infra/scripts/offline-load.sh
