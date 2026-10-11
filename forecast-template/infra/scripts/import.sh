#!/usr/bin/env bash
# Импорт репетиционных данных: train.csv (новый датасет), затем test.csv в тот же датасет.
# Пути относительны Import__Root (локально hackaton/data, в compose /data).
# Использование: API_URL=http://localhost infra/scripts/import.sh [train-path] [test-path]
set -euo pipefail
TAG=import; source "$(dirname "$0")/lib.sh"
TRAIN=${1:-${IMPORT_TRAIN:-store-sales/train.csv}}
TEST=${2:-${IMPORT_TEST:-store-sales/test.csv}}
TIMEOUT=${IMPORT_TIMEOUT:-1200}

say "API $API_URL, train=$TRAIN"
req POST /api/datasets/import "{\"path\":\"$TRAIN\",\"name\":\"store-sales\"}"; body=$BODY
[[ $HTTP_CODE == 202 ]] || fail "импорт train: HTTP $HTTP_CODE $body"
ds=$(jget "$body" .datasetId); job=$(jget "$body" .jobId)
say "datasetId=$ds jobId=$job"
res=$(poll "/api/jobs/$job" "$TIMEOUT") || fail "импорт train не завершился: $res"
say "train: rows=$(jget "$res" .rows) series=$(jget "$res" .seriesCount) $(jget "$res" .dateFrom)..$(jget "$res" .dateTo)"

if [[ -n $TEST && $TEST != none ]]; then
  req POST /api/datasets/import "{\"path\":\"$TEST\",\"datasetId\":\"$ds\"}"; body=$BODY
  [[ $HTTP_CODE == 202 ]] || fail "импорт test: HTTP $HTTP_CODE $body"
  res=$(poll "/api/jobs/$(jget "$body" .jobId)" "$TIMEOUT") || fail "импорт test не завершился: $res"
  say "test: rows=$(jget "$res" .rows)"
fi
say "OK datasetId=$ds"
echo "$ds" > "$(dirname "$0")/../../.run/last-dataset-id" 2>/dev/null || true
