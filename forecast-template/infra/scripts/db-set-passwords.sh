#!/bin/sh
# Выполняется в контейнере db из /docker-entrypoint-initdb.d ПОСЛЕ init.sql (только на пустом volume).
# Меняет локальные пароли app/ml_reader (= имени) на значения из .env.
# Для уже инициализированного volume: make db-passwords.
set -eu
if [ -z "${APP_DB_PASSWORD:-}" ] || [ -z "${ML_DB_PASSWORD:-}" ]; then
  echo "db-set-passwords: APP_DB_PASSWORD/ML_DB_PASSWORD не заданы, оставляю пароли из init.sql" >&2
  exit 0
fi
psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER:-postgres}" -d postgres \
  -v app_pw="$APP_DB_PASSWORD" -v ml_pw="$ML_DB_PASSWORD" <<'SQL'
ALTER ROLE app PASSWORD :'app_pw';
ALTER ROLE ml_reader PASSWORD :'ml_pw';
SQL
echo "db-set-passwords: пароли app и ml_reader обновлены"
