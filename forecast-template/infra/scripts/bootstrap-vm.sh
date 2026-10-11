#!/usr/bin/env bash
# Подготовка VM (Ubuntu 22.04/24.04) под compose-стек. Идемпотентный: повторный запуск ничего не ломает.
# Любая загрузка идёт с ретраями. Шаги можно запускать по отдельности.
#
#   sudo infra/scripts/bootstrap-vm.sh               # всё: base docker docker-proxy user app offline check
#   sudo infra/scripts/bootstrap-vm.sh docker-proxy  # только прокси/зеркало для демона Docker
#
# Переменные:
#   HTTPS_PROXY/HTTP_PROXY/NO_PROXY  прокси: прописывается в apt, systemd-drop-in dockerd, /etc/environment
#   REGISTRY_MIRROR                  https://mirror.example (registry-mirrors в daemon.json)
#   APP_USER=${SUDO_USER}            кого добавить в группу docker и кому отдать APP_DIR
#   APP_DIR=/opt/forecast            куда кладётся репозиторий (git clone REPO_URL или заливка из CI)
#   REPO_URL                         если задан и APP_DIR пуст: git clone
#   OFFLINE_DIR=$APP_DIR/offline     если там есть images/*.tar(.gz) или debs/: офлайн-установка
set -euo pipefail

STEPS_ALL=(base docker docker-proxy user app offline check)
APP_USER=${APP_USER:-${SUDO_USER:-$(id -un)}}
APP_DIR=${APP_DIR:-/opt/forecast}
OFFLINE_DIR=${OFFLINE_DIR:-$APP_DIR/offline}
PROXY=${HTTPS_PROXY:-${https_proxy:-${HTTP_PROXY:-${http_proxy:-}}}}
NOPROXY=${NO_PROXY:-${no_proxy:-localhost,127.0.0.1,::1,db,api,ml,frontend}}

log()  { printf '\n[bootstrap] == %s\n' "$*"; }
info() { printf '[bootstrap] %s\n' "$*"; }
die()  { printf '[bootstrap] ОШИБКА: %s\n' "$*" >&2; exit 1; }

# retry <попыток> <cmd...>: экспоненциальная пауза 5, 10, 20...
retry() {
  local max=$1; shift; local n=1 delay=5
  until "$@"; do
    (( n >= max )) && { info "не удалось после $n попыток: $*"; return 1; }
    info "попытка $n не удалась, повтор через ${delay}s: $*"; sleep "$delay"
    n=$((n+1)); delay=$((delay*2))
  done
}
apt_get() { retry 4 env DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=3 "$@"; }
dl()      { retry 4 curl -fsSL --connect-timeout 15 --retry 3 --retry-delay 3 "$@"; }

need_root() { [[ $EUID -eq 0 ]] || die "нужен root: sudo $0 $*"; }

step_base() {
  log "base: пакеты"
  . /etc/os-release
  [[ ${ID:-} == ubuntu ]] || info "ВНИМАНИЕ: ожидали Ubuntu, а это ${PRETTY_NAME:-?}"
  local free; free=$(df -Pm / | awk 'NR==2{print $4}')
  (( free > 15000 )) || info "ВНИМАНИЕ: свободно ${free} МБ на /, рекомендуется ≥ 15 ГБ"
  if [[ -n $PROXY ]]; then
    printf 'Acquire::http::Proxy "%s";\nAcquire::https::Proxy "%s";\n' "$PROXY" "$PROXY" > /etc/apt/apt.conf.d/95proxy
    grep -q '^HTTPS_PROXY=' /etc/environment || cat >> /etc/environment <<EOF
HTTP_PROXY=$PROXY
HTTPS_PROXY=$PROXY
NO_PROXY=$NOPROXY
http_proxy=$PROXY
https_proxy=$PROXY
no_proxy=$NOPROXY
EOF
  fi
  local pkgs=(ca-certificates curl git make jq unzip gnupg openssl rsync)
  local missing=(); for p in "${pkgs[@]}"; do dpkg -s "$p" >/dev/null 2>&1 || missing+=("$p"); done
  if (( ${#missing[@]} )); then
    if compgen -G "$OFFLINE_DIR/debs/*.deb" >/dev/null; then apt_get install -y "$OFFLINE_DIR"/debs/*.deb
    else apt_get update -q && apt_get install -y -q "${missing[@]}"; fi
  else info "все пакеты уже стоят"; fi
}

step_docker() {
  log "docker: Docker CE + compose plugin"
  if command -v docker >/dev/null && docker compose version >/dev/null 2>&1; then
    info "уже установлен: $(docker --version), $(docker compose version --short)"
  elif compgen -G "$OFFLINE_DIR/debs/docker/*.deb" >/dev/null; then
    apt_get install -y "$OFFLINE_DIR"/debs/docker/*.deb
  else
    . /etc/os-release
    install -m 0755 -d /etc/apt/keyrings
    [[ -s /etc/apt/keyrings/docker.asc ]] || dl https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
      > /etc/apt/sources.list.d/docker.list
    apt_get update -q
    apt_get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  fi
  systemctl enable --now docker >/dev/null 2>&1 || true
}

step_docker_proxy() {
  log "docker-proxy: прокси и зеркала для ДЕМОНА (shell-прокси он не видит)"
  local changed=0
  if [[ -n $PROXY ]]; then
    mkdir -p /etc/systemd/system/docker.service.d
    local f=/etc/systemd/system/docker.service.d/http-proxy.conf
    local want
    want=$(printf '[Service]\nEnvironment="HTTP_PROXY=%s" "HTTPS_PROXY=%s" "NO_PROXY=%s"\n' "$PROXY" "$PROXY" "$NOPROXY")
    if [[ ! -f $f || "$(cat "$f")" != "$want" ]]; then echo "$want" > "$f"; changed=1; info "записан $f"; fi
  fi
  # daemon.json: ротация логов + registry-mirrors (сливаем с существующим через jq)
  local dj=/etc/docker/daemon.json tmp; mkdir -p /etc/docker; [[ -s $dj ]] || echo '{}' > "$dj"
  tmp=$(mktemp)
  jq --arg m "${REGISTRY_MIRROR:-}" '
      .["log-driver"] //= "json-file"
    | .["log-opts"] //= {"max-size":"20m","max-file":"5"}
    | if $m != "" then .["registry-mirrors"] = ((.["registry-mirrors"] // []) + [$m] | unique) else . end' "$dj" > "$tmp"
  if ! cmp -s "$tmp" "$dj"; then cp "$dj" "$dj.bak.$(date +%s)"; mv "$tmp" "$dj"; changed=1; info "обновлён $dj"; else rm -f "$tmp"; fi
  if (( changed )); then systemctl daemon-reload; systemctl restart docker; info "dockerd перезапущен"; fi
  docker info --format 'daemon: proxy={{.HTTPSProxy}} mirrors={{.RegistryConfig.Mirrors}}' || true
}

step_user() {
  log "user: $APP_USER в группе docker"
  id -nG "$APP_USER" | grep -qw docker && info "уже в группе" || { usermod -aG docker "$APP_USER"; info "добавлен (перелогиньтесь)"; }
}

step_app() {
  log "app: каталог $APP_DIR и .env"
  mkdir -p "$APP_DIR"
  if [[ -n ${REPO_URL:-} && ! -d $APP_DIR/.git ]] && [[ -z "$(ls -A "$APP_DIR" 2>/dev/null)" ]]; then
    retry 4 git clone "$REPO_URL" "$APP_DIR"
  fi
  chown -R "$APP_USER": "$APP_DIR"
  mkdir -p "$APP_DIR/data" "$APP_DIR/offline"/{wheels,nuget,npm,images}
  if [[ ! -f $APP_DIR/.env ]]; then
    if [[ -f $APP_DIR/.env.example ]]; then
      local pg app ml
      pg=$(openssl rand -hex 16); app=$(openssl rand -hex 16); ml=$(openssl rand -hex 16)
      sed -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$pg|" \
          -e "s|^APP_DB_PASSWORD=.*|APP_DB_PASSWORD=$app|" \
          -e "s|^ML_DB_PASSWORD=.*|ML_DB_PASSWORD=$ml|" \
          -e "s|^DATA_HOST_DIR=.*|DATA_HOST_DIR=$APP_DIR/data|" "$APP_DIR/.env.example" > "$APP_DIR/.env"
      chmod 600 "$APP_DIR/.env"; chown "$APP_USER": "$APP_DIR/.env"
      info ".env создан со случайными паролями"
    else info "нет .env.example (код ещё не залит), .env создадите позже: повторите шаг app"; fi
  else info ".env уже есть, не трогаю"; fi
  chown -R "$APP_USER": "$APP_DIR"
}

step_offline() {
  log "offline: загрузка запаса из $OFFLINE_DIR (если есть)"
  if [[ -x $APP_DIR/infra/scripts/offline-load.sh ]] && compgen -G "$OFFLINE_DIR/images/*.tar*" >/dev/null; then
    OFFLINE_DIR=$OFFLINE_DIR "$APP_DIR/infra/scripts/offline-load.sh"
  else info "офлайн-образов нет, пропускаю"; fi
}

step_check() {
  log "check"
  docker --version; docker compose version
  if [[ -x $APP_DIR/infra/scripts/check-network.sh ]]; then
    CHECK_TIMEOUT=3 SPEED_TIMEOUT=5 "$APP_DIR/infra/scripts/check-network.sh" || true
  fi
  info "дальше (от $APP_USER): cd $APP_DIR && make up && make smoke API_URL=http://localhost"
}

main() {
  need_root "$@"
  local steps=("$@"); (( ${#steps[@]} )) || steps=("${STEPS_ALL[@]}")
  for s in "${steps[@]}"; do
    case $s in
      base|docker|user|app|offline|check) "step_$s" ;;
      docker-proxy) step_docker_proxy ;;
      *) die "неизвестный шаг $s (есть: ${STEPS_ALL[*]})" ;;
    esac
  done
  log "готово"
}
main "$@"
