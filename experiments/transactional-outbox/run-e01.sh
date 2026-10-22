#!/usr/bin/env bash
# E01 Lost Event: scripted, reproducible run. LOCAL LAB ONLY.
#
#   A. naive dual write (UNSAFE baseline) + kill after COMMIT  -> work order without event
#   B. transactional outbox + broker down + kill after COMMIT   -> event published after restart
#   C. transactional outbox + failure before COMMIT              -> no work order, no outbox row
#
# Starts from a clean environment (docker compose down -v) and tears everything down at the
# end. Set KEEP=1 to leave the stack running for inspection.
#
# Requires: docker (with compose v2), curl, jq.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE=(docker compose -f "$HERE/compose.yaml")
API="http://127.0.0.1:${DRLAB_E01_API_PORT:-18480}"
MGMT="http://127.0.0.1:${DRLAB_RABBITMQ_UI_PORT:-15682}"
QUEUE="e01.work-order-created"
KEEP="${KEEP:-0}"

for tool in docker curl jq; do
  command -v "$tool" >/dev/null || { echo "missing prerequisite: $tool" >&2; exit 2; }
done

if [[ -t 1 ]]; then B=$'\e[1m'; DIM=$'\e[2m'; RED=$'\e[31m'; GRN=$'\e[32m'; YEL=$'\e[33m'; R=$'\e[0m'; else B= DIM= RED= GRN= YEL= R=; fi

section() { printf '\n%s%s\n%s%s\n' "$B" "================================================================================" "$1" "$R"; }
step()    { printf '\n%s--- %s%s\n' "$B" "$1" "$R"; }
cmd()     { printf '%s$ %s%s\n' "$DIM" "$*" "$R"; }
note()    { printf '%s%s%s\n' "$YEL" "$*" "$R"; }

MISMATCHES=()
check() { # description expected observed
  if [[ "$2" == "$3" ]]; then printf '  %sOK%s   %-62s expected=%s observed=%s\n' "$GRN" "$R" "$1" "$2" "$3"
  else printf '  %sDIFF%s %-62s expected=%s observed=%s\n' "$RED" "$R" "$1" "$2" "$3"; MISMATCHES+=("$1"); fi
}

# ---- helpers ------------------------------------------------------------------------------
psql_lab() { "${COMPOSE[@]}" exec -T postgres psql -U lab -d lab -v ON_ERROR_STOP=1 -P pager=off "$@"; }
sql()      { cmd "psql: $1"; psql_lab -c "$1"; }
sql_value(){ psql_lab -tA -c "$1"; }

start_api() { # mode
  cmd "E01_MODE=$1 docker compose up -d --no-deps workorders"
  E01_MODE="$1" "${COMPOSE[@]}" up -d --no-deps workorders 2>&1 | sed 's/^/    /'
  for _ in $(seq 1 60); do
    if curl -fsS "$API/health" >/dev/null 2>&1; then
      cmd "curl $API/health"; curl -sS "$API/health"; echo; return 0
    fi
    sleep 0.5
  done
  echo "API did not become healthy" >&2; "${COMPOSE[@]}" logs workorders >&2; exit 1
}

post_work_order() { # operation_id [fault]
  local args=(-sS -X POST "$API/work-orders" -H 'Content-Type: application/json'
              -H "X-Operation-Id: $1" -d "{\"title\":\"Inspect pump P-101 ($1)\"}" -w '\nHTTP %{http_code}\n')
  local shown="curl -X POST $API/work-orders -H 'X-Operation-Id: $1'"
  if [[ -n "${2:-}" ]]; then args+=(-H "X-Fault-Inject: $2"); shown+=" -H 'X-Fault-Inject: $2'"; fi
  cmd "$shown"
  curl "${args[@]}" || note "curl exit $?: the connection dropped, the client does not know the outcome"
}

workorders_container() { "${COMPOSE[@]}" ps -a -q workorders; }

wait_for_exit() {
  local id; id="$(workorders_container)"
  for _ in $(seq 1 40); do
    [[ "$(docker inspect -f '{{.State.Status}}' "$id")" == exited ]] && break
    sleep 0.25
  done
  WORKORDERS_EXIT="$(docker inspect -f '{{.State.ExitCode}}' "$id")"
  cmd "docker compose ps -a workorders"
  printf '    workorders: %s (exit code %s)\n' "$(docker inspect -f '{{.State.Status}}' "$id")" "$WORKORDERS_EXIT"
}

broker_running() { [[ -n "$("${COMPOSE[@]}" ps -q --status running rabbitmq)" ]]; }

queue_depth() { # authoritative count from the queue process (management stats can lag)
  "${COMPOSE[@]}" exec -T rabbitmq rabbitmqctl list_queues -q --no-table-headers name messages \
    | awk -v q="$QUEUE" '$1 == q { print $2; found=1 } END { if (!found) print 0 }'
}

peek_queue() { # non-destructive: messages are requeued
  curl -sS -u lab:lab -H 'content-type: application/json' -X POST "$MGMT/api/queues/%2F/$QUEUE/get" \
       -d '{"count":100,"ackmode":"ack_requeue_true","encoding":"auto"}' | jq 'if type == "array" then . else [] end'
}

show_broker() {
  if ! broker_running; then cmd "rabbitmqctl list_queues"; echo "    broker: STOPPED (unreachable)"; return; fi
  cmd "rabbitmqctl list_queues name messages  |  management API: POST /api/queues/%2F/$QUEUE/get (requeue)"
  printf '    queue %s: %s message(s)\n' "$QUEUE" "$(queue_depth)"
  peek_queue | jq -r '.[] | (.payload | fromjson) as $p
    | "      message_id=\(.properties.message_id)  type=\(.properties.type)  operation_id=\($p.operation_id)  work_order_id=\($p.work_order_id)"'
}

# Joins what PostgreSQL says happened with what the broker actually holds.
reconcile() {
  local ids="ARRAY[]::uuid[]"
  if broker_running; then
    ids="$(peek_queue | jq -r '[.[] | .payload | fromjson | "'"'"'\(.work_order_id)'"'"'"] | "ARRAY[" + join(",") + "]::uuid[]"')"
  fi
  local event_col="CASE WHEN w.id = ANY($ids) THEN 'yes' ELSE 'NO  <-- no event' END"
  broker_running || event_col="'(broker stopped)'"
  sql "SELECT w.operation_id, w.write_mode,
              CASE WHEN o.event_id IS NULL THEN '(none)' WHEN o.published_at IS NULL THEN 'PENDING' ELSE 'published' END AS outbox_entry,
              $event_col AS event_in_broker
       FROM work_orders w LEFT JOIN outbox_messages o ON o.aggregate_id = w.id
       ORDER BY w.created_at"
}

count_in_broker() { # operation_id
  peek_queue | jq --arg op "$1" '[.[] | select((.payload | fromjson | .operation_id) == $op)] | length'
}

show_logs() { # optional operation_id filter
  cmd "docker compose logs workorders | jq 'select(.phase)'${1:+ (operation_id=$1)}"
  "${COMPOSE[@]}" logs --no-log-prefix workorders 2>/dev/null \
    | jq -rR --arg op "${1:-}" 'fromjson? | select(.phase) | select($op == "" or .operation_id == $op)
        | ([ (if .phase == "startup" then "mode=\(.mode)" + (if .level == "warn" then " UNSAFE BASELINE" else "" end) else empty end),
             (.fault       // empty | "fault=\(.)"),
             (.count       // empty | "count=\(.)"),
             (.attempt     // empty | "attempt=\(.)"),
             (.retry_in_ms // empty | "retry_in_ms=\(.)") ] | join(" ")) as $detail
        | [.ts[11:23], .level, .phase, (.operation_id // "-"), (.event_id // "-"), $detail] | @tsv' \
    | awk -F'\t' '{ printf "    %s  %-8s %-24s %-22s %-37s %s\n", $1, $2, $3, $4, $5, $6 }'
}

wait_for_phase() { # phase: wait until the workorders logs contain it
  for _ in $(seq 1 60); do
    "${COMPOSE[@]}" logs --no-log-prefix workorders 2>/dev/null | grep -q "\"phase\":\"$1\"" && return 0
    sleep 0.5
  done
  echo "phase $1 not observed in logs" >&2; return 1
}

teardown() {
  if [[ "$KEEP" == 1 ]]; then
    note "KEEP=1: stack left running. API $API, RabbitMQ UI $MGMT (lab/lab)."
    note "Tear down with: docker compose -f $HERE/compose.yaml down -v"
  else
    step "Teardown"; cmd "docker compose down -v"; "${COMPOSE[@]}" down -v --remove-orphans 2>&1 | sed 's/^/    /'
  fi
}

# ---- 0. clean environment -----------------------------------------------------------------
section "E01 Lost Event  |  0. clean environment"
cmd "docker compose down -v && docker compose build workorders && docker compose up -d --wait postgres rabbitmq"
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
"${COMPOSE[@]}" build -q workorders
"${COMPOSE[@]}" up -d --wait postgres rabbitmq 2>&1 | sed 's/^/    /'
trap teardown EXIT

# ---- A. baseline --------------------------------------------------------------------------
section "A. BASELINE (UNSAFE): naive dual write, process killed after COMMIT, before publish"

step "A1. Start WorkOrders in naive mode"
start_api naive

step "A2. Control request, no fault: commit, then publish"
post_work_order op-naive-ok

step "A3. Same request, but the process is SIGKILLed right after COMMIT"
post_work_order op-naive-crash crash-after-commit
wait_for_exit
A_EXIT="$WORKORDERS_EXIT"

step "A4. Evidence: PostgreSQL"
sql "SELECT operation_id, write_mode, id AS work_order_id FROM work_orders ORDER BY created_at"
step "A5. Evidence: RabbitMQ"
show_broker
step "A6. Reconciliation (database vs broker)"
reconcile
step "A7. Logs (phase)"
show_logs

A_COMMITTED="$(sql_value "SELECT count(*) FROM work_orders WHERE write_mode = 'naive'")"
A_CRASH_COMMITTED="$(sql_value "SELECT count(*) FROM work_orders WHERE operation_id = 'op-naive-crash'")"
A_CRASH_EVENTS="$(count_in_broker op-naive-crash)"
A_OK_EVENTS="$(count_in_broker op-naive-ok)"

# ---- B. outbox ----------------------------------------------------------------------------
section "B. MITIGATION: transactional outbox, broker down, process killed after COMMIT, then restart"

step "B1. Make the broker unavailable"
cmd "docker compose stop rabbitmq"; "${COMPOSE[@]}" stop rabbitmq 2>&1 | sed 's/^/    /'

step "B2. Start WorkOrders in outbox mode (its publisher cannot reach the broker)"
start_api outbox

step "B3. Write while the broker is down: accepted, event pending"
post_work_order op-outbox-broker-down

step "B4. The publisher picks the pending row up and fails to reach the broker"
wait_for_phase broker_unavailable
cmd "docker compose logs workorders | jq 'select(.phase == \"broker_unavailable\")' | head -n 1"
"${COMPOSE[@]}" logs --no-log-prefix workorders 2>/dev/null | jq -rR 'fromjson? | select(.phase == "broker_unavailable")
  | "    \(.ts[11:23])  \(.level)     \(.phase)       attempt=\(.attempt) retry_in_ms=\(.retry_in_ms)  \(.error)"' | head -n 1

step "B5. Same kill point as A3: SIGKILL right after COMMIT"
post_work_order op-outbox-crash crash-after-commit
wait_for_exit
B_EXIT="$WORKORDERS_EXIT"

step "B6. Evidence BEFORE restart"
sql "SELECT operation_id, event_id, event_type, created_at, published_at FROM outbox_messages ORDER BY created_at"
show_broker
B_PENDING_BEFORE="$(sql_value "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL")"

step "B7. Restore the broker (WorkOrders still down)"
cmd "docker compose up -d --wait rabbitmq"; "${COMPOSE[@]}" up -d --wait --no-deps rabbitmq 2>&1 | sed 's/^/    /'
show_broker

step "B8. Restart WorkOrders; wait until no outbox row is pending"
start_api outbox
polls=0; pending="$B_PENDING_BEFORE"
while (( pending > 0 && polls < 60 )); do # 0.5 s interval, 30 s timeout
  sleep 0.5; polls=$((polls + 1)); pending="$(sql_value "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL")"
done
echo "    pending outbox rows: $B_PENDING_BEFORE -> $pending (after $polls check(s) at 0.5 s intervals)"

step "B9. Evidence AFTER restart"
sql "SELECT operation_id, event_id, created_at, published_at FROM outbox_messages ORDER BY created_at"
show_broker
step "B10. Reconciliation (database vs broker)"
reconcile
step "B11. Logs (phase): first run, kill, restart"
show_logs

B_PENDING_AFTER="$pending"
B_OUTBOX_EVENTS="$(( $(count_in_broker op-outbox-broker-down) + $(count_in_broker op-outbox-crash) ))"
B_WITHOUT_ENTRY="$(sql_value "SELECT count(*) FROM work_orders w WHERE w.write_mode = 'outbox'
                                AND NOT EXISTS (SELECT 1 FROM outbox_messages o WHERE o.aggregate_id = w.id)")"

# ---- C. rollback --------------------------------------------------------------------------
section "C. ROLLBACK: failure injected before COMMIT (outbox mode)"
DEPTH_BEFORE_C="$(queue_depth)"

step "C1. Request that fails after the INSERTs, before COMMIT"
response="$(post_work_order op-outbox-rollback fail-before-commit)"; echo "$response"
body="$(grep -m1 '^{' <<<"$response")"
attempted_wo="$(jq -r '.attempted_work_order_id' <<<"$body")"
attempted_ev="$(jq -r '.attempted_event_id' <<<"$body")"

step "C2. Evidence: the attempted ids do not exist anywhere"
sql "SELECT (SELECT count(*) FROM work_orders WHERE id = '$attempted_wo')                     AS work_orders,
            (SELECT count(*) FROM outbox_messages WHERE event_id = '$attempted_ev'
                OR aggregate_id = '$attempted_wo' OR operation_id = 'op-outbox-rollback')       AS outbox_rows"
sleep 2 # give the running publisher a few polling cycles
show_broker
step "C3. Logs (phase) for op-outbox-rollback"
show_logs op-outbox-rollback

C_WORK_ORDERS="$(sql_value "SELECT count(*) FROM work_orders WHERE operation_id = 'op-outbox-rollback'")"
C_OUTBOX="$(sql_value "SELECT count(*) FROM outbox_messages WHERE operation_id = 'op-outbox-rollback' OR event_id = '$attempted_ev'")"
C_EVENTS="$(count_in_broker op-outbox-rollback)"
DEPTH_AFTER_C="$(queue_depth)"

# ---- summary ------------------------------------------------------------------------------
section "SUMMARY: expected vs observed"
echo "A. naive baseline"
check "process killed by SIGKILL after COMMIT (exit code)"            137 "$A_EXIT"
check "op-naive-crash work order committed"                           1   "$A_CRASH_COMMITTED"
check "op-naive-crash WorkOrderCreated in broker (lost event)"        0   "$A_CRASH_EVENTS"
check "op-naive-ok control event in broker"                           1   "$A_OK_EVENTS"
echo "B. transactional outbox"
check "process killed by SIGKILL after COMMIT (exit code)"            137 "$B_EXIT"
check "(a) outbox work orders without an outbox entry"                0   "$B_WITHOUT_ENTRY"
check "outbox rows pending before restart"                            2   "$B_PENDING_BEFORE"
check "(b) outbox rows pending after restart + broker restored"       0   "$B_PENDING_AFTER"
check "(b) outbox events that reached the broker"                     2   "$B_OUTBOX_EVENTS"
echo "C. rollback"
check "(c) op-outbox-rollback work orders"                            0   "$C_WORK_ORDERS"
check "(c) op-outbox-rollback outbox rows"                            0   "$C_OUTBOX"
check "(c) op-outbox-rollback events in broker"                       0   "$C_EVENTS"
check "queue depth unchanged by the rolled-back request"              "$DEPTH_BEFORE_C" "$DEPTH_AFTER_C"
echo
echo "Not shown here and not claimed: exactly-once delivery. The outbox publishes at least once;"
echo "a crash between broker confirm and marking the row would publish the same event_id again (E02)."

if (( ${#MISMATCHES[@]} > 0 )); then
  printf '\n%s%d observation(s) did not match expectations.%s\n' "$RED" "${#MISMATCHES[@]}" "$R"; exit 1
fi
printf '\n%sAll observations matched expectations.%s\n' "$GRN" "$R"
