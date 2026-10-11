#!/usr/bin/env bash
# Локальный режим дней 2–4 (CONVENTIONS.md «Локальный запуск»): db в Docker (ft-db), api/ml/frontend процессами.
# Использование: infra/scripts/dev.sh db | start [api|ml|frontend ...] | stop | status
# PID и логи: .run/<svc>.pid, .run/<svc>.log
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
RUN="$ROOT/.run"
DATA_ABS=${DATA_ABS:-$(cd "$ROOT/.." && pwd)/data}   # hackaton/data
DB_CONTAINER=${DB_CONTAINER:-ft-db}
DB_PORT=${DB_PORT:-55432}
DB_IMAGE=${DEV_DB_IMAGE:-postgres:13.1-alpine}
mkdir -p "$RUN"

log() { printf '[dev] %s\n' "$*"; }

db() {
  if docker container inspect "$DB_CONTAINER" >/dev/null 2>&1; then
    if [[ $(docker inspect -f '{{.State.Running}}' "$DB_CONTAINER") == true ]]; then
      log "$DB_CONTAINER уже запущен, переиспользую (localhost:$DB_PORT)"
    else
      log "$DB_CONTAINER существует, но остановлен: docker start"
      docker start "$DB_CONTAINER" >/dev/null
    fi
  else
    docker image inspect "$DB_IMAGE" >/dev/null 2>&1 || {
      log "образа $DB_IMAGE нет локально, а тянуть его не будем (сеть). docker load из offline/ или DEV_DB_IMAGE=..."; exit 1; }
    log "создаю $DB_CONTAINER из $DB_IMAGE"
    docker run -d --name "$DB_CONTAINER" -p "$DB_PORT:5432" -e POSTGRES_PASSWORD=postgres \
      -v "$ROOT/infra/db:/docker-entrypoint-initdb.d:ro" -v ft-db-data:/var/lib/postgresql/data "$DB_IMAGE" >/dev/null
  fi
  for _ in $(seq 1 30); do
    docker exec "$DB_CONTAINER" pg_isready -U postgres -d forecast >/dev/null 2>&1 && { log "db готова"; return 0; }
    sleep 1
  done
  log "db не ответила за 30 с: docker logs $DB_CONTAINER"; exit 1
}

running() { [[ -f "$RUN/$1.pid" ]] && kill -0 "$(cat "$RUN/$1.pid")" 2>/dev/null; }

# spawn <svc> <dir> <cmd...> : фоновый процесс в своей группе (setsid), чтобы stop убил и детей
spawn() {
  local svc=$1 dir=$2; shift 2
  if running "$svc"; then log "$svc уже работает (pid $(cat "$RUN/$svc.pid"))"; return; fi
  ( cd "$dir" && exec setsid "$@" ) >"$RUN/$svc.log" 2>&1 < /dev/null &
  echo $! > "$RUN/$svc.pid"
  log "$svc запущен (pid $!), лог: .run/$svc.log"
}

start_api() {
  local proj
  proj=$(find "$ROOT/api" -maxdepth 3 -name '*.csproj' ! -iname '*test*' 2>/dev/null | sort | head -1)
  [[ -n $proj ]] || { log "api: нет проекта .NET в api/, пропускаю"; return; }
  ASPNETCORE_URLS=http://localhost:5080 \
  ASPNETCORE_ENVIRONMENT=Development \
  ConnectionStrings__Forecast="Host=localhost;Port=$DB_PORT;Database=forecast;Username=app;Password=app" \
  Ml__BaseUrl=http://localhost:8000 \
  Ml__PredictAttemptTimeoutSeconds=120 Ml__PredictTotalTimeoutSeconds=300 Ml__PredictRetries=2 Ml__HealthTimeoutSeconds=2 \
  Import__Root="$DATA_ABS" \
    spawn api "$ROOT/api" dotnet run --project "$proj" --no-launch-profile
}

start_ml() {
  [[ -f "$ROOT/ml/app/main.py" ]] || { log "ml: нет ml/app/main.py, пропускаю"; return; }
  local uv=uvicorn
  [[ -x "$ROOT/ml/.venv/bin/uvicorn" ]] && uv="$ROOT/ml/.venv/bin/uvicorn"
  command -v "$uv" >/dev/null || { log "ml: uvicorn не найден (создайте ml/.venv)"; return; }
  ML_DB_DSN="postgresql://ml_reader:ml_reader@localhost:$DB_PORT/forecast" \
  DATA_DIR="$DATA_ABS" MODEL_DIR="$ROOT/ml/models" \
    spawn ml "$ROOT/ml" "$uv" app.main:app --host 127.0.0.1 --port 8000
}

start_frontend() {
  [[ -f "$ROOT/frontend/package.json" ]] || { log "frontend: нет package.json, пропускаю"; return; }
  [[ -d "$ROOT/frontend/node_modules" ]] || { log "frontend: нет node_modules (npm ci / npm ci --offline --cache ../offline/npm)"; return; }
  VITE_API_MODE=live VITE_PROXY_TARGET=http://localhost:5080 \
    spawn frontend "$ROOT/frontend" npm run dev -- --host 127.0.0.1 --port 5173 --strictPort
}

stop() {
  local any=0
  for f in "$RUN"/*.pid; do
    [[ -e $f ]] || continue
    local svc pid; svc=$(basename "$f" .pid); pid=$(cat "$f")
    if kill -0 "$pid" 2>/dev/null; then
      kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
      for _ in 1 2 3 4 5; do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
      kill -KILL -- "-$pid" 2>/dev/null || true
      log "$svc остановлен"; any=1
    fi
    rm -f "$f"
  done
  (( any )) || log "нечего останавливать"
  log "ft-db не трогаю (её используют другие); остановить: docker stop $DB_CONTAINER"
}

status() {
  for svc in api ml frontend; do
    if running "$svc"; then echo "$svc: работает (pid $(cat "$RUN/$svc.pid"))"; else echo "$svc: остановлен"; fi
  done
  docker ps --filter "name=^${DB_CONTAINER}$" --format '{{.Names}}: {{.Status}} {{.Ports}}' || true
}

cmd=${1:-start}; shift || true
case $cmd in
  db) db ;;
  start)
    db
    svcs=("$@"); (( ${#svcs[@]} )) || svcs=(ml api frontend)
    for s in "${svcs[@]}"; do "start_$s"; done
    log "адреса: api http://localhost:5080  ml http://localhost:8000  front http://localhost:5173"
    ;;
  stop) stop ;;
  status) status ;;
  *) echo "usage: $0 db|start [api|ml|frontend]|stop|status"; exit 2 ;;
esac
