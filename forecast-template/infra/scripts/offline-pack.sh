#!/usr/bin/env bash
# Собирает офлайн-запас там, где сеть есть/быстрая. Результат: $OFFLINE_DIR (по умолчанию <repo>/offline):
#   images/*.tar.gz  базовые + собранные образы (docker save | gzip, по файлу на образ)
#   wheels/          pip download под linux x86_64 / py3.11 (для Dockerfile ml и локального venv)
#   nuget/           пакеты NuGet (dotnet restore --packages) с ЧИСТЫМ nuget.config (<clear/> + nuget.org)
#   npm/             кэш npm (npm ci --cache), ставится через npm ci --offline --cache
#   SHA256SUMS, MANIFEST.txt
# Части: offline-pack.sh [images] [wheels] [nuget] [npm]  (по умолчанию все). Повторный запуск докачивает недостающее.
# NO_PULL=1 — не тянуть образы, сохранять только те, что уже есть локально.
set -uo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${OFFLINE_DIR:-$ROOT/offline}; [[ $OUT = /* ]] || OUT="$ROOT/$OUT"
PY_VER=${PY_VER:-3.11}
IMAGE_TAG=${IMAGE_TAG:-local}
REGISTRY_PREFIX=${REGISTRY_PREFIX:-forecast}
# Базовые образы приложения — из infra/images.lock (точный тег@digest); CI-образы (DOCKER_*, ALPINE_*) не берём.
BASE_IMAGES=(${BASE_IMAGES:-$(grep -E '^(POSTGRES|NGINX|PYTHON|NODE|DOTNET_SDK|DOTNET_ASPNET)_IMAGE=' "$ROOT/infra/images.lock" | cut -d= -f2- | tr '\n' ' ')})
APP_IMAGES=("$REGISTRY_PREFIX/api:$IMAGE_TAG" "$REGISTRY_PREFIX/ml:$IMAGE_TAG" "$REGISTRY_PREFIX/frontend:$IMAGE_TAG")
FAILED=()

log() { printf '[offline-pack] %s\n' "$*"; }
retry() { local n=1; until "$@"; do (( n >= 4 )) && return 1; log "повтор $n через $((n*10))s: $1 ..."; sleep $((n*10)); n=$((n+1)); done; }

pack_images() {
  mkdir -p "$OUT/images"
  for ref in "${BASE_IMAGES[@]}" "${APP_IMAGES[@]}"; do
    # Сохраняем по тегу БЕЗ digest: после docker load классическое хранилище теряет digest, а тег
    # остаётся. Неизменность образа гарантирует файл .id (offline-load.sh сверяет ID).
    local img=${ref%@sha256:*}
    local f="$OUT/images/$(echo "$img" | tr '/:' '__').tar.gz"
    if [[ $ref != "$img" ]] && docker image inspect "$ref" >/dev/null 2>&1; then
      docker tag "$ref" "$img"                     # уже скачан по digest: вешаем точный тег
    elif [[ $ref != "$img" ]]; then
      retry docker pull "$ref" || { FAILED+=("pull $ref"); continue; }
      docker tag "$ref" "$img"
    fi
    if ! docker image inspect "$img" >/dev/null 2>&1; then
      if [[ ${NO_PULL:-0} == 1 || " ${APP_IMAGES[*]} " == *" $img "* ]]; then log "нет локально, пропускаю: $img"; continue; fi
      retry docker pull "$img" || { FAILED+=("pull $img"); continue; }
    fi
    local id; id=$(docker image inspect -f '{{.Id}}' "$img")
    if [[ -s $f && -f $f.id && $(cat "$f.id") == "$id" ]]; then log "актуален: $f"; continue; fi
    log "docker save $img"
    docker save "$img" | gzip -1 > "$f.part" && mv "$f.part" "$f" && echo "$id" > "$f.id" || FAILED+=("save $img")
  done
}

pack_wheels() {
  local req="$ROOT/ml/requirements.txt"
  [[ -f $req ]] || { log "нет ml/requirements.txt, wheels пропускаю"; return; }
  mkdir -p "$OUT/wheels"
  local pip=(python3 -m pip download --retries 10 --timeout 60 --progress-bar off -d "$OUT/wheels")
  # Бинарные колёса под контейнер (linux x86_64, CPython $PY_VER) — независимо от ОС машины упаковки.
  retry "${pip[@]}" -r "$req" --only-binary=:all: --python-version "$PY_VER" --implementation cp \
    --platform manylinux2014_x86_64 --platform manylinux_2_17_x86_64 --platform manylinux_2_28_x86_64 \
    || FAILED+=("wheels")
  # Инструменты сборки/тестов (на случай sdist и pytest в CI)
  retry "${pip[@]}" --only-binary=:all: --python-version "$PY_VER" --implementation cp \
    --platform manylinux2014_x86_64 pip setuptools wheel pytest ruff || FAILED+=("wheels-tools")
}

pack_nuget() {
  local projs; projs=$(find "$ROOT/api" -name '*.csproj' 2>/dev/null)
  [[ -n $projs ]] || { log "нет *.csproj в api/, nuget пропускаю"; return; }
  mkdir -p "$OUT/nuget"
  # Урок репетиции: глобальный NuGet.Config указывал на недоступный корпоративный фид -> свой конфиг с <clear/>.
  local cfg="$OUT/nuget.pack.config"
  cat > "$cfg" <<CFG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="${MIRROR_NUGET_SOURCE:-https://api.nuget.org/v3/index.json}" />
  </packageSources>
</configuration>
CFG
  for p in $projs; do
    log "dotnet restore $p"
    retry dotnet restore "$p" --packages "$OUT/nuget" --configfile "$cfg" || FAILED+=("nuget $p")
  done
  # Конфиг для офлайн-использования: только локальная папка.
  cat > "$OUT/nuget.offline.config" <<'CFG'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="offline" value="./nuget" />
  </packageSources>
</configuration>
CFG
}

pack_npm() {
  [[ -f $ROOT/frontend/package.json ]] || { log "нет frontend/package.json, npm пропускаю"; return; }
  mkdir -p "$OUT/npm"
  local tmp; tmp=$(mktemp -d)
  cp "$ROOT/frontend/package.json" "$tmp/"; [[ -f $ROOT/frontend/package-lock.json ]] && cp "$ROOT/frontend/package-lock.json" "$tmp/"
  local reg=(); [[ -n ${MIRROR_NPM_REGISTRY:-} ]] && reg=(--registry "$MIRROR_NPM_REGISTRY")
  local cmd=install; [[ -f $tmp/package-lock.json ]] && cmd=ci
  log "npm $cmd в временной копии (кэш -> $OUT/npm)"
  ( cd "$tmp" && retry npm "$cmd" --cache "$OUT/npm" --prefer-offline --no-audit --no-fund \
      --fetch-retries 5 --fetch-retry-mintimeout 20000 --fetch-retry-maxtimeout 120000 "${reg[@]}" ) || FAILED+=("npm")
  # Урок репетиции: npm ci кладёт в кэш платформенные пакеты только для ЭТОЙ машины (glibc), а образ
  # node:alpine — musl. Без @rollup/rollup-linux-x64-musl `npm ci --offline` молча пропускает его и
  # падает уже vite build. Докладываем все linux-x64 варианты (gnu и musl) из lock-файла.
  if [[ -f $tmp/package-lock.json ]]; then
    local p
    for p in $(cd "$tmp" && node -e 'const l=require("./package-lock.json").packages||{};
        for (const [k,v] of Object.entries(l)) if (v.optional && (v.os||[]).includes("linux") && (v.cpu||[]).includes("x64"))
          console.log(k.replace(/^.*node_modules\//,"")+"@"+v.version)'); do
      retry npm cache add "$p" --cache "$OUT/npm" "${reg[@]}" >/dev/null || FAILED+=("npm cache add $p")
    done
  fi
  rm -rf "$tmp"
}

parts=("$@"); (( ${#parts[@]} )) || parts=(images wheels nuget npm)
mkdir -p "$OUT"
for p in "${parts[@]}"; do "pack_$p"; done

( cd "$OUT" && find . -type f ! -name SHA256SUMS ! -name '*.part' -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS ) 2>/dev/null
{ echo "packed: $(date -Is) on $(hostname)"; du -sh "$OUT"/* 2>/dev/null; } > "$OUT/MANIFEST.txt"
cat "$OUT/MANIFEST.txt"
if (( ${#FAILED[@]} )); then log "НЕ УДАЛОСЬ: ${FAILED[*]}"; exit 1; fi
log "OK -> $OUT (скопируйте на USB / в облако: tar -cf offline.tar -C $(dirname "$OUT") $(basename "$OUT"))"
