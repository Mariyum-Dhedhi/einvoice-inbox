#!/usr/bin/env bash
# End-to-end test of the ERP sync (package 16c) on the real stack: mock ERP -> erp-sync (Go) -> app -> SQL Server.
#
#   1. The first run loads all 10,000 purchase orders (with their vendors and receipts).
#   2. A second run right after reads only the overlap and changes nothing.
#   3. 10 orders change in the ERP: the next run updates exactly those 10.
#   4. 3,000 orders change; the sync is killed halfway; the watermark stays; the next run completes.
#   5. The same with chaos mode at 30 %.
# After every run that completes, the app's ERP records must equal the mock ERP's, line for line (export.sql against
# the mock's _mock/master-data/export).
#
# It runs its own Compose project (einvoice-e2e, own volumes) and removes it at the end. The fixed container names
# would clash with a running stack: stop that first. Needs Docker, curl, openssl, sha256sum and Python 3 ($PYTHON).
# E2E_RESULTS=<dir> saves the run summaries there (tests/erp-sync/results) for the README.
set -euo pipefail
cd "$(dirname "$0")/../.."

PY=${PYTHON:-python3}
HERE=tests/erp-sync
MOCK=http://127.0.0.1:${MOCK_ERP_PORT:-8090}
WORK=$(mktemp -d)
COMPOSE=(docker compose -p einvoice-e2e -f compose.yaml -f tests/erp-sync/compose.e2e.yaml --profile public --profile sync)
RESULTS=()
EXTRA=()   # more settings for the sync runs of a step

if [ -n "$(docker ps -aq --filter name='^einvoice-')" ]; then
  echo "Containers named einvoice-* exist. Stop your stack first: docker compose --profile public down" >&2
  exit 1
fi

# Throwaway secrets for this run only; the shell's values win over any .env.
export MSSQL_SA_PASSWORD="E2e${RANDOM}x#$(openssl rand -hex 6)"
export EINVOICE_APP_PASSWORD="E2e${RANDOM}y#$(openssl rand -hex 6)"
export ERP_CLIENT_ID=einvoice
export ERP_CLIENT_SECRET=$(openssl rand -hex 24)
export SYNC_API_KEY=$(openssl rand -hex 32)
export SYNC_API_KEY_SHA256=$(printf %s "$SYNC_API_KEY" | sha256sum | cut -d' ' -f1)
export AI_ENABLED=false TS_AUTHKEY=

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "---- failed; last app log lines ----" >&2
    "${COMPOSE[@]}" logs --no-color web 2>/dev/null | tail -60 >&2 || true
  fi
  docker rm -f einvoice-erp-sync-e2e >/dev/null 2>&1 || true
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

step() { printf '\n== %s ==\n' "$*"; }

# A query in the app's database (sa, via the SQL Server container), one value or line per row.
sql() {
  docker exec -i einvoice-sqlserver sh -c \
    'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d EInvoice -h -1 -W -i /dev/stdin' |
    tr -d '\r' | sed '/^$/d'
}

audits() { echo "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.AuditEvent WHERE Action = 'PurchaseOrderSyncedFromErp';" | sql; }
watermark() { echo "SET NOCOUNT ON; SELECT CONVERT(varchar(40), Value, 127) FROM dbo.SyncWatermark WHERE Entity = 2;" | sql; }

# The app's ERP records must equal the mock's, line for line.
same_data() {
  curl -fsS "$MOCK/_mock/master-data/export" | tr -d '\r' | sed '/^$/d' | LC_ALL=C sort > "$WORK/erp.txt"
  sql < "$HERE/export.sql" | LC_ALL=C sort > "$WORK/app.txt"
  if ! diff -q "$WORK/erp.txt" "$WORK/app.txt" >/dev/null; then
    echo "The app's data differs from the ERP's ($(wc -l < "$WORK/erp.txt") ERP lines, $(wc -l < "$WORK/app.txt") app lines):" >&2
    diff "$WORK/erp.txt" "$WORK/app.txt" | head -20 >&2
    return 1
  fi
  echo "app equals ERP: $(wc -l < "$WORK/app.txt") lines"
}

change() { curl -fsS -X POST -H 'Content-Type: application/json' -d "{\"purchaseOrders\":$1,\"seed\":$2}" "$MOCK/_mock/master-data/changes" >/dev/null; }
chaos() { curl -fsS -X POST -H 'Content-Type: application/json' -d "{\"share\":$1,\"seed\":7,\"retryAfterSeconds\":1,\"slowMs\":500}" "$MOCK/_mock/chaos" >/dev/null; }

# One run to its end; its log in $WORK/<name>.log; fails unless the run succeeded.
run() {
  local name=$1
  if ! "${COMPOSE[@]}" run --rm --no-deps -e SYNC_INTERVAL=0 -e SYNC_RUN_TIMEOUT=20m "${EXTRA[@]}" erp-sync > "$WORK/$name.log" 2>&1; then
    cat "$WORK/$name.log" >&2
    return 1
  fi
  "$PY" "$HERE/summary.py" "$WORK/$name.log" | tee "$WORK/$name.txt"
  RESULTS+=("$name")
}

expect() { "$PY" "$HERE/summary.py" "$WORK/$1.log" "${@:2}"; }

# Starts a run, waits until it has updated at least $2 orders beyond $1 audit events, and kills it.
kill_halfway() {
  local before=$1 at=$2
  "${COMPOSE[@]}" run -d --name einvoice-erp-sync-e2e --no-deps -e SYNC_INTERVAL=0 -e SYNC_RUN_TIMEOUT=20m "${EXTRA[@]}" erp-sync >/dev/null
  for _ in $(seq 1 600); do
    [ "$(audits)" -ge $((before + at)) ] && break
    sleep 0.5
  done
  docker kill einvoice-erp-sync-e2e >/dev/null
  local done_now=$(( $(audits) - before ))
  docker rm -f einvoice-erp-sync-e2e >/dev/null
  echo "killed after $done_now order updates"
  [ "$done_now" -ge "$at" ] && [ "$done_now" -lt 3000 ]
}

step "Start the stack (app, SQL Server, mock ERP)"
"${COMPOSE[@]}" build -q erp-sync
"${COMPOSE[@]}" up -d --build --wait web mock-erp >/dev/null
for _ in $(seq 1 60); do curl -fsS "$MOCK/_mock/stats" >/dev/null 2>&1 && break; sleep 1; done

step "1. First run: the full load"
run full-load
expect full-load purchase-orders read=10000 created=10000 failed=0 outcome=ok
expect full-load vendors created=250 failed=0
expect full-load goods-receipts failed=0 outcome=ok
same_data

step "2. A run right after: only the overlap, nothing changes"
run rerun
for entity in vendors purchase-orders goods-receipts; do expect rerun "$entity" created=0 updated=0 failed=0 read\<100; done
same_data

step "3. 10 orders change in the ERP: exactly those 10 are updated"
change 10 1
run ten-changes
expect ten-changes purchase-orders updated=10 created=0 failed=0
expect ten-changes goods-receipts updated=0 created=0
same_data

step "4. 3,000 orders change; the sync is killed halfway; the next run completes"
change 3000 2
mark=$(watermark)
kill_halfway "$(audits)" 1000
[ "$(watermark)" = "$mark" ] || { echo "the watermark moved during a killed run" >&2; exit 1; }
run after-kill
expect after-kill purchase-orders outcome=ok failed=0 updated\<3000 unchanged\>0
same_data

step "5. The same with chaos mode at 30 %"
# Pages of 100, so the ERP is asked often enough for chaos to matter; the mock's counts start from zero here.
EXTRA=(-e SYNC_PAGE_SIZE=100)
curl -fsS -X POST "$MOCK/_mock/reset" >/dev/null
chaos 0.3
change 3000 3
mark=$(watermark)
kill_halfway "$(audits)" 1000
[ "$(watermark)" = "$mark" ] || { echo "the watermark moved during a killed run" >&2; exit 1; }
run after-kill-chaos
expect after-kill-chaos purchase-orders outcome=ok failed=0
curl -fsS "$MOCK/_mock/stats" | tee "$WORK/chaos-stats.json"; echo
chaos 0
EXTRA=()
same_data

step "Passed"
if [ -n "${E2E_RESULTS:-}" ]; then
  mkdir -p "$E2E_RESULTS"
  file="$E2E_RESULTS/$(date -u +%Y-%m-%d)-$(git rev-parse --short HEAD).md"
  {
    echo "# ERP sync E2E: $(date -u +%Y-%m-%dT%H:%MZ), commit $(git rev-parse --short HEAD)"
    echo
    echo "Machine: ${E2E_MACHINE:-$(uname -sm)}. Workers 4, page size 1000. Each run's summary lines (tests/erp-sync/e2e.sh):"
    for name in "${RESULTS[@]}"; do
      echo; echo "## $name"; echo; echo '```'; cat "$WORK/$name.txt"; echo '```'
    done
    echo; echo "Chaos run (step 5, pages of 100): the mock ERP's counts for that step:"; echo; echo '```'; cat "$WORK/chaos-stats.json"; echo; echo '```'
  } > "$file"
  echo "saved $file"
fi
