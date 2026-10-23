#!/usr/bin/env bash
# E02 Duplicate Delivery: scripted, reproducible run. LOCAL LAB ONLY.
#
#   (a) REPRODUCE the failure: the outbox worker is killed after the broker confirmed a message and
#       before it marked the outbox row published; after the restart it publishes the same event_id
#       again. A consumer is killed after COMMIT and before ack; the broker redelivers. The naive
#       consumer (UNSAFE baseline) reserves a seal kit on every delivery: stock drops twice.
#   (b) APPLY the mechanism: the idempotent consumer received the same deliveries on its own queue;
#       processed_messages + primary key in the same transaction kept one reservation per event.
#   (c) VERIFY: restart the worker and both consumers, measure again, compare with the correct stock.
#
# Starts from a clean environment (docker compose down -v) and tears everything down at the end.
# Set KEEP=1 to leave the stack running for inspection. Exits non-zero if any check fails.
#
# Requires: docker (with compose v2), curl, jq.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE=(docker compose -f "$HERE/compose.yaml")
API="http://127.0.0.1:${DRLAB_E02_API_PORT:-18501}"
WORKER="http://127.0.0.1:${DRLAB_E02_WORKER_PORT:-18502}"
NAIVE="http://127.0.0.1:${DRLAB_E02_NAIVE_PORT:-18503}"
IDEMPOTENT="http://127.0.0.1:${DRLAB_E02_IDEMPOTENT_PORT:-18504}"
MGMT="http://127.0.0.1:${DRLAB_RABBITMQ_UI_PORT:-15683}"
AUDIT_QUEUE="e02.work-order-created.audit"
NAIVE_QUEUE="e02.inventory.naive"
IDEMPOTENT_QUEUE="e02.inventory.idempotent"
APPS=(workorders-api outbox-worker inventory-naive inventory-idempotent)
KEEP="${KEEP:-0}"
INITIAL_STOCK="${E02_INITIAL_STOCK:-10}"

# No automatic restarts during the script: it shows the state between a crash and the restart,
# then restarts the crashed container itself (as an orchestrator would).
export E02_RESTART_POLICY=no

for tool in docker curl jq; do
  command -v "$tool" >/dev/null || { echo "missing prerequisite: $tool" >&2; exit 2; }
done

if [[ -t 1 ]]; then B=$'\e[1m'; DIM=$'\e[2m'; RED=$'\e[31m'; GRN=$'\e[32m'; YEL=$'\e[33m'; R=$'\e[0m'; else B= DIM= RED= GRN= YEL= R=; fi

section() { printf '\n%s%s\n%s%s\n' "$B" "================================================================================" "$1" "$R"; }
step()    { printf '\n%s--- %s%s\n' "$B" "$1" "$R"; }
cmd()     { printf '%s$ %s%s\n' "$DIM" "$*" "$R"; }
note()    { printf '%s%s%s\n' "$YEL" "$*" "$R"; }

CHECKS=0
MISMATCHES=()
check() { # description expected observed
  CHECKS=$((CHECKS + 1))
  if [[ "$2" == "$3" ]]; then printf '  %sOK%s   %-70s expected=%s observed=%s\n' "$GRN" "$R" "$1" "$2" "$3"
  else printf '  %sFAIL%s %-70s expected=%s observed=%s\n' "$RED" "$R" "$1" "$2" "$3"; MISMATCHES+=("$1"); fi
}

# ---- helpers ------------------------------------------------------------------------------
psql_lab() { "${COMPOSE[@]}" exec -T postgres psql -U lab -d lab -v ON_ERROR_STOP=1 -P pager=off "$@"; }
sql()      { cmd "psql: $(tr -s ' \n' ' ' <<<"$1")"; psql_lab -c "$1"; }
sql_as()   { cmd "psql: $1"; psql_lab -c "$2"; }
sql_value(){ psql_lab -tA -c "$1"; }

wait_health() { # url name
  for _ in $(seq 1 80); do
    curl -fsS "$1/health" >/dev/null 2>&1 && return 0
    sleep 0.25
  done
  echo "$2 did not become healthy" >&2; "${COMPOSE[@]}" logs "$2" >&2; exit 1
}

url_of() {
  case "$1" in
    workorders-api) echo "$API" ;; outbox-worker) echo "$WORKER" ;;
    inventory-naive) echo "$NAIVE" ;; inventory-idempotent) echo "$IDEMPOTENT" ;;
  esac
}

start_apps() { # service...
  cmd "docker compose start $*"
  "${COMPOSE[@]}" start "$@" 2>&1 | sed -E '/Waiting|Healthy/d; s/^/    /'
  for svc in "$@"; do wait_health "$(url_of "$svc")" "$svc"; done
}

post_work_order() { # operation_id
  cmd "curl -X POST $API/work-orders -H 'X-Operation-Id: $1'"
  curl -sS -X POST "$API/work-orders" -H 'Content-Type: application/json' -H "X-Operation-Id: $1" \
       -d '{"title":"Replace seal on pump P-101"}' -w '\nHTTP %{http_code}\n' | sed 's/^/    /'
}

arm() { # base_url fault operation_id
  cmd "curl -X POST '$1/faults/$2?operation_id=$3'"
  curl -sS -X POST "$1/faults/$2?operation_id=$3" -w '\nHTTP %{http_code}\n' | sed 's/^/    /'
}

event_of() { sql_value "SELECT event_id FROM outbox_messages WHERE operation_id = '$1'"; }

wait_for_exit() { # service -> sets EXIT_CODE
  local id; id="$("${COMPOSE[@]}" ps -a -q "$1")"
  for _ in $(seq 1 60); do
    [[ "$(docker inspect -f '{{.State.Status}}' "$id")" == exited ]] && break
    sleep 0.25
  done
  EXIT_CODE="$(docker inspect -f '{{.State.ExitCode}}' "$id")"
  printf '    %s: %s (exit code %s)\n' "$1" "$(docker inspect -f '{{.State.Status}}' "$id")" "$EXIT_CODE"
}

wait_sql() { # condition_sql description: poll until the query returns t
  for _ in $(seq 1 120); do
    [[ "$(sql_value "SELECT ($1)")" == t ]] && return 0
    sleep 0.25
  done
  echo "timed out waiting for: $2" >&2; exit 1
}

queue_counts() { # queue -> "ready unacked" (from the queue process itself; management stats can lag)
  "${COMPOSE[@]}" exec -T rabbitmq rabbitmqctl list_queues -q --no-table-headers name messages_ready messages_unacknowledged \
    | awk -v q="$1" '$1 == q { print $2, $3; found=1 } END { if (!found) print "0 0" }'
}

queue_messages() { local c; c="$(queue_counts "$1")"; echo $(( ${c% *} + ${c#* } )); }

show_queues() {
  cmd "rabbitmqctl list_queues name messages_ready messages_unacknowledged"
  "${COMPOSE[@]}" exec -T rabbitmq rabbitmqctl list_queues -q name messages_ready messages_unacknowledged \
    | awk 'NR == 1 || $1 ~ /^e02\./ { printf "    %-32s %-15s %s\n", $1, $2, $3 }'
}

wait_consumed() { # consumer operation_id deliveries: deliveries recorded and the consumer queue idle (acked)
  local q="e02.inventory.$1"
  for _ in $(seq 1 120); do
    if [[ "$(sql_value "SELECT count(*) FROM consumer_deliveries WHERE consumer = '$1' AND operation_id = '$2'")" -ge "$3" \
          && "$(queue_messages "$q")" == 0 ]]; then return 0; fi
    sleep 0.25
  done
  echo "timed out waiting for $1 to consume $2" >&2; exit 1
}

peek_audit() { # non-destructive: messages are requeued
  curl -sS -u lab:lab -H 'content-type: application/json' -X POST "$MGMT/api/queues/%2F/$AUDIT_QUEUE/get" \
       -d '{"count":1000,"ackmode":"ack_requeue_true","encoding":"auto"}' | jq 'if type == "array" then . else [] end'
}

audit_copies() { # event_id: how many copies of this event the broker accepted
  peek_audit | jq --arg id "$1" '[.[] | select(.properties.message_id == $id)] | length'
}

show_audit() { # optional event_id filter
  cmd "management API: POST /api/queues/%2F/$AUDIT_QUEUE/get (ackmode ack_requeue_true: peek, messages stay)"
  printf '    audit queue %s: %s message(s) in total\n' "$AUDIT_QUEUE" "$(queue_messages "$AUDIT_QUEUE")"
  peek_audit | jq -r --arg id "${1:-}" '.[] | select($id == "" or .properties.message_id == $id) | (.payload | fromjson) as $p
    | "      message_id=\(.properties.message_id)  operation_id=\(.properties.correlation_id)  work_order_id=\($p.work_order_id)"'
}

# SQL relation (event_id, copies) with the broker's per-event copy counts, for joins.
broker_copies_sql() {
  local values
  values="$(peek_audit | jq -r '[group_by(.properties.message_id)[] | "('"'"'\(.[0].properties.message_id)'"'"'::uuid, \(length))"] | join(", ")')"
  if [[ -z "$values" ]]; then echo "(SELECT NULL::uuid AS event_id, 0 AS copies WHERE false)"
  else echo "(VALUES $values)"; fi
}

show_logs() { # service operation_id [event_id]
  cmd "docker compose logs $1 | jq 'select(.phase)' (operation_id=$2)"
  "${COMPOSE[@]}" logs --no-log-prefix "$1" 2>/dev/null \
    | jq -rR --arg op "$2" --arg ev "${3:-}" 'fromjson? | select(.phase)
        | select(.operation_id == $op or ($ev != "" and ((.event_ids // "") | contains($ev))))
        | ([ (.fault         // empty | "fault=\(.)"),
             (.count         // empty | "count=\(.)"),
             (if has("redelivered") then "redelivered=\(.redelivered)" else empty end),
             (.on_hand_after // empty | "on_hand_after=\(.)") ] | join(" ")) as $detail
        | [.ts[11:23], .level, .phase, (.operation_id // "-"), ((.event_id // "") | if . == "" then "-" else .[0:13] + "..." end), $detail] | @tsv' \
    | awk -F'\t' '{ printf "    %s  %-8s %-24s %-17s %-17s %s\n", $1, $2, $3, $4, $5, $6 }'
}

show_stock() {
  sql "SELECT consumer, part_no, initial_on_hand, on_hand, initial_on_hand - on_hand AS units_taken
       FROM stock_levels ORDER BY consumer"
}

show_reservations() { # consumer [operation_id]
  local filter=""; [[ -n "${2:-}" ]] && filter=" AND operation_id = '$2'"
  sql "SELECT operation_id, event_id, reservation_id, part_no, quantity, reserved_at
       FROM part_reservations WHERE consumer = '$1'$filter ORDER BY reserved_at"
}

show_deliveries() { # consumer operation_id
  sql "SELECT operation_id, event_id, instance, delivery_tag, redelivered, received_at
       FROM consumer_deliveries WHERE consumer = '$1' AND operation_id = '$2' ORDER BY id"
}

show_rows_per_event() { # consumer
  sql "SELECT o.operation_id, o.event_id, count(r.reservation_id) AS reservations, coalesce(sum(r.quantity), 0) AS units_reserved
       FROM outbox_messages o LEFT JOIN part_reservations r ON r.event_id = o.event_id AND r.consumer = '$1'
       GROUP BY o.operation_id, o.event_id, o.created_at ORDER BY o.created_at"
}

show_final_state() {
  local copies; copies="$(queue_messages "$AUDIT_QUEUE")"
  sql_as "per consumer: broker copies (audit queue), deliveries, reservations, stock; each next to its correct value" \
      "SELECT s.consumer,
              (SELECT count(*) FROM work_orders)                                            AS work_orders,
              $copies                                                                       AS broker_copies,
              format('%s (%s redelivered)', count(d.id), count(d.id) FILTER (WHERE d.redelivered)) AS deliveries,
              (SELECT count(*) FROM part_reservations r WHERE r.consumer = s.consumer)       AS reservations,
              (SELECT count(*) FROM work_orders)                                            AS correct_reservations,
              s.on_hand,
              s.initial_on_hand - (SELECT sum(quantity) FROM work_orders)                   AS correct_on_hand,
              s.on_hand - (s.initial_on_hand - (SELECT sum(quantity) FROM work_orders))     AS difference
       FROM stock_levels s LEFT JOIN consumer_deliveries d ON d.consumer = s.consumer
       GROUP BY s.consumer, s.on_hand, s.initial_on_hand ORDER BY s.consumer"
}

reservations() { # consumer [operation_id]
  local filter=""; [[ -n "${2:-}" ]] && filter=" AND operation_id = '$2'"
  sql_value "SELECT count(*) FROM part_reservations WHERE consumer = '$1'$filter"
}
on_hand()     { sql_value "SELECT on_hand FROM stock_levels WHERE consumer = '$1'"; }
deliveries()  { sql_value "SELECT count(*) FROM consumer_deliveries WHERE consumer = '$1' AND operation_id = '$2'${3:+ AND redelivered = $3}"; }

snapshot() { # one line with every count that must not change on restart
  local db
  db="$(sql_value "SELECT format('reservations naive=%s idempotent=%s | on_hand naive=%s idempotent=%s | processed_messages=%s | deliveries=%s | pending_outbox=%s',
                (SELECT count(*) FROM part_reservations WHERE consumer = 'naive'),
                (SELECT count(*) FROM part_reservations WHERE consumer = 'idempotent'),
                (SELECT on_hand FROM stock_levels WHERE consumer = 'naive'),
                (SELECT on_hand FROM stock_levels WHERE consumer = 'idempotent'),
                (SELECT count(*) FROM processed_messages),
                (SELECT count(*) FROM consumer_deliveries),
                (SELECT count(*) FROM outbox_messages WHERE published_at IS NULL))")"
  echo "$db | audit_copies=$(queue_messages "$AUDIT_QUEUE") | queued naive=$(queue_messages "$NAIVE_QUEUE") idempotent=$(queue_messages "$IDEMPOTENT_QUEUE")"
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
section "E02 Duplicate Delivery  |  0. clean environment"
cmd "docker compose down -v && docker compose build && docker compose up -d --wait postgres rabbitmq"
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
"${COMPOSE[@]}" build -q "${APPS[@]}"
"${COMPOSE[@]}" up -d --wait postgres rabbitmq 2>&1 | sed 's/^/    /'
trap teardown EXIT
cmd "E02_RESTART_POLICY=no docker compose up -d ${APPS[*]}"
"${COMPOSE[@]}" up -d --no-deps "${APPS[@]}" 2>&1 | sed 's/^/    /'
for svc in "${APPS[@]}"; do wait_health "$(url_of "$svc")" "$svc"; done
for svc in "${APPS[@]}"; do cmd "curl $(url_of "$svc")/health"; printf '    %s\n' "$(curl -sS "$(url_of "$svc")/health")"; done

step "0.1 Business state BEFORE: each consumer has its own stock of seal kits; nothing reserved"
show_stock
sql "SELECT consumer, count(*) AS reservations FROM part_reservations GROUP BY consumer"

# ---- (a) reproduce ------------------------------------------------------------------------
section "(a) REPRODUCE THE FAILURE: duplicate publication and redelivery; naive consumer (UNSAFE baseline)"
note "Both consumers run side by side, each on its own queue, and receive the same deliveries."
note "Section (a) shows the broker and the naive consumer; section (b) shows the idempotent consumer."

step "A1. Control: one work order, published once, processed once"
post_work_order op-ok
wait_consumed naive op-ok 1; wait_consumed idempotent op-ok 1
EV_OK="$(event_of op-ok)"
show_audit "$EV_OK"
show_reservations naive op-ok
A1_COPIES="$(audit_copies "$EV_OK")"
A1_NAIVE="$(reservations naive op-ok)"

step "A2. A publisher confirm is not processing: consumers stopped, the worker gets its confirm anyway"
cmd "docker compose stop inventory-naive inventory-idempotent"
"${COMPOSE[@]}" stop inventory-naive inventory-idempotent 2>&1 | sed 's/^/    /'
post_work_order op-confirm-only
wait_sql "SELECT published_at IS NOT NULL FROM outbox_messages WHERE operation_id = 'op-confirm-only'" "op-confirm-only published"
EV_CONFIRM="$(event_of op-confirm-only)"
sql "SELECT operation_id, event_id, published_at FROM outbox_messages WHERE operation_id = 'op-confirm-only'"
show_logs outbox-worker op-confirm-only "$EV_CONFIRM"
show_queues
sql "SELECT consumer, count(*) AS reservations FROM part_reservations WHERE operation_id = 'op-confirm-only' GROUP BY consumer"
A2_MARKED="$(sql_value "SELECT count(*) FROM outbox_messages WHERE operation_id = 'op-confirm-only' AND published_at IS NOT NULL")"
A2_WAITING="$(( $(queue_messages "$NAIVE_QUEUE") + $(queue_messages "$IDEMPOTENT_QUEUE") ))"
A2_RESERVED="$(sql_value "SELECT count(*) FROM part_reservations WHERE operation_id = 'op-confirm-only'")"
note "The worker holds a confirm and marked the row published; no consumer has done anything yet."
start_apps inventory-naive inventory-idempotent
wait_consumed naive op-confirm-only 1; wait_consumed idempotent op-confirm-only 1

step "A3. Outbox worker killed AFTER the broker confirm, BEFORE marking the outbox row published"
arm "$WORKER" crash-after-publish-confirm op-dup
post_work_order op-dup
wait_for_exit outbox-worker
A3_EXIT="$EXIT_CODE"
EV_DUP="$(event_of op-dup)"
wait_consumed naive op-dup 1; wait_consumed idempotent op-dup 1

step "A3.1 Evidence BEFORE the worker restarts"
sql "SELECT operation_id, event_id, published_at FROM outbox_messages WHERE operation_id = 'op-dup'"
show_logs outbox-worker op-dup "$EV_DUP"
note "publish_confirmed, then the kill: no outbox_marked_published for this event."
show_audit "$EV_DUP"
show_reservations naive op-dup
A3_PENDING_BEFORE="$(sql_value "SELECT count(*) FROM outbox_messages WHERE operation_id = 'op-dup' AND published_at IS NULL")"
A3_COPIES_BEFORE="$(audit_copies "$EV_DUP")"

step "A3.2 Restart the outbox worker: the row is still pending, so it is published again"
start_apps outbox-worker
wait_sql "SELECT count(*) = 0 FROM outbox_messages WHERE published_at IS NULL" "no pending outbox rows"
wait_consumed naive op-dup 2; wait_consumed idempotent op-dup 2

step "A3.3 Evidence AFTER the restart: two copies of one event_id at the broker"
sql "SELECT operation_id, event_id, published_at FROM outbox_messages WHERE operation_id = 'op-dup'"
show_logs outbox-worker op-dup "$EV_DUP"
show_audit "$EV_DUP"
note "Two copies with the same message_id (= outbox event_id). To the broker they are two separate messages."
show_deliveries naive op-dup
note "Both deliveries have redelivered=false: the redelivered flag cannot reveal a publisher-side duplicate."
show_reservations naive op-dup
show_stock
A3_PENDING_AFTER="$(sql_value "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL")"
A3_COPIES_AFTER="$(audit_copies "$EV_DUP")"
A3_CONFIRMS="$("${COMPOSE[@]}" logs --no-log-prefix outbox-worker 2>/dev/null \
  | jq -rR --arg op op-dup 'fromjson? | select(.phase == "publish_confirmed" and .operation_id == $op) | .event_id' | grep -c . || true)"

step "A4. Consumers killed AFTER their transaction committed, BEFORE basic.ack"
arm "$NAIVE" crash-after-commit-before-ack op-ack
arm "$IDEMPOTENT" crash-after-commit-before-ack op-ack
post_work_order op-ack
wait_for_exit inventory-naive;      A4_NAIVE_EXIT="$EXIT_CODE"
wait_for_exit inventory-idempotent; A4_IDEMPOTENT_EXIT="$EXIT_CODE"
EV_ACK="$(event_of op-ack)"

step "A4.1 Evidence BEFORE the consumers restart"
show_logs inventory-naive op-ack
note "effect_committed, then the kill: no acked line. The broker requeued the unacked message:"
show_queues
show_reservations naive op-ack
A4_WAITING_NAIVE="$(queue_messages "$NAIVE_QUEUE")"
A4_WAITING_IDEMPOTENT="$(queue_messages "$IDEMPOTENT_QUEUE")"

step "A4.2 Restart both consumers: the broker delivers the same message again"
start_apps inventory-naive inventory-idempotent
wait_consumed naive op-ack 2; wait_consumed idempotent op-ack 2
show_deliveries naive op-ack
note "The second delivery comes from another process instance with redelivered=true."
show_logs inventory-naive op-ack
show_reservations naive op-ack
show_audit "$EV_ACK"
A4_COPIES="$(audit_copies "$EV_ACK")"

step "A5. Naive consumer: business tables after A1-A4"
show_rows_per_event naive
show_stock
note "Four work orders need four seal kits; the naive consumer took $(( INITIAL_STOCK - $(on_hand naive) )) off the shelf."

# ---- (b) apply the mechanism --------------------------------------------------------------
section "(b) APPLY THE MECHANISM: idempotent consumer, processed_messages + primary key in the same transaction"
cat <<'EOF'
    For every delivery, in ONE transaction:
      INSERT INTO processed_messages (consumer, event_id) VALUES (...) ON CONFLICT (consumer, event_id) DO NOTHING
        1 row  -> INSERT INTO part_reservations ...; UPDATE stock_levels SET on_hand = on_hand - 1 ...; COMMIT
        0 rows -> already applied by this consumer: write nothing
    then basic.ack (only after the transaction has ended).
    It received exactly the deliveries the naive consumer received in A1-A4, on its own queue.
EOF

step "B1. Deliveries received by the idempotent consumer"
sql "SELECT operation_id, event_id, instance, delivery_tag, redelivered, received_at
     FROM consumer_deliveries WHERE consumer = 'idempotent' ORDER BY id"
step "B2. processed_messages: one row per event_id, enforced by PRIMARY KEY (consumer, event_id)"
sql "SELECT consumer, event_id, processed_at FROM processed_messages ORDER BY processed_at"
step "B3. Idempotent consumer: business tables after A1-A4"
show_rows_per_event idempotent
show_stock
step "B4. Logs: the duplicates the primary key recognised"
cmd "docker compose logs inventory-idempotent | jq 'select(.phase == \"duplicate_skipped\")'"
"${COMPOSE[@]}" logs --no-log-prefix inventory-idempotent 2>/dev/null \
  | jq -rR 'fromjson? | select(.phase == "duplicate_skipped")
      | "    \(.ts[11:23])  \(.phase)  \(.operation_id)  redelivered=\(.redelivered)  (processed_messages PK conflict; nothing written)"'

B_DUP_NAIVE="$(reservations naive op-dup)";            B_DUP_IDEMPOTENT="$(reservations idempotent op-dup)"
B_ACK_NAIVE="$(reservations naive op-ack)";            B_ACK_IDEMPOTENT="$(reservations idempotent op-ack)"
B_DUP_DELIV_NAIVE="$(deliveries naive op-dup false)";  B_DUP_DELIV_IDEMPOTENT="$(deliveries idempotent op-dup false)"
B_ACK_REDELIV_NAIVE="$(deliveries naive op-ack true)"; B_ACK_REDELIV_IDEMPOTENT="$(deliveries idempotent op-ack true)"
B_SKIPPED="$("${COMPOSE[@]}" logs --no-log-prefix inventory-idempotent 2>/dev/null | grep -c '"phase":"duplicate_skipped"' || true)"

# ---- (c) verify ---------------------------------------------------------------------------
section "(c) VERIFY: restart the worker and both consumers, then measure again"

step "C1. Measurements before the restart"
SNAP_BEFORE="$(snapshot)"; echo "    $SNAP_BEFORE"

step "C2. Restart outbox worker and both consumers (graceful), wait 3 s (6 publisher polling cycles)"
cmd "docker compose restart outbox-worker inventory-naive inventory-idempotent"
"${COMPOSE[@]}" restart outbox-worker inventory-naive inventory-idempotent 2>&1 | sed 's/^/    /'
for svc in outbox-worker inventory-naive inventory-idempotent; do wait_health "$(url_of "$svc")" "$svc"; done
sleep 3

step "C3. Measurements after the restart"
SNAP_AFTER="$(snapshot)"; echo "    $SNAP_AFTER"
show_queues

step "C4. Side by side, per event_id: publishes at the broker, deliveries, business effects"
COPIES_SQL="$(broker_copies_sql)"
sql_as "per event_id: broker copies (audit queue) joined with consumer_deliveries and part_reservations" \
    "SELECT o.operation_id,
            coalesce(b.copies, 0) AS broker_copies,
            format('%s (%s redelivered)', (SELECT count(*) FROM consumer_deliveries d WHERE d.consumer = 'naive' AND d.event_id = o.event_id),
                   (SELECT count(*) FROM consumer_deliveries d WHERE d.consumer = 'naive' AND d.event_id = o.event_id AND d.redelivered)) AS naive_deliveries,
            (SELECT count(*) FROM part_reservations r WHERE r.consumer = 'naive' AND r.event_id = o.event_id) AS naive_reservations,
            format('%s (%s redelivered)', (SELECT count(*) FROM consumer_deliveries d WHERE d.consumer = 'idempotent' AND d.event_id = o.event_id),
                   (SELECT count(*) FROM consumer_deliveries d WHERE d.consumer = 'idempotent' AND d.event_id = o.event_id AND d.redelivered)) AS idempotent_deliveries,
            (SELECT count(*) FROM part_reservations r WHERE r.consumer = 'idempotent' AND r.event_id = o.event_id) AS idempotent_reservations
     FROM outbox_messages o LEFT JOIN $COPIES_SQL AS b(event_id, copies) ON b.event_id = o.event_id
     ORDER BY o.created_at"

step "C5. Final business state per consumer (correct: one reservation per work order, stock = initial - units needed)"
show_final_state

C_NAIVE_ON_HAND="$(on_hand naive)"
C_IDEMPOTENT_ON_HAND="$(on_hand idempotent)"
C_CORRECT="$(sql_value "SELECT $INITIAL_STOCK - sum(quantity) FROM work_orders")"
C_EXTRA_IDEMPOTENT="$(sql_value "SELECT count(*) FROM (SELECT event_id FROM part_reservations WHERE consumer = 'idempotent' GROUP BY event_id HAVING count(*) <> 1) x")"
C_MISSING_IDEMPOTENT="$(sql_value "SELECT count(*) FROM outbox_messages o WHERE NOT EXISTS (SELECT 1 FROM part_reservations r WHERE r.consumer = 'idempotent' AND r.event_id = o.event_id)")"
C_DOUBLE_NAIVE="$(sql_value "SELECT count(*) FROM (SELECT event_id FROM part_reservations WHERE consumer = 'naive' GROUP BY event_id HAVING count(*) > 1) x")"

# ---- summary ------------------------------------------------------------------------------
section "SUMMARY: expected vs observed"
echo "1. The same event_id is published twice (crash after broker confirm, before marking)"
check "outbox-worker killed after the broker confirm (exit code)"            137 "$A3_EXIT"
check "op-dup outbox row still pending after the crash"                      1   "$A3_PENDING_BEFORE"
check "op-dup copies at the broker before the worker restart"                1   "$A3_COPIES_BEFORE"
check "op-dup copies at the broker after the restart (same event_id)"        2   "$A3_COPIES_AFTER"
check "op-dup publisher confirms logged by the worker"                       2   "$A3_CONFIRMS"
check "outbox rows pending after the restart"                                0   "$A3_PENDING_AFTER"
check "op-dup deliveries to naive / idempotent, both redelivered=false"      "2/2" "$B_DUP_DELIV_NAIVE/$B_DUP_DELIV_IDEMPOTENT"
echo "   A publisher confirm is not processing"
check "op-confirm-only confirmed and marked published, consumers stopped"    1   "$A2_MARKED"
check "op-confirm-only messages waiting in the consumer queues"              2   "$A2_WAITING"
check "op-confirm-only reservations at that moment"                          0   "$A2_RESERVED"
echo "2. Naive consumer (UNSAFE baseline): two business effects for one event_id"
check "op-ok control: copies at the broker / naive reservations"             "1/1" "$A1_COPIES/$A1_NAIVE"
check "op-dup naive reservations"                                            2   "$B_DUP_NAIVE"
echo "3. Idempotent consumer: one business effect for that event_id"
check "op-dup idempotent reservations"                                       1   "$B_DUP_IDEMPOTENT"
echo "4. Consumer killed after COMMIT, before ack: the broker redelivers"
check "inventory-naive killed after commit (exit code)"                      137 "$A4_NAIVE_EXIT"
check "inventory-idempotent killed after commit (exit code)"                 137 "$A4_IDEMPOTENT_EXIT"
check "op-ack copies at the broker (published once)"                         1   "$A4_COPIES"
check "op-ack unacked message back in the queue: naive / idempotent"         "1/1" "$A4_WAITING_NAIVE/$A4_WAITING_IDEMPOTENT"
check "op-ack deliveries with redelivered=true: naive / idempotent"          "1/1" "$B_ACK_REDELIV_NAIVE/$B_ACK_REDELIV_IDEMPOTENT"
check "op-ack naive reservations (another effect)"                           2   "$B_ACK_NAIVE"
check "op-ack idempotent reservations"                                       1   "$B_ACK_IDEMPOTENT"
check "idempotent duplicate_skipped log lines (op-dup + op-ack)"             2   "$B_SKIPPED"
echo "5. Restart of worker and consumers: no new effects, final state consistent"
check "C1 vs C3: every count unchanged by the restart"                       same "$([[ "$SNAP_BEFORE" == "$SNAP_AFTER" ]] && echo same || echo changed)"
check "idempotent: events without exactly one reservation"                   0   "$C_EXTRA_IDEMPOTENT"
check "idempotent: published events without a reservation"                   0   "$C_MISSING_IDEMPOTENT"
check "naive: events reserved more than once"                                2   "$C_DOUBLE_NAIVE"
check "idempotent: SK-101 on hand equals the correct stock ($C_CORRECT)"     "$C_CORRECT" "$C_IDEMPOTENT_ON_HAND"
check "naive: SK-101 on hand, 2 below the correct stock ($C_CORRECT)"        "$(( C_CORRECT - 2 ))" "$C_NAIVE_ON_HAND"
echo
echo "Shown here: the transactional outbox gives at-least-once publication, not exactly-once processing."
echo "The idempotent consumer makes its DATABASE effect effectively-once within its own transaction;"
echo "side effects outside that transaction (e-mail, HTTP calls, device commands) are not covered."

if (( ${#MISMATCHES[@]} > 0 )); then
  printf '\n%s%d of %d checks FAILED.%s\n' "$RED" "${#MISMATCHES[@]}" "$CHECKS" "$R"; exit 1
fi
printf '\n%s%d of %d checks OK.%s\n' "$GRN" "$CHECKS" "$CHECKS" "$R"
