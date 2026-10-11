#!/usr/bin/env bash
# Разворачивает офлайн-запас из $OFFLINE_DIR (по умолчанию <repo>/offline): docker load образов
# и подсказки, как ставить зависимости без сети. Идемпотентный.
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
IN=${OFFLINE_DIR:-$ROOT/offline}; [[ $IN = /* ]] || IN="$ROOT/$IN"
log() { printf '[offline-load] %s\n' "$*"; }
[[ -d $IN ]] || { log "нет каталога $IN"; exit 1; }

if [[ -f $IN/SHA256SUMS ]]; then
  log "проверка SHA256SUMS"
  ( cd "$IN" && sha256sum --quiet -c SHA256SUMS ) || log "ВНИМАНИЕ: контрольные суммы не сошлись (файл битый или дописан позже)"
fi

shopt -s nullglob
n=0
for f in "$IN"/images/*.tar.gz "$IN"/images/*.tar; do
  if [[ -f $f.id ]] && docker image inspect "$(cat "$f.id")" >/dev/null 2>&1; then log "уже загружен: $(basename "$f")"; continue; fi
  log "docker load $(basename "$f")"; docker load -i "$f" && n=$((n+1)) || log "ОШИБКА загрузки $f"
done
log "загружено образов: $n"
mkdir -p "$IN/wheels" "$IN/nuget" "$IN/npm"

cat <<HINT

Как пользоваться запасом без сети:
  compose:  OFFLINE=1 OFFLINE_DIR=$IN make up      # Dockerfile берут wheels/nuget/npm из контекста 'offline'
  python:   pip install --no-index --find-links $IN/wheels -r ml/requirements.txt
  .NET:     dotnet restore --configfile $IN/nuget.offline.config   (или --source $IN/nuget)
  npm:      npm ci --offline --cache $IN/npm
HINT
