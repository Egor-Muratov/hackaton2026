#!/usr/bin/env bash
# Печатает VAR=образ для docker compose (используется в Makefile: env $(images-env.sh) docker compose ...).
# Онлайн (OFFLINE!=1) — ничего не печатает: compose берёт тег@digest из своих значений по умолчанию (= images.lock).
# Офлайн (OFFLINE=1)  — тег БЕЗ digest: после docker save/load классическое хранилище Docker теряет digest
#   (RepoDigests=[]), и FROM тег@digest пошёл бы в сеть. Неизменность образа вместо digest гарантирует
#   offline-load.sh: сверяет ID загруженного образа с ID, записанным при offline-pack.
set -euo pipefail
cd "$(dirname "$0")/../.."
off=${OFFLINE:-$( { grep -sh '^OFFLINE=' .env || true; } | tail -1 | cut -d= -f2)}
[[ ${off:-0} == 1 ]] || exit 0
grep -E '^[A-Z_]+_IMAGE=' infra/images.lock | sed 's/@sha256:[0-9a-f]*$//' | tr '\n' ' '
