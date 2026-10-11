#!/usr/bin/env bash
# Сквозная проверка: health -> meta -> POST /api/forecasts -> опрос -> latest/series (all/all).
# Использование: API_URL=http://<vm> infra/scripts/smoke.sh [--health-only]
# Нет датасета -> подсказка `make import` и код 1 (кроме --health-only).
set -euo pipefail
TAG=smoke; source "$(dirname "$0")/lib.sh"
HORIZON=${SMOKE_HORIZON:-16}
TIMEOUT=${SMOKE_TIMEOUT:-600}
HEALTH_RETRIES=${HEALTH_RETRIES:-30}

say "API $API_URL (X-Correlation-Id: $CID)"
for i in $(seq 1 "$HEALTH_RETRIES"); do
  req GET /api/health; h=$BODY
  [[ $HTTP_CODE == 200 ]] && break
  say "  /api/health -> $HTTP_CODE, жду ($i/$HEALTH_RETRIES)"; sleep 2
done
[[ $HTTP_CODE == 200 ]] || fail "/api/health = $HTTP_CODE $h"
say "health: status=$(jget "$h" .status) db=$(jget "$h" .checks.db) ml=$(jget "$h" .checks.ml) model=$(jget "$h" .modelVersion)"
[[ ${1:-} == --health-only ]] && { say "OK (health-only)"; exit 0; }

req GET /api/meta; m=$BODY
[[ $HTTP_CODE == 200 ]] || fail "/api/meta = $HTTP_CODE $m"
ds=$(jget "$m" .latestDatasetId)
say "meta: dataset=${ds:-<нет>} series=$(jget "$m" .seriesCount) history=$(jget "$m" .historyFrom)..$(jget "$m" .historyTo)"
[[ -n $ds ]] || fail "датасетов нет: сначала make import"

req POST /api/forecasts "{\"datasetId\":\"$ds\",\"horizon\":$HORIZON}"; f=$BODY
[[ $HTTP_CODE == 202 ]] || fail "POST /api/forecasts = $HTTP_CODE $f"
run=$(jget "$f" .runId); say "runId=$run"
r=$(poll "/api/forecasts/$run" "$TIMEOUT") || fail "прогон не done: $r"
say "run done: model=$(jget "$r" .modelVersion) rmsle=$(jget "$r" .metrics.rmsle) baseline=$(jget "$r" .metrics.baselineRmsle)"

req GET "/api/forecasts/latest/series?store=all&family=all&horizon=$HORIZON&historyDays=60"; s=$BODY
[[ $HTTP_CODE == 200 ]] || fail "latest/series = $HTTP_CODE $s"
if command -v jq >/dev/null; then
  nf=$(jq '.forecast | length' <<<"$s"); nh=$(jq '.history | length' <<<"$s")
  say "series all/all: history=$nh forecast=$nf"
  [[ $nf == "$HORIZON" ]] || fail "ожидали $HORIZON точек прогноза, пришло $nf"
fi
say "OK end-to-end"
