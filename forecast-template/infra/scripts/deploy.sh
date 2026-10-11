#!/usr/bin/env bash
# Деплой на VM по ssh (используется и из Makefile, и из .gitlab-ci.yml).
#
# Переменные:
#   VM_HOST, VM_USER          куда (обязательно); VM_PORT=22; VM_DIR=/opt/forecast
#   SSH_KEY_FILE              ключ (в CI: файл из переменной SSH_PRIVATE_KEY)
#   SYNC=git|tar              git: на VM `git pull --ff-only` (VM нужен доступ к репо);
#                             tar: заливаем рабочее дерево из текущего каталога (git archive HEAD), VM в GitLab не ходит
#   BUILD_MODE=vm|registry    vm: `compose build` на VM; registry: `compose pull` из $REGISTRY_PREFIX
#   IMAGE_TAG                 тег образов (по умолчанию короткий sha HEAD); пишется в .env на VM => откат: IMAGE_TAG=<old> make deploy
#   ACTION=deploy|build|up    build: только синхронизация + сборка (стадия build в CI); up: только запуск
#   ENV_FILE                  локальный файл, который станет .env на VM (иначе используется уже лежащий там)
set -euo pipefail

: "${VM_HOST:?VM_HOST не задан}"; : "${VM_USER:?VM_USER не задан}"
VM_PORT=${VM_PORT:-22}
VM_DIR=${VM_DIR:-/opt/forecast}
SYNC=${SYNC:-git}
BUILD_MODE=${BUILD_MODE:-vm}
ACTION=${ACTION:-deploy}
IMAGE_TAG=${IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || echo local)}
GIT_BRANCH=${GIT_BRANCH:-main}

SSH=(ssh -p "$VM_PORT" -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15
     -o ServerAliveInterval=30)
[[ -n ${SSH_KEY_FILE:-} ]] && SSH+=(-i "$SSH_KEY_FILE")
TARGET="$VM_USER@$VM_HOST"
say() { printf '[deploy] %s\n' "$*"; }

say "$TARGET:$VM_DIR  action=$ACTION sync=$SYNC build=$BUILD_MODE tag=$IMAGE_TAG"

if [[ $ACTION != up ]]; then
  if [[ $SYNC == tar ]]; then
    say "заливаю код (git archive HEAD)"
    git archive --format=tar HEAD | "${SSH[@]}" "$TARGET" \
      "mkdir -p '$VM_DIR' && tar -x -C '$VM_DIR' --overwrite"
  else
    say "git pull на VM"
    "${SSH[@]}" "$TARGET" "set -e; cd '$VM_DIR' && git fetch --prune origin && git checkout -q '$GIT_BRANCH' && git pull --ff-only origin '$GIT_BRANCH'"
  fi
fi

if [[ -n ${ENV_FILE:-} && -f ${ENV_FILE} ]]; then
  say "обновляю .env на VM (из $ENV_FILE)"
  "${SSH[@]}" "$TARGET" "umask 077; cat > '$VM_DIR/.env'" < "$ENV_FILE"
fi

if [[ $BUILD_MODE == registry && $ACTION != up && -n ${CI_REGISTRY_USER:-} ]]; then
  say "docker login $CI_REGISTRY на VM (пароль через stdin)"
  printf '%s' "$CI_REGISTRY_PASSWORD" | "${SSH[@]}" "$TARGET" "docker login -u '$CI_REGISTRY_USER' --password-stdin '$CI_REGISTRY' >/dev/null"
fi

# Удалённая часть: всё в одном ssh, с ретраями на pull/build (медленная сеть).
"${SSH[@]}" "$TARGET" bash -s -- "$VM_DIR" "$ACTION" "$BUILD_MODE" "$IMAGE_TAG" "${REGISTRY_PREFIX:-}" <<'REMOTE'
set -euo pipefail
VM_DIR=$1 ACTION=$2 BUILD_MODE=$3 IMAGE_TAG=$4 PREFIX=$5
cd "$VM_DIR"
[[ -f .env ]] || { echo "[vm] нет $VM_DIR/.env: запустите bootstrap-vm.sh app или передайте ENV_FILE"; exit 1; }
setenv() { grep -q "^$1=" .env && sed -i "s|^$1=.*|$1=$2|" .env || echo "$1=$2" >> .env; }
setenv IMAGE_TAG "$IMAGE_TAG"
[[ -n $PREFIX ]] && setenv REGISTRY_PREFIX "$PREFIX"
mkdir -p offline/wheels offline/nuget offline/npm
DC="docker compose --env-file .env -f infra/docker-compose.yml"
retry() { local n=0; until "$@"; do n=$((n+1)); (( n >= 3 )) && return 1; echo "[vm] повтор $n: $*"; sleep $((n*10)); done; }

if [[ $ACTION != up ]]; then
  if [[ $BUILD_MODE == registry ]]; then
    retry $DC pull api ml frontend
    retry $DC pull --ignore-pull-failures db nginx || true
  else
    retry $DC build api ml frontend
  fi
fi
if [[ $ACTION != build ]]; then
  $DC up -d --no-build --remove-orphans
  $DC ps
  port=$(sed -n 's/^HTTP_PORT=//p' .env | tail -1); port=${port:-80}
  for i in $(seq 1 30); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$port/api/health" || true)
    [[ $code == 200 ]] && { echo "[vm] /api/health 200"; exit 0; }
    sleep 3
  done
  echo "[vm] /api/health не 200 (последний код $code)"; $DC logs --tail 50 api nginx; exit 1
fi
REMOTE
say "готово"
