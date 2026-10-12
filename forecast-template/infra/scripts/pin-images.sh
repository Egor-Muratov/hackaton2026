#!/usr/bin/env bash
# Фиксирует базовые образы: точный тег -> digest мультиархитектурного индекса.
# Вход:  infra/images.txt  (VAR=repo:точный-тег, комментарии через #)
# Выход: infra/images.lock (VAR=repo:тег@sha256:...) — единственный источник правды для
#        Dockerfile (ARG), compose, CI и offline-pack. Проверка расхождений: make check-images.
# Запускать там, где есть сеть до registry. Ретраи на каждый запрос (урок репетиции).
set -euo pipefail
cd "$(dirname "$0")/.."
IN=images.txt; OUT=images.lock; TMP=$(mktemp)
ACCEPT='application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.docker.distribution.manifest.v2+json,application/vnd.oci.image.manifest.v1+json'

curl_r() { curl -fsS --retry 5 --retry-all-errors --retry-delay 3 --max-time 30 "$@"; }

digest() {  # $1 = repo:tag  ->  sha256:...
  local ref=$1 repo=${1%:*} tag=${1##*:} host path token=""
  case $repo in
    mcr.microsoft.com/*) host=mcr.microsoft.com; path=${repo#mcr.microsoft.com/} ;;
    */*)                 host=registry-1.docker.io; path=$repo ;;
    *)                   host=registry-1.docker.io; path=library/$repo ;;
  esac
  if [[ $host == registry-1.docker.io ]]; then
    token=$(curl_r "https://auth.docker.io/token?service=registry.docker.io&scope=repository:$path:pull" \
            | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
  fi
  curl_r -I -H "Accept: $ACCEPT" ${token:+-H "Authorization: Bearer $token"} \
    "https://$host/v2/$path/manifests/$tag" | tr -d '\r' | awk -F': ' 'tolower($1)=="docker-content-digest"{print $2}'
}

{
  echo "# СГЕНЕРИРОВАНО infra/scripts/pin-images.sh $(date -u +%F) из images.txt — не править руками."
  while IFS= read -r line; do
    [[ $line =~ ^[[:space:]]*(#|$) ]] && continue
    var=${line%%=*}; ref=${line#*=}
    d=$(digest "$ref"); [[ $d == sha256:* ]] || { echo "не удалось получить digest для $ref" >&2; exit 1; }
    echo "$var=$ref@$d"; echo "  $var=$ref@${d:0:19}…" >&2
  done < "$IN"
} > "$TMP"
mv "$TMP" "$OUT"; echo "записано: infra/$OUT" >&2
