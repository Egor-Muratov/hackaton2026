# Общие функции для import.sh / smoke.sh (source). Нужны curl и jq (или python3).
API_URL=${API_URL:-http://localhost:5080}
CID=${CID:-smoke-$(date +%s)}
CURL=(curl -sS --noproxy '*' --connect-timeout 5 -H "X-Correlation-Id: $CID")

say()  { printf '[%s] %s\n' "${TAG:-ft}" "$*"; }
fail() { say "FAIL: $*"; exit 1; }

# jget <json> <поле через точку, например .datasetId>
jget() {
  if command -v jq >/dev/null; then jq -r "$2 // empty" <<<"$1"
  else python3 -c 'import json,sys
d=json.loads(sys.argv[1])
for k in sys.argv[2].strip(".").split("."):
    d = d.get(k) if isinstance(d, dict) else None
print("" if d is None else d)' "$1" "$2"; fi
}

# req <METHOD> <path> [json-body] -> тело в $BODY, код в $HTTP_CODE (без subshell, чтобы переменные дожили)
req() {
  local method=$1 path=$2 data=${3:-} out
  local args=(-X "$method" -w '\n%{http_code}' "$API_URL$path")
  [[ -n $data ]] && args+=(-H 'Content-Type: application/json' -d "$data")
  if out=$("${CURL[@]}" "${args[@]}" 2>/dev/null); then
    HTTP_CODE=${out##*$'\n'}; BODY=${out%$'\n'*}
  else HTTP_CODE=000; BODY=""; fi
}

# poll <path> <timeout_s> -> ждёт status done|failed, печатает финальный JSON
poll() {
  local path=$1 timeout=$2 started=$SECONDS st
  while (( SECONDS - started < timeout )); do
    req GET "$path"
    st=$(jget "$BODY" .status 2>/dev/null)
    say "  $path -> HTTP $HTTP_CODE ${st:-?} $(jget "$BODY" .message 2>/dev/null)" >&2
    case $st in done) printf '%s' "$BODY"; return 0 ;; failed) printf '%s' "$BODY"; return 1 ;; esac
    sleep 2
  done
  return 2
}
