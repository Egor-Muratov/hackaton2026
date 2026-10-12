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

# Сверка с infra/images.lock: тег на месте и ID совпадает с записанным при offline-pack (замена digest).
bad=0
while IFS= read -r ref; do
  img=${ref%@sha256:*}; f="$IN/images/$(echo "$img" | tr '/:' '__').tar.gz"
  have=$(docker image inspect -f '{{.Id}}' "$img" 2>/dev/null || true)
  if [[ -z $have ]]; then log "НЕТ образа $img (нет в запасе?)"; bad=1
  elif [[ ! -f $f.id ]]; then log "нет $f.id — не могу проверить $img"; bad=1
  elif [[ $have != "$(cat "$f.id")" ]]; then log "ID НЕ СОВПАДАЕТ: $img ($have != $(cat "$f.id"))"; bad=1
  else log "OK $img"; fi
done < <(grep -E '^(POSTGRES|NGINX|PYTHON|NODE|DOTNET_SDK|DOTNET_ASPNET)_IMAGE=' "$ROOT/infra/images.lock" | cut -d= -f2-)
(( bad )) && log "ВНИМАНИЕ: базовые образы не сходятся с images.lock — сборка OFFLINE=1 пойдёт в сеть или упадёт"
mkdir -p "$IN/wheels" "$IN/nuget" "$IN/npm"

cat <<HINT

Как пользоваться запасом без сети:
  compose:  OFFLINE=1 OFFLINE_DIR=$IN make up      # Dockerfile берут wheels/nuget/npm из контекста 'offline'
  python:   pip install --no-index --find-links $IN/wheels -r ml/requirements.txt
  .NET:     dotnet restore --configfile $IN/nuget.offline.config   (или --source $IN/nuget)
  npm:      npm ci --offline --cache $IN/npm
HINT
