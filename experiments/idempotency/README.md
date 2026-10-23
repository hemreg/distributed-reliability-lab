# E02 — Duplicate Delivery (idempotent consumer)

[🇹🇷 Türkçe](README.tr.md) · **English**

Accompanying articles: [Idempotency in Distributed Systems](https://emregorgulu.com/en/articles/idempotency-in-distributed-systems/) ([Türkçe](https://emregorgulu.com/tr/makaleler/dagitik-sistemlerde-idempotency/)) and [Transactional Outbox](https://emregorgulu.com/en/articles/transactional-outbox/) ([Türkçe](https://emregorgulu.com/tr/makaleler/transactional-outbox/)).

> **Status:** runnable. The scripted experiment and the test suite below were executed on 2026-10-23 with the pinned versions listed here. Results from other machines are not claimed.

## Question

E01 showed that a transactional outbox does not lose events. Its cost is the opposite failure: if the outbox worker publishes a message, the broker confirms it, and the worker dies before it marks the outbox row published, the row is still pending after the restart and the same event is published again. What does that duplicate do to the consumer's data, and what does an idempotent consumer actually guarantee?

The scenario:

1. The outbox worker publishes `WorkOrderCreated` to RabbitMQ.
2. RabbitMQ accepts it (publisher confirm received).
3. The worker is killed **before** it marks the outbox row as published.
4. After the restart it publishes the same message again, with the same `event_id`.
5. The consumer receives the same event a second time.

A second, independent path to the same result is also injected: a consumer commits its database transaction and is killed **before** it sends the ack, so the broker delivers the message again (`redelivered=true`).

Two consumers receive every event side by side, each on its own queue:

| Consumer | What it does per delivery | Label |
| --- | --- | --- |
| `inventory-naive` | One transaction: insert a reservation, take one seal kit off the shelf, `COMMIT`, then ack. | **Unsafe baseline.** It exists only to show the failure. |
| `inventory-idempotent` | Same transaction, plus `INSERT INTO processed_messages (consumer, event_id) ... ON CONFLICT DO NOTHING` first. 1 row: apply the effect and `COMMIT`. 0 rows: already applied, write nothing. Then ack. | Mitigation |

Both ack manually, and only after their transaction has ended. The only difference between them is the deduplication.

### The business effect, and why the duplicate matters

Every work order ("Replace seal on pump P-101") needs one seal kit, part `SK-101`. The consumer reserves it: it inserts a row into `part_reservations` and decrements `stock_levels.on_hand` for that part. Each consumer has its own stock level (10 at start), so the two can be compared directly.

With the naive consumer, one duplicated event reserves two seal kits for one work order: the stock drops by 2 instead of 1. In a real warehouse the system would then show fewer kits than are on the shelf, so a later work order could be told the part is out of stock, or a reorder could be triggered early, and the reservation list would tell a technician to pick two kits for one job.

### Method

The scripted run, the README and the sample output follow the same order:

- **(a) Reproduce the failure:** duplicate publication and redelivery, observed at the broker and in the naive consumer's tables.
- **(b) Apply the mechanism:** the idempotent consumer's tables for exactly the same deliveries.
- **(c) Verify with real measurements:** restart the worker and both consumers, measure again, compare every count and the final stock with the correct values.

### Short answer (from the runs below)

- The outbox worker received two publisher confirms for one `event_id`, and the broker held two copies of it (same `message_id`, both `redelivered=false`). The transactional outbox gives **at-least-once publication**, not exactly-once.
- The naive consumer made two reservations for that event; after four work orders its stock was 4 instead of 6.
- The idempotent consumer received the same six deliveries and made exactly one reservation per event; its stock was 6, the correct value.
- A consumer killed after `COMMIT` and before the ack received the message again with `redelivered=true`. The naive consumer reserved again; the idempotent one did not.
- Restarting the worker and both consumers changed no count.
- A publisher confirm says nothing about processing: with both consumers stopped, the worker got its confirm and marked the row published while no reservation existed.

The idempotent consumer makes its **database** effect effectively-once, and only within its own transaction. Anything outside that transaction (an e-mail, an HTTP call to another system, a command to a device) is not covered by it and needs its own safeguard.

## Quick start (one command)

Requires Docker with Compose v2. From this directory:

```bash
docker compose up --build
```

This starts PostgreSQL, RabbitMQ and four services, all on loopback only:

| URL | Service | Role |
| --- | --- | --- |
| http://localhost:18501 | `workorders-api` | `POST /work-orders`: work order + outbox row in one transaction |
| http://localhost:18502 | `outbox-worker` | publishes pending outbox rows with publisher confirms; crash point 1 |
| http://localhost:18503 | `inventory-naive` | consumer, **unsafe** baseline; crash point 2 |
| http://localhost:18504 | `inventory-idempotent` | consumer with `processed_messages`; crash point 2 |
| http://localhost:15683 | RabbitMQ management (user `lab`, password `lab`) | queue `e02.work-order-created.audit` keeps every published copy |

In a second terminal, create a work order that is processed once:

```bash
curl -X POST localhost:18501/work-orders -H 'X-Operation-Id: ok-1'
```

Crash point 1: arm the worker, then create a work order. The worker publishes it, receives the confirm, and is killed before it marks the row. Docker restarts it (`restart: on-failure`) and it publishes the same event again:

```bash
curl -X POST 'localhost:18502/faults/crash-after-publish-confirm?operation_id=dup-1'
curl -X POST localhost:18501/work-orders -H 'X-Operation-Id: dup-1'
```

Crash point 2: arm both consumers, then create a work order. Each consumer commits its reservation and is killed before the ack. After the automatic restart the broker delivers the message again:

```bash
curl -X POST 'localhost:18503/faults/crash-after-commit-before-ack?operation_id=ack-1'
curl -X POST 'localhost:18504/faults/crash-after-commit-before-ack?operation_id=ack-1'
curl -X POST localhost:18501/work-orders -H 'X-Operation-Id: ack-1'
```

Look at the business tables a few seconds later:

```bash
docker compose exec postgres psql -U lab -d lab -c "
  SELECT consumer, operation_id, count(*) AS reservations
  FROM part_reservations GROUP BY consumer, operation_id ORDER BY operation_id, consumer;"

docker compose exec postgres psql -U lab -d lab -c "SELECT consumer, initial_on_hand, on_hand FROM stock_levels ORDER BY consumer;"
```

Observed on 2026-10-23 (three work orders, so the correct stock is 7):

```text
  consumer  | operation_id | reservations
------------+--------------+--------------
 idempotent | ack-1        |            1
 naive      | ack-1        |            2
 idempotent | dup-1        |            1
 naive      | dup-1        |            2
 idempotent | ok-1         |            1
 naive      | ok-1         |            1
(6 rows)

  consumer  | initial_on_hand | on_hand
------------+-----------------+---------
 idempotent |              10 |       7
 naive      |              10 |       5
(2 rows)
```

Each crashed container had restarted once (`docker inspect -f '{{.RestartCount}}'`), and the audit queue held 4 messages for 3 work orders. An armed fault lives only in the memory of the process and fires once, so the restarted process runs normally.

Stop and remove everything, including data:

```bash
docker compose down -v
```

The scripted experiment shows the same thing step by step, with the state between each crash and its restart, and checks every number.

## Run it

From the repository root:

```bash
# Scripted experiment: clean environment → (a) → (b) → (c) → checks → teardown (~40 s after the first build)
./experiments/idempotency/run-e02.sh

# Keep the stack running afterwards (RabbitMQ UI at http://127.0.0.1:15683, lab/lab)
KEEP=1 ./experiments/idempotency/run-e02.sh
# teardown when done
docker compose -f experiments/idempotency/compose.yaml down -v

# Tests (xUnit, in Docker, against the compose PostgreSQL/RabbitMQ), then teardown
./experiments/idempotency/test-e02.sh

# Equivalent without the wrapper
docker compose -f experiments/idempotency/compose.yaml run --rm --build tests
docker compose -f experiments/idempotency/compose.yaml --profile test down -v
```

`run-e02.sh` prints 27 expected-vs-observed checks and exits with a non-zero code if any of them fails. It uses the same four services as `docker compose up`, but sets `E02_RESTART_POLICY=no` so that a crashed container stays down until the script has recorded the state, and then starts it itself (`docker compose start`), as an orchestrator would. The default `docker compose up` does not run the script.

On macOS, `caffeinate -di ./experiments/idempotency/run-e02.sh` keeps the machine from sleeping during the run (Docker's VM sleeps with it).

## Failure injection points

All fault injection is local-lab only. The `/faults` endpoints answer `400` unless `E02_FAULT_INJECTION_ENABLED=true` (set in `compose.yaml`). A fault is armed in the target process's memory and fires at most once, for the given `operation_id` (or for the next message, without one).

| Trigger | Where exactly | Effect |
| --- | --- | --- |
| `POST :18502/faults/crash-after-publish-confirm?operation_id=X` | `OutboxPublisher`, right after `BasicPublishAsync` returned the broker's confirm for X, before the `UPDATE ... SET published_at` | **SIGKILL** of the worker (exit 137). Its claim transaction (`FOR UPDATE SKIP LOCKED`) aborts, so the row is pending again. After the restart the same `event_id` is published a second time. |
| `POST :18503/faults/crash-after-commit-before-ack?operation_id=X` (naive) or `:18504` (idempotent) | `ConsumerHost`, after the consumer's transaction for X has committed, before `BasicAckAsync` | **SIGKILL** of the consumer (exit 137). RabbitMQ requeues the unacked message when the connection closes and delivers it again with `redelivered=true`. |
| `docker compose stop inventory-naive inventory-idempotent` | consumers | Used in step A2: the worker still receives its confirm and marks the row published while nothing is processed. |

The kill is a real process exit, not an exception: no `finally` block runs, no transaction commits, no ack is sent. The services run with `init: true`, so the SIGKILL is not ignored for PID 1. Log lines are flushed first; that is the only thing that happens between the crash point and the kill.

Unlike E01, the outbox publisher runs in its own container (`outbox-worker`), so killing it does not take the API with it.

## What is measured, and where

| Evidence | Source | Why it is trustworthy |
| --- | --- | --- |
| Copies of an event at the broker | Audit queue `e02.work-order-created.audit`, bound to the exchange and never consumed. Every message the broker accepted stays there; the script peeks at it through the management API (`ackmode: ack_requeue_true`) and counts messages per `message_id` (= outbox `event_id`). | The broker routes each publish to every bound queue, so the audit queue sees what the consumer queues see. |
| Publisher confirms | `publish_confirmed` lines of `outbox-worker`, one per confirm, across both process lifetimes | Logged after `BasicPublishAsync` returned the confirm. |
| Queue depth, unacked messages | `rabbitmqctl list_queues name messages_ready messages_unacknowledged` | Asks the queue process directly; management statistics are sampled and can lag. |
| Deliveries and the redelivered flag | `consumer_deliveries`: one row per delivery, written by the consumer (autocommit) before processing, with `instance` (random per process start), `delivery_tag` and `redelivered` | Lab instrumentation outside the business transaction, so a crash after it does not remove it. A real consumer would emit a log line or a metric instead. |
| Business effects | `part_reservations` (one row per reservation) and `stock_levels.on_hand` | These are the business tables themselves. |
| Duplicate decisions | `processed_messages` (`PRIMARY KEY (consumer, event_id)`) and `duplicate_skipped` log lines | The row exists only if the effect committed with it. |

The correct values are computed from `work_orders`: one reservation per work order, and `initial_on_hand - sum(quantity)` for the stock.

Logs are one flat JSON object per line: `ts`, `level`, `category`, `phase`, `operation_id` (from the `X-Operation-Id` header; AMQP `correlation_id`), `event_id` (outbox `event_id`; AMQP `message_id`), `work_order_id`, `consumer`, `delivery_tag`, `redelivered`, `fault`, `count`, `on_hand_after`, `msg`, `error`.

| `phase` | Meaning |
| --- | --- |
| `fault_armed`, `fault_injected`, `process_killed` | A fault was armed, fired, and the SIGKILL followed. |
| `outbox_claimed`, `publish_confirmed`, `outbox_marked_published` | Worker claimed pending rows, received a confirm per row, marked the rows published. |
| `delivery_received` | Consumer received a delivery (with `redelivered`). |
| `effect_committed` | Reservation and stock update committed (with `on_hand_after`). |
| `duplicate_skipped` | Idempotent consumer: the primary key already held this `(consumer, event_id)`; nothing written. |
| `acked` | `basic.ack` sent after the transaction. |
| `processing_failed`, `message_rejected` | Processing threw (nack, requeue after 1 s), or the payload was unreadable (rejected, not requeued). |

## Observed results

Excerpts from [`results/sample/run-e02.txt`](results/sample/run-e02.txt), the complete output of a run from a clean state (E02 images removed first) on 2026-10-23. Some steps are left out below; the lines shown are copied unchanged. IDs and timestamps differ on every run.

Before the run, both consumers had 10 seal kits and no reservations.

### (a) Reproduce the failure

**A3. The worker is killed after the broker confirm, before marking the row.** Before the restart, the row is still pending, the broker already holds one copy, and both consumers have processed it:

```text
 operation_id |               event_id               | published_at 
--------------+--------------------------------------+--------------
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 
(1 row)

$ docker compose logs outbox-worker | jq 'select(.phase)' (operation_id=op-dup)
    04:17:38.992  warn     fault_armed              op-dup            -                 fault=crash-after-publish-confirm
    04:17:39.045  info     publish_confirmed        op-dup            01a166fa-8ebc...  
    04:17:39.046  warn     fault_injected           op-dup            01a166fa-8ebc...  fault=crash-after-publish-confirm
    04:17:39.046  critical process_killed           op-dup            01a166fa-8ebc...  fault=crash-after-publish-confirm
```

After `docker compose start outbox-worker`, the same event is published again and the row is marked:

```text
    04:17:42.015  info     broker_connected         op-dup            01a166fa-8ebc...  
    04:17:42.019  info     publish_confirmed        op-dup            01a166fa-8ebc...  
    04:17:42.040  info     outbox_marked_published  -                 -                 count=1
$ management API: POST /api/queues/%2F/e02.work-order-created.audit/get (ackmode ack_requeue_true: peek, messages stay)
    audit queue e02.work-order-created.audit: 4 message(s) in total
      message_id=01a166fa-8ebc-7e48-99c7-f68a32f5b602  operation_id=op-dup  work_order_id=01a166fa-8ebc-7513-b0df-814bc52398e2
      message_id=01a166fa-8ebc-7e48-99c7-f68a32f5b602  operation_id=op-dup  work_order_id=01a166fa-8ebc-7513-b0df-814bc52398e2
```

The naive consumer received both copies, neither marked as redelivered, and reserved twice:

```text
 operation_id |               event_id               | instance | delivery_tag | redelivered |          received_at          
--------------+--------------------------------------+----------+--------------+-------------+-------------------------------
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 2543b0c7 |            2 | f           | 2026-10-23 04:17:39.046261+00
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 2543b0c7 |            3 | f           | 2026-10-23 04:17:42.018685+00
(2 rows)

 operation_id |               event_id               |            reservation_id            | part_no | quantity |          reserved_at          
--------------+--------------------------------------+--------------------------------------+---------+----------+-------------------------------
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 01a166fa-8ee7-74b7-b465-81fb4577a24d | SK-101  |        1 | 2026-10-23 04:17:39.047567+00
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 01a166fa-9a83-738d-82a5-cac237b60053 | SK-101  |        1 | 2026-10-23 04:17:42.01944+00
(2 rows)
```

To the broker these are two separate messages. Only the `event_id` links them; the `redelivered` flag cannot reveal a publisher-side duplicate.

**A4. Both consumers are killed after `COMMIT`, before the ack.** The naive consumer's log ends with the kill; there is no `acked` line, and the unacked message is back in both queues:

```text
    04:17:44.551  info     delivery_received        op-ack            01a166fa-a40f...  redelivered=false
    04:17:44.552  info     effect_committed         op-ack            01a166fa-a40f...  redelivered=false on_hand_after=5
    04:17:44.552  warn     fault_injected           op-ack            01a166fa-a40f...  fault=crash-after-commit-before-ack redelivered=false
    04:17:44.553  critical process_killed           op-ack            01a166fa-a40f...  fault=crash-after-commit-before-ack redelivered=false
effect_committed, then the kill: no acked line. The broker requeued the unacked message:
$ rabbitmqctl list_queues name messages_ready messages_unacknowledged
    name                             messages_ready  messages_unacknowledged
    e02.inventory.idempotent         1               0
    e02.work-order-created.audit     5               0
    e02.inventory.naive              1               0
```

After the restart, a new process instance receives it with `redelivered=true` and the naive consumer reserves again:

```text
 operation_id |               event_id               | instance | delivery_tag | redelivered |          received_at          
--------------+--------------------------------------+----------+--------------+-------------+-------------------------------
 op-ack       | 01a166fa-a40f-7693-900c-53292ec798be | 2543b0c7 |            4 | f           | 2026-10-23 04:17:44.550851+00
 op-ack       | 01a166fa-a40f-7693-900c-53292ec798be | 30dc7458 |            1 | t           | 2026-10-23 04:17:47.319393+00
(2 rows)
```

**A5. The naive consumer's business tables after four work orders:**

```text
  operation_id   |               event_id               | reservations | units_reserved 
-----------------+--------------------------------------+--------------+----------------
 op-ok           | 01a166fa-7383-702a-83cc-eedbbaf8ac2a |            1 |              1
 op-confirm-only | 01a166fa-7d5c-742b-a869-9f43d762b959 |            1 |              1
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 |            2 |              2
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be |            2 |              2
(4 rows)

  consumer  | part_no | initial_on_hand | on_hand | units_taken 
------------+---------+-----------------+---------+-------------
 idempotent | SK-101  |              10 |       6 |           4
 naive      | SK-101  |              10 |       4 |           6
(2 rows)
```

**A2. A confirm is not processing.** With both consumers stopped, the worker received its confirm and marked the row published (`published_at 2026-10-23 04:17:35.012801+00`). At that moment each consumer queue held one ready message and `part_reservations` had no row for `op-confirm-only`.

### (b) Apply the mechanism

The idempotent consumer received the same six deliveries, including the duplicate copy (`op-dup`, twice `f`) and the redelivery (`op-ack`, `t`):

```text
  operation_id   |               event_id               | instance | delivery_tag | redelivered |          received_at          
-----------------+--------------------------------------+----------+--------------+-------------+-------------------------------
 op-ok           | 01a166fa-7383-702a-83cc-eedbbaf8ac2a | e8120831 |            1 | f           | 2026-10-23 04:17:32.486975+00
 op-confirm-only | 01a166fa-7d5c-742b-a869-9f43d762b959 | 627228f7 |            1 | f           | 2026-10-23 04:17:37.806515+00
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 627228f7 |            2 | f           | 2026-10-23 04:17:39.046055+00
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 627228f7 |            3 | f           | 2026-10-23 04:17:42.018735+00
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be | 627228f7 |            4 | f           | 2026-10-23 04:17:44.550853+00
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be | 40b30208 |            1 | t           | 2026-10-23 04:17:47.317544+00
(6 rows)
```

`processed_messages` holds one row per event, and the reservations follow it:

```text
  consumer  |               event_id               |         processed_at          
------------+--------------------------------------+-------------------------------
 idempotent | 01a166fa-7383-702a-83cc-eedbbaf8ac2a | 2026-10-23 04:17:32.489775+00
 idempotent | 01a166fa-7d5c-742b-a869-9f43d762b959 | 2026-10-23 04:17:37.808254+00
 idempotent | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 2026-10-23 04:17:39.046995+00
 idempotent | 01a166fa-a40f-7693-900c-53292ec798be | 2026-10-23 04:17:44.551753+00
(4 rows)

  operation_id   |               event_id               | reservations | units_reserved 
-----------------+--------------------------------------+--------------+----------------
 op-ok           | 01a166fa-7383-702a-83cc-eedbbaf8ac2a |            1 |              1
 op-confirm-only | 01a166fa-7d5c-742b-a869-9f43d762b959 |            1 |              1
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 |            1 |              1
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be |            1 |              1
(4 rows)

    04:17:42.020  duplicate_skipped  op-dup  redelivered=false  (processed_messages PK conflict; nothing written)
    04:17:47.320  duplicate_skipped  op-ack  redelivered=true  (processed_messages PK conflict; nothing written)
```

### (c) Verify

Every count, before and after `docker compose restart outbox-worker inventory-naive inventory-idempotent` and a 3 s wait:

```text
--- C1. Measurements before the restart
    reservations naive=6 idempotent=4 | on_hand naive=4 idempotent=6 | processed_messages=4 | deliveries=12 | pending_outbox=0 | audit_copies=5 | queued naive=0 idempotent=0
--- C3. Measurements after the restart
    reservations naive=6 idempotent=4 | on_hand naive=4 idempotent=6 | processed_messages=4 | deliveries=12 | pending_outbox=0 | audit_copies=5 | queued naive=0 idempotent=0
```

Side by side, per event:

```text
  operation_id   | broker_copies | naive_deliveries  | naive_reservations | idempotent_deliveries | idempotent_reservations 
-----------------+---------------+-------------------+--------------------+-----------------------+-------------------------
 op-ok           |             1 | 1 (0 redelivered) |                  1 | 1 (0 redelivered)     |                       1
 op-confirm-only |             1 | 1 (0 redelivered) |                  1 | 1 (0 redelivered)     |                       1
 op-dup          |             2 | 2 (0 redelivered) |                  2 | 2 (0 redelivered)     |                       1
 op-ack          |             1 | 2 (1 redelivered) |                  2 | 2 (1 redelivered)     |                       1
(4 rows)
```

Final business state per consumer, next to the correct values:

```text
  consumer  | work_orders | broker_copies |    deliveries     | reservations | correct_reservations | on_hand | correct_on_hand | difference 
------------+-------------+---------------+-------------------+--------------+----------------------+---------+-----------------+------------
 idempotent |           4 |             5 | 6 (1 redelivered) |            4 |                    4 |       6 |               6 |          0
 naive      |           4 |             5 | 6 (1 redelivered) |            6 |                    4 |       4 |               6 |         -2
(2 rows)
```

### Checks printed by the script

```text
1. The same event_id is published twice (crash after broker confirm, before marking)
  OK   outbox-worker killed after the broker confirm (exit code)              expected=137 observed=137
  OK   op-dup outbox row still pending after the crash                        expected=1 observed=1
  OK   op-dup copies at the broker before the worker restart                  expected=1 observed=1
  OK   op-dup copies at the broker after the restart (same event_id)          expected=2 observed=2
  OK   op-dup publisher confirms logged by the worker                         expected=2 observed=2
  OK   outbox rows pending after the restart                                  expected=0 observed=0
  OK   op-dup deliveries to naive / idempotent, both redelivered=false        expected=2/2 observed=2/2
   A publisher confirm is not processing
  OK   op-confirm-only confirmed and marked published, consumers stopped      expected=1 observed=1
  OK   op-confirm-only messages waiting in the consumer queues                expected=2 observed=2
  OK   op-confirm-only reservations at that moment                            expected=0 observed=0
2. Naive consumer (UNSAFE baseline): two business effects for one event_id
  OK   op-ok control: copies at the broker / naive reservations               expected=1/1 observed=1/1
  OK   op-dup naive reservations                                              expected=2 observed=2
3. Idempotent consumer: one business effect for that event_id
  OK   op-dup idempotent reservations                                         expected=1 observed=1
4. Consumer killed after COMMIT, before ack: the broker redelivers
  OK   inventory-naive killed after commit (exit code)                        expected=137 observed=137
  OK   inventory-idempotent killed after commit (exit code)                   expected=137 observed=137
  OK   op-ack copies at the broker (published once)                           expected=1 observed=1
  OK   op-ack unacked message back in the queue: naive / idempotent           expected=1/1 observed=1/1
  OK   op-ack deliveries with redelivered=true: naive / idempotent            expected=1/1 observed=1/1
  OK   op-ack naive reservations (another effect)                             expected=2 observed=2
  OK   op-ack idempotent reservations                                         expected=1 observed=1
  OK   idempotent duplicate_skipped log lines (op-dup + op-ack)               expected=2 observed=2
5. Restart of worker and consumers: no new effects, final state consistent
  OK   C1 vs C3: every count unchanged by the restart                         expected=same observed=same
  OK   idempotent: events without exactly one reservation                     expected=0 observed=0
  OK   idempotent: published events without a reservation                     expected=0 observed=0
  OK   naive: events reserved more than once                                  expected=2 observed=2
  OK   idempotent: SK-101 on hand equals the correct stock (6)                expected=6 observed=6
  OK   naive: SK-101 on hand, 2 below the correct stock (6)                   expected=4 observed=4

27 of 27 checks OK.
```

The scripted run passed all 27 checks in three runs on 2026-10-23: twice with existing images, once after the E02 images had been removed (38.5 s including the image build from the local build cache).

## What this shows

1. **The outbox publishes at least once.** A crash between the broker confirm and the outbox update re-publishes the same `event_id`. The outbox cannot avoid this: marking the row and the broker accepting the message are two systems, and the worker cannot commit them together.
2. **A publisher confirm is a statement about the broker, not about consumers.** It means the broker has taken responsibility for the message (routed it to the bound durable queues). In A2 the confirm arrived and the row was marked published while no consumer had done anything.
3. **Duplicates reach a consumer in two different ways,** and only one of them is visible to the broker: a publisher-side duplicate is a new message (`redelivered=false`); a redelivery after a lost ack is the same message (`redelivered=true`). Deduplication has to work on the business identity of the event (`event_id`), not on the redelivered flag.
4. **Acking after commit is necessary but not sufficient.** Both consumers ack only after `COMMIT`, so neither lost an effect; the naive one still applied the redelivered message a second time.
5. **A uniqueness constraint in the same transaction makes the consumer's database effect effectively-once.** The marker and the effect commit together or not at all, and the primary key, not a prior read, decides which of two concurrent deliveries wins (see the tests).
6. **The damage of a duplicate shows up in business data,** not in the broker: two reservations for one work order and a stock level 2 below the truth after four work orders.

## What this does not show

- **Exactly-once delivery or processing in general.** Nothing here stops the broker from delivering a message more than once. The consumer makes one specific effect idempotent: the writes in its own PostgreSQL transaction.
- **External side effects.** If the consumer also sent an e-mail, called another system over HTTP or sent a command to a device, that call would be outside the transaction. Made before `COMMIT`, it is repeated whenever the transaction rolls back or the process dies before `COMMIT` and the message is redelivered. Made after `COMMIT`, it is lost if the process dies between `COMMIT` and the call, because the redelivery is then recognised as a duplicate and skipped. Such effects need their own safeguards, such as an idempotency key that the receiving system honours, or an outbox on the consumer side. None of that is implemented.
- **Concurrent consumers in production conditions.** Two concurrent deliveries of the same event are tested in process (two connections, one held open), not with two consumer processes competing on one queue under load.
- **Poison messages.** A message that always fails is nacked and redelivered forever (with a 1 s pause). There is no retry limit and no dead-letter queue. Out of scope.
- **Retention of `processed_messages`.** The table grows by one row per event per consumer and is never cleaned up. Deleting old rows is a trade-off: a row deleted too early lets a late duplicate (for example a re-publish from an outbox restored from backup) through. The retention window has to be longer than the longest time a duplicate can still arrive. Not implemented.
- **Ordering.** Not examined.
- **Broker or database failures.** Only application processes are killed. Not simulated: PostgreSQL crash, an unknown `COMMIT` outcome, network partitions, broker restarts, quorum queues, multi-node RabbitMQ.

## Consumer design

The idempotent path, in one transaction (`ReservationHandler.HandleAsync`):

```sql
BEGIN;
INSERT INTO processed_messages (consumer, event_id) VALUES ($1, $2)
  ON CONFLICT (consumer, event_id) DO NOTHING;
-- row count 0: already applied by this consumer -> ROLLBACK, nothing written, ack
-- row count 1: first time:
INSERT INTO part_reservations (reservation_id, consumer, event_id, work_order_id, ...) VALUES (...);
UPDATE stock_levels SET on_hand = on_hand - $quantity WHERE consumer = $1 AND part_no = $part RETURNING on_hand;
COMMIT;
-- then basic.ack
```

Why this shape:

- **The constraint decides, not a read.** With `SELECT ... WHERE event_id = ...` followed by `INSERT`, two concurrent deliveries can both read "not processed" and both apply the effect. With the primary key, the second `INSERT` waits on the first transaction's uncommitted key; when the first commits it gets 0 rows, and if the first rolls back it gets 1 row and applies the effect itself. One of the tests holds the first transaction open to show the second one waiting.
- **Same transaction.** If the marker were written separately, a crash between the marker and the business commit would leave the event marked but not applied, and the redelivery would be skipped: a lost reservation.
- **Scoped per consumer.** The key is `(consumer, event_id)`: another consumer of the same event deduplicates independently.
- **Marker first.** Inserting the marker before the business writes makes a duplicate cheap (no business writes, nothing to undo) and lets a concurrent duplicate block early.
- **Manual ack after the transaction, prefetch 1.** A crash before the ack causes a redelivery, never a lost message. A graceful stop cancels the consumer and waits for the delivery in progress to finish.
- **No natural-key constraint on the business table.** `part_reservations` has no unique `(consumer, work_order_id)`, on purpose, so that the naive baseline can show the duplicate. In a real system such a constraint is a useful second safeguard where the business rule allows it (here: one seal-kit reservation per work order), but it is specific to each effect; `processed_messages` is generic.

Both consumers use the same PostgreSQL database as the producer (`lab`) for convenience. That does not affect the guarantee: the consumer's transaction covers only its own tables, and it is unrelated to the producer's transaction.

## Automated tests

`tests/Idempotency.Tests` (xUnit v3, 12 tests) runs against the real PostgreSQL and RabbitMQ from compose, in a dedicated database (`e02_tests`) and with dedicated exchange and queue names, so it never touches the script's state. The end-to-end tests start the real outbox worker and consumers as **separate OS processes**; the injected crashes are real SIGKILLs and every restart is a fresh process.

| Test | Criterion |
| --- | --- |
| `C1_crash_between_broker_confirm_and_marking_publishes_the_same_event_id_twice` | 1. Worker exits 137 after one `publish_confirmed` and no `outbox_marked_published`; the row is pending and the audit queue holds 1 copy. After a fresh worker, the audit queue holds 2 copies, both with `message_id` = `event_id` and `redelivered=false`. |
| `C2_C3_duplicate_publication_naive_consumer_reserves_twice_idempotent_consumer_once` | 2, 3. Same crash with both consumers running: each receives 2 deliveries (`false, false`); naive 2 reservations and stock −2, idempotent 1 reservation, stock −1, 1 `processed_messages` row, 1 `duplicate_skipped`. |
| `C4_consumer_crash_after_commit_before_ack_is_redelivered_naive_reserves_again_idempotent_does_not` | 4. Both consumers exit 137 with no `acked` line; 1 committed reservation each; the message is back in each queue; the event was published once. After fresh processes: deliveries `false, true`; naive 2 reservations, idempotent 1. |
| `C5_restarting_worker_and_consumers_creates_no_new_effects` | 5. Three events plus one broker-level duplicate; snapshot (reservations, stock, `processed_messages`, deliveries); graceful stop of all three processes, fresh processes, 3 s; snapshot unchanged, nothing pending, queues empty. |
| `Publisher_confirm_does_not_mean_a_consumer_processed_the_event` | A confirm and `published_at` exist while no consumer runs: 1 message in each consumer queue, 0 reservations, stock unchanged. |
| `Naive_baseline_reserves_again_on_every_delivery_of_the_same_event` | Three deliveries of one event: 3 reservations, stock −3. |
| `Idempotent_consumer_applies_an_event_once_and_skips_later_deliveries` | Three deliveries: `Applied, Duplicate, Duplicate`; 1 reservation. |
| `Two_concurrent_deliveries_of_the_same_event_id_the_primary_key_lets_exactly_one_win` | Two handlers, two connections. The first holds its transaction open after writing; the second is still blocked 500 ms later. After release: one `Applied`, one `Duplicate`, 1 reservation. |
| `Sixteen_concurrent_deliveries_of_the_same_event_id_create_exactly_one_reservation` | 16 handlers released at once: exactly 1 `Applied`, 15 `Duplicate`, 1 reservation. |
| `A_rolled_back_transaction_leaves_no_processed_marker_so_the_redelivery_applies_the_effect` | Failure before `COMMIT`: no marker, no reservation, stock unchanged; the next delivery is `Applied`, the one after it `Duplicate`. |
| `Duplicate_detection_is_scoped_per_consumer` | The same event applies once for `idempotent` and once for `idempotent-b`, and is a duplicate for each afterwards. |
| `Processed_messages_has_a_primary_key_on_consumer_and_event_id` | The schema has `PRIMARY KEY (consumer, event_id)`. |

Output of `./experiments/idempotency/test-e02.sh` (2026-10-23; the services' own log lines are omitted):

```text
  Passed Idempotency.Tests.DuplicateDeliveryTests.C2_C3_duplicate_publication_naive_consumer_reserves_twice_idempotent_consumer_once [1 s]
  Passed Idempotency.Tests.DuplicateDeliveryTests.C5_restarting_worker_and_consumers_creates_no_new_effects [4 s]
  Passed Idempotency.Tests.DuplicateDeliveryTests.C4_consumer_crash_after_commit_before_ack_is_redelivered_naive_reserves_again_idempotent_does_not [1 s]
  Passed Idempotency.Tests.DuplicateDeliveryTests.C1_crash_between_broker_confirm_and_marking_publishes_the_same_event_id_twice [649 ms]
  Passed Idempotency.Tests.DuplicateDeliveryTests.Publisher_confirm_does_not_mean_a_consumer_processed_the_event [264 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Sixteen_concurrent_deliveries_of_the_same_event_id_create_exactly_one_reservation [25 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Duplicate_detection_is_scoped_per_consumer [17 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Processed_messages_has_a_primary_key_on_consumer_and_event_id [13 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Two_concurrent_deliveries_of_the_same_event_id_the_primary_key_lets_exactly_one_win [534 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Naive_baseline_reserves_again_on_every_delivery_of_the_same_event [29 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.A_rolled_back_transaction_leaves_no_processed_marker_so_the_redelivery_applies_the_effect [20 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Idempotent_consumer_applies_an_event_once_and_skips_later_deliveries [16 ms]

Test Run Successful.
Total tests: 12
     Passed: 12
```

As a sanity check I ran the suite against three deliberately broken variants of the idempotent consumer, then restored the code and the suite passed again (12 of 12):

| Broken variant | Failed tests | Which |
| --- | --- | --- |
| A. `processed_messages` insert in its own autocommit statement, outside the business transaction | 2 of 12 | the rollback test (the marker survives a rolled-back reservation, so the redelivery is skipped), and the two-concurrent-deliveries test (the second delivery is no longer blocked) |
| B. No primary key on `processed_messages`, check-then-insert (`SELECT EXISTS`, then `INSERT`) | 3 of 12 | both concurrency tests (2 and 16 reservations instead of 1), and the schema test. Every sequential test, including all end-to-end crash tests, still passed. |
| C. Deduplication removed (the idempotent consumer behaves like the naive one) | 8 of 12 | everything except the naive, producer-side and schema tests |

Variant B is the instructive one: a check-then-insert consumer passes every test that delivers duplicates one after the other. Only concurrent duplicates expose it.

**What the tests guarantee, and what they do not.** They run against the real PostgreSQL 18.6 and RabbitMQ 4.2.9 from compose, with real process kills, on the .NET version pinned below. They check that a crash between broker confirm and marking re-publishes the same `event_id`; that the naive consumer applies every delivery and the idempotent consumer applies one per `(consumer, event_id)`, including on a redelivery after a lost ack, under concurrent deliveries in process and after a rolled-back attempt; and that restarts create no new effects. They do **not** check two consumer processes competing on one queue under load, behaviour under broker or database failures, external side effects, poison messages, retention of `processed_messages`, or performance. Concurrency is tested with up to 16 concurrent handlers in one process. Passing tests say nothing about production use: the code is a lab, not a library.

## Layout

```text
experiments/idempotency/
├── compose.yaml              # project "drlab-e02": `docker compose up --build` → API, outbox worker, two consumers; test profile
├── infrastructure.env        # moves the RabbitMQ UI to 15683 for the included infrastructure (E01 uses 15682)
├── Dockerfile                # build / test / runtime stages; one runtime image with both programs
├── run-e02.sh                # scripted experiment (a) → (b) → (c), 27 checks, teardown
├── test-e02.sh               # runs the xUnit suite in Docker, tears down
├── Idempotency.slnx
├── results/sample/run-e02.txt  # complete output of the reference run
├── src/Lab.Shared/           # JSON console logs, SIGKILL terminator, schema setup, one-shot ArmedFault
├── src/WorkOrders/           # producer (E01's outbox path), roles `api` and `outbox-worker`
│   ├── Program.cs            # POST /work-orders (api), POST /faults/crash-after-publish-confirm (worker), GET /health
│   ├── WorkOrderService.cs   # work order + outbox row in one transaction
│   ├── OutboxPublisher.cs    # claim (FOR UPDATE SKIP LOCKED) → publish with confirm → crash point 1 → mark
│   ├── Broker.cs             # publisher confirms, mandatory=true, exchange + audit queue
│   └── schema.sql            # work_orders, outbox_messages, immutability trigger
├── src/Inventory/            # consumer, modes `naive` and `idempotent`
│   ├── Program.cs            # GET /health (200 only while consuming), POST /faults/crash-after-commit-before-ack
│   ├── ConsumerHost.cs       # manual ack, prefetch 1, delivery log, crash point 2, reconnect loop
│   ├── ReservationHandler.cs # the business transaction; processed_messages in idempotent mode
│   └── schema.sql            # stock_levels, part_reservations, processed_messages, consumer_deliveries
└── tests/Idempotency.Tests/  # xUnit v3 tests against real PostgreSQL + RabbitMQ
```

## Versions used

| Component | Version |
| --- | --- |
| .NET SDK / ASP.NET Core runtime images | `mcr.microsoft.com/dotnet/sdk:10.0.402`, `mcr.microsoft.com/dotnet/aspnet:10.0.13` (runtime reported: .NET 10.0.13) |
| PostgreSQL | `postgres:18.6-alpine` |
| RabbitMQ | `rabbitmq:4.2.9-management-alpine` |
| NuGet | Npgsql 10.0.3, RabbitMQ.Client 7.2.2, xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.11.0 |

## Prerequisites

- Docker with Compose v2.20 or later (for `include`). Tested with Docker 28.4 / Compose 2.39 on macOS (arm64).
- `curl` and `jq` on the host (only used by `run-e02.sh`).
- No local .NET SDK is needed.
- Free loopback ports `127.0.0.1:18501`-`18504` (services) and `127.0.0.1:15683` (RabbitMQ management UI/API). Override them with `DRLAB_E02_API_PORT`, `DRLAB_E02_WORKER_PORT`, `DRLAB_E02_NAIVE_PORT`, `DRLAB_E02_IDEMPOTENT_PORT` and `DRLAB_RABBITMQ_UI_PORT`. PostgreSQL publishes no host port; use `docker compose exec postgres psql -U lab -d lab`.

The compose project name `drlab-e02` keeps containers, networks and volumes separate from E01, E03 and other local stacks. E01 publishes the RabbitMQ UI on 15682 and E02 on 15683 (set in `infrastructure.env`, which only applies to E02's include of the shared infrastructure file), so E01 and E02 can run at the same time.

## Limitations (read before citing results)

- **Effectively-once, only for the database effect, only for this consumer.** The guarantee holds for writes inside the consumer's own PostgreSQL transaction. It does not extend to other consumers, other databases or external calls.
- **No exactly-once claim.** Each consumer received 6 deliveries for 4 work orders (5 published copies, 1 redelivery). That is how at-least-once delivery behaves, not an error the outbox could have prevented.
- **Both crash points are armed by HTTP** and live in process memory. A real crash can happen anywhere; these two places were chosen because they produce the two kinds of duplicate.
- **Stock can go negative** (no `CHECK (on_hand >= 0)`), and out-of-stock handling is not modelled.
- **`consumer_deliveries` is written outside the business transaction** and only for evidence. It is not part of the mechanism.
- **The tests' concurrency** is in one process with separate connections, not several consumer processes on one queue.
- **Processed IDs are kept forever;** see "What this does not show" for the retention trade-off.
- **What is simulated:** SIGKILL of application processes and a graceful restart. Not simulated: database or broker crashes, network partitions, unknown `COMMIT` outcomes, clock issues.
- Credentials (`lab/lab`) and fault injection are for local experiments only.

## Failure analysis (brief)

The outbox closes the "lost event" gap by making the obligation to publish durable, but marking the obligation as fulfilled is a second write that cannot commit together with the broker's acceptance. Every crash in that window turns into a re-publish, and every crash between a consumer's `COMMIT` and its ack turns into a redelivery. Neither is a bug in the broker or in the outbox; both are what at-least-once means. The broker cannot tell a re-published copy from a new message, so the only place that can recognise the duplicate is the consumer, and only by a stable business identity (`event_id`). The consumer can make that recognition reliable only where it has atomicity: inside its own database transaction, with a uniqueness constraint deciding. Outside that transaction, the same question has to be answered again, by the systems that perform the external effects.
