#!/usr/bin/env bash
# Проверка сети в первые 15 минут: доступность И скорость каждого источника, с прокси и без.
# Урок репетиции: демон Docker ходил в Docker Hub напрямую и получал 502, прокси давал 20–40 КБ/с,
# глобальный NuGet.Config указывал на недоступный корпоративный фид, pip рвал соединения.
#
# Использование: infra/scripts/check-network.sh [--quick]
#   --quick           только доступность, без замера скорости
#   CHECK_TIMEOUT=4   таймаут проверки доступности, с
#   SPEED_TIMEOUT=6   таймаут замера скорости, с (качаем первые 1 МБ файла через Range)
#   PROXY=http://...  прокси для колонки «proxy» (по умолчанию $HTTPS_PROXY/$HTTP_PROXY)
set -uo pipefail
export LC_ALL=${LC_ALL:-C.UTF-8}

CHECK_TIMEOUT=${CHECK_TIMEOUT:-4}
SPEED_TIMEOUT=${SPEED_TIMEOUT:-6}
QUICK=0; [[ "${1:-}" == "--quick" ]] && QUICK=1
PROXY=${PROXY:-${HTTPS_PROXY:-${https_proxy:-${HTTP_PROXY:-${http_proxy:-}}}}}
RANGE="0-1048575"
FAST=1048576   # >= 1 МБ/с: online
SLOW=204800    # <  200 КБ/с: offline (между ними: mirror / прогрев заранее)

command -v curl >/dev/null || { echo "нужен curl"; exit 2; }
command -v jq >/dev/null || command -v python3 >/dev/null || { echo "нужен jq или python3"; exit 2; }

# json_get <expr-jq> <expr-python> : читает JSON из stdin
json_get() {
  if command -v jq >/dev/null; then jq -r "$1" 2>/dev/null
  else python3 -c "import json,sys; d=json.load(sys.stdin); print($2)" 2>/dev/null; fi
}

# c <mode> <curl args...> : curl без прокси (direct) или через $PROXY (proxy)
c() {
  local mode=$1; shift
  if [[ $mode == direct ]]; then curl --noproxy '*' -sS "$@"
  else curl --proxy "$PROXY" -sS "$@"; fi
}

# probe <mode> <url> -> печатает HTTP-код (000 = нет соединения)
probe() {
  c "$1" -o /dev/null -w '%{http_code}' --connect-timeout "$CHECK_TIMEOUT" --max-time "$CHECK_TIMEOUT" "$2" 2>/dev/null || true
}

# speed <mode> <url> [extra curl args] -> байт/с по первому 1 МБ (0 при ошибке)
speed() {
  local mode=$1 url=$2; shift 2
  local out
  out=$(c "$mode" -L -o /dev/null -r "$RANGE" -w '%{http_code} %{speed_download} %{size_download}' \
        --connect-timeout "$CHECK_TIMEOUT" --max-time "$SPEED_TIMEOUT" "$@" "$url" 2>/dev/null) || true
  # При таймауте curl всё равно печатает -w: берём фактическую скорость, если что-то скачалось.
  local sp=${out#* }; sp=${sp%% *}; local size=${out##* }
  if [[ -n "$out" && "${size:-0}" != "0" ]]; then printf '%.0f' "${sp:-0}"; else echo 0; fi
}

# URL ~1 МБ+ блоба в OCI-registry (Docker Hub требует токен, MCR анонимный)
registry_blob() {
  local mode=$1 reg=$2 repo=$3 tag=$4 token_url=${5:-}
  local auth=() token idx digest man layer
  if [[ -n $token_url ]]; then
    token=$(c "$mode" --max-time "$CHECK_TIMEOUT" "$token_url" 2>/dev/null | json_get '.token' 'd["token"]')
    [[ -z $token || $token == null ]] && return 1
    auth=(-H "Authorization: Bearer $token")
  fi
  local acc='application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json'
  idx=$(c "$mode" --max-time "$CHECK_TIMEOUT" "${auth[@]}" -H "Accept: $acc" "https://$reg/v2/$repo/manifests/$tag" 2>/dev/null) || return 1
  digest=$(json_get '[.manifests[]? | select(.platform.architecture=="amd64" and .platform.os=="linux")][0].digest // empty' \
                    'next((m["digest"] for m in d.get("manifests",[]) if m.get("platform",{}).get("architecture")=="amd64"), "")' <<<"$idx")
  if [[ -n $digest ]]; then
    man=$(c "$mode" --max-time "$CHECK_TIMEOUT" "${auth[@]}" -H "Accept: $acc" "https://$reg/v2/$repo/manifests/$digest" 2>/dev/null) || return 1
  else man=$idx; fi
  layer=$(json_get '[.layers[]? | select(.size > 1000000)][0].digest // .layers[0].digest // empty' \
                   'next((l["digest"] for l in d["layers"] if l["size"]>1000000), d["layers"][0]["digest"])' <<<"$man")
  [[ -z $layer ]] && return 1
  BLOB_URL="https://$reg/v2/$repo/blobs/$layer"
  BLOB_AUTH=("${auth[@]}")
}

# Источники: имя | URL доступности | ok-коды | способ получить URL для скорости
NAMES=(DockerHub MCR PyPI npm NuGet GitHub)
declare -A PING=(
  [DockerHub]=https://registry-1.docker.io/v2/
  [MCR]=https://mcr.microsoft.com/v2/
  [PyPI]=https://pypi.org/simple/pip/
  [npm]=https://registry.npmjs.org/react
  [NuGet]=https://api.nuget.org/v3/index.json
  [GitHub]=https://github.com/
)
declare -A FILE=(
  [PyPI]=pypi
  [npm]=https://registry.npmjs.org/typescript/-/typescript-5.6.3.tgz
  [NuGet]=https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg
  [GitHub]=https://github.com/docker/compose/releases/download/v2.29.7/docker-compose-linux-x86_64
)

speed_for() {
  local mode=$1 name=$2 url
  BLOB_AUTH=()
  case $name in
    DockerHub) registry_blob "$mode" registry-1.docker.io library/python 3.11-slim \
                 "https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/python:pull" || { echo 0; return; }
               url=$BLOB_URL ;;
    MCR)       registry_blob "$mode" mcr.microsoft.com dotnet/runtime-deps 8.0 || { echo 0; return; }
               url=$BLOB_URL ;;
    PyPI)      url=$(c "$mode" --max-time "$CHECK_TIMEOUT" https://pypi.org/pypi/pip/24.2/json 2>/dev/null \
                 | json_get '[.urls[] | select(.packagetype=="bdist_wheel")][0].url' \
                            'next(u["url"] for u in d["urls"] if u["packagetype"]=="bdist_wheel")')
               [[ -z $url || $url == null ]] && { echo 0; return; } ;;
    *)         url=${FILE[$name]} ;;
  esac
  speed "$mode" "$url" "${BLOB_AUTH[@]}"
}

pad() { local t=$1; printf '%s%*s' "$t" $(( $2 > ${#t} ? $2 - ${#t} : 0 )) ''; }  # выравнивание по символам, не байтам

human() { # байт/с -> строка
  local b=$1
  if (( b == 0 )); then echo "-"
  elif (( b >= 1048576 )); then awk -v b="$b" 'BEGIN{printf "%.1f МБ/с", b/1048576}'
  else awk -v b="$b" 'BEGIN{printf "%.0f КБ/с", b/1024}'; fi
}

verdict() { # код, скорость -> online/mirror/offline
  local code=$1 sp=$2
  [[ $code == 000 || $code =~ ^5 ]] && { echo offline; return; }
  (( QUICK )) && { echo "ok?"; return; }
  if (( sp >= FAST )); then echo online
  elif (( sp >= SLOW )); then echo mirror
  else echo offline; fi
}

MODES=(direct)
[[ -n $PROXY ]] && MODES+=(proxy)

echo "== Сеть: $(hostname)  $(date '+%F %T')"
echo "   proxy: ${PROXY:-<нет>}   таймауты: ${CHECK_TIMEOUT}s/${SPEED_TIMEOUT}s   порог online ≥ $(human $FAST), offline < $(human $SLOW)"
echo
# printf считает байты, поэтому кириллицу в заголовке выравниваем вручную.
printf 'Источник  '; for m in "${MODES[@]}"; do printf '| %-6s %-4s %-11s %-8s' "$m" code speed verdict; done; echo
printf '%.0s-' {1..80}; echo

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
measure() { # <mode> <name> -> "code speed" в файл; режимы одного источника меряем параллельно
  local code sp=0
  code=$(probe "$1" "${PING[$2]}")
  if (( ! QUICK )) && [[ $code != 000 && ! $code =~ ^5 ]]; then sp=$(speed_for "$1" "$2"); fi
  echo "$code $sp" > "$TMP/$1.$2"
}

declare -A BEST
for n in "${NAMES[@]}"; do
  for m in "${MODES[@]}"; do measure "$m" "$n" & done
  wait
  printf '%-10s' "$n"
  best=offline
  for m in "${MODES[@]}"; do
    read -r code sp < "$TMP/$m.$n"
    v=$(verdict "$code" "$sp")
    printf '| %-6s %-4s %s %-8s' "" "$code" "$(pad "$(human "$sp")" 11)" "$v"
    case $v in online) best=online ;; mirror|ok?) [[ $best == offline ]] && best=$v ;; esac
  done
  BEST[$n]=$best
  echo
done

echo
echo "== Локальные настройки (то, что подвело на репетиции)"
if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
  dp=$(docker info --format '{{.HTTPProxy}}{{if .HTTPSProxy}} https={{.HTTPSProxy}}{{end}}' 2>/dev/null)
  mirrors=$(docker info --format '{{range .RegistryConfig.Mirrors}}{{.}} {{end}}' 2>/dev/null)
  echo "docker daemon: proxy=${dp:-<нет>}  mirrors=${mirrors:-<нет>}  (демон НЕ видит прокси shell!)"
else
  echo "docker daemon: недоступен"
fi
if command -v pip >/dev/null || command -v pip3 >/dev/null; then
  pipcfg=$({ pip3 config list 2>/dev/null || pip config list 2>/dev/null; } | tr '\n' ' ')
  echo "pip config: ${pipcfg:-<по умолчанию>}"
fi
command -v npm >/dev/null && echo "npm registry: $(npm config get registry 2>/dev/null)"
if command -v dotnet >/dev/null; then
  echo "NuGet sources (включённые):"
  dotnet nuget list source --format short 2>/dev/null | awk '$1 ~ /^E/ {print $2}' | while read -r src; do
    [[ -z $src ]] && continue
    if [[ $src =~ ^https?:// ]]; then
      code=$(probe direct "$src"); [[ $code == 000 && -n $PROXY ]] && code=$(probe proxy "$src")
      echo "  $src -> ${code:-000}$([[ ${code:-000} == 000 || ${code:-0} =~ ^5 ]] && echo '  <-- НЕДОСТУПЕН: restore упадёт, уберите source или используйте свой nuget.config с <clear/>')"
    else
      [[ -d $src ]] && echo "  $src (локальный каталог)" || echo "  $src (локальный каталог НЕ существует)"
    fi
  done
fi

echo
echo "== Рекомендация"
for n in "${NAMES[@]}"; do printf '  %-10s %s\n' "$n" "${BEST[$n]}"; done
worst=online
for n in DockerHub PyPI npm NuGet MCR; do
  case ${BEST[$n]} in offline) worst=offline ;; mirror|ok?) [[ $worst == online ]] && worst=mirror ;; esac
done
echo
case $worst in
  online)  echo "ИТОГ: online. Собираем как есть: make up (сборка на VM / в CI)." ;;
  mirror)  echo "ИТОГ: mirror. Сеть есть, но медленная/частичная: зеркала в .env (MIRROR_*), registry-mirrors в daemon.json,"
           echo "       базовые образы и зависимости прогреть заранее (make offline-pack там, где сеть быстрая)." ;;
  offline) echo "ИТОГ: offline. Не ждём сеть: make offline-load (образы + wheels/nuget/npm), затем OFFLINE=1 make up."
           echo "       Если прокси доступен, но медленный, им можно тянуть только мелочь (pip/npm точечно)." ;;
esac
[[ ${BEST[DockerHub]} != online ]] && echo "ВНИМАНИЕ: Docker Hub не online. Проверьте прокси/зеркало ДЕМОНА: sudo infra/scripts/bootstrap-vm.sh docker-proxy"
exit 0
