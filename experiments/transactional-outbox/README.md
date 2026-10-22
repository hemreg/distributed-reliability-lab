# E01 — Lost Event (transactional outbox)

[🇹🇷 Türkçe](README.tr.md) · **English**

> **Status:** runnable. The scripted experiment and the test suite below were executed on 2026-10-22 with the pinned versions listed here. Results from other machines are not claimed.

## Question

What happens when a database transaction commits but the corresponding event is never published, and does a transactional outbox close that gap?

The experiment compares two write paths in the same small `WorkOrders` service:

| Mode | What it does | Label |
| --- | --- | --- |
| `naive` | `COMMIT` the work order in PostgreSQL, **then** publish `WorkOrderCreated` to RabbitMQ in a separate step (dual write). | **Unsafe baseline.** It exists only to demonstrate the failure. |
| `outbox` | Insert the work order **and** an immutable outbox row in the **same** transaction. A background publisher relays pending rows to RabbitMQ with publisher confirms, then marks them published. | Mitigation |

The mode is set with `E01_MODE` and has no default. The service refuses to start without it.

## Quick start (one command)

Requires Docker with Compose v2. From this directory:

```bash
docker compose up --build
```

This starts PostgreSQL, RabbitMQ and **both** write paths side by side:

| URL | Service | Mode |
| --- | --- | --- |
| http://localhost:18481 | `outbox-api` | transactional outbox (mitigation) |
| http://localhost:18482 | `naive-api` | dual write (**unsafe** baseline) |
| http://localhost:15682 | RabbitMQ management (user `lab`, password `lab`) | queue `e01.work-order-created` holds the published events |

Create a work order on each API:

```bash
curl -X POST localhost:18481/work-orders -H 'X-Operation-Id: outbox-ok'
curl -X POST localhost:18482/work-orders -H 'X-Operation-Id: naive-ok'
```

Now kill each process right after `COMMIT` (local lab only; the request gets no response because the process dies):

```bash
curl -X POST localhost:18482/work-orders -H 'X-Operation-Id: naive-crash'  -H 'X-Fault-Inject: crash-after-commit'
curl -X POST localhost:18481/work-orders -H 'X-Operation-Id: outbox-crash' -H 'X-Fault-Inject: crash-after-commit'
```

Both containers restart on their own (`restart: on-failure`), as an orchestrator would restart them. Compare the database with the broker:

```bash
docker compose exec postgres psql -U lab -d lab -c "
  SELECT w.operation_id, w.write_mode,
         CASE WHEN o.event_id IS NULL THEN '-' WHEN o.published_at IS NULL THEN 'pending' ELSE 'published' END AS outbox
  FROM work_orders w LEFT JOIN outbox_messages o ON o.aggregate_id = w.id ORDER BY w.created_at;"

docker compose exec rabbitmq rabbitmqctl -q list_queues name messages
```

Observed on 2026-10-22: four work orders are committed but the queue holds **three** events. The `naive-crash` event never existed; it was lost between `COMMIT` and publish. After its restart, `outbox-api` found `outbox-crash` still pending and published it within about half a second. To see which operations reached the broker, open the queue in the management UI and use *Get messages* (the `operation_id` is in each message).

Stop and remove everything, including data:

```bash
docker compose down -v
```

The scripted experiment and the automated tests below cover the same ground more rigorously (broker outage, rollback, assertions).

## Layout

```text
experiments/transactional-outbox/
├── compose.yaml              # project "drlab-e01": `docker compose up` → outbox-api + naive-api; script/test profiles
├── Dockerfile                # build / test / runtime stages (.NET SDK only inside Docker)
├── run-e01.sh                # scripted experiment A → B → C, prints evidence, tears down
├── test-e01.sh               # runs the xUnit suite in Docker, tears down
├── TransactionalOutbox.slnx
├── src/WorkOrders/
│   ├── Program.cs            # minimal API: POST /work-orders, GET /health
│   ├── WorkOrderService.cs   # the two write paths + fault injection points
│   ├── OutboxPublisher.cs    # claim (FOR UPDATE SKIP LOCKED) → publish with confirms → mark
│   ├── Broker.cs             # RabbitMQ publisher with confirms, mandatory=true
│   ├── FaultInjection.cs     # X-Fault-Inject header, SIGKILL terminator
│   ├── schema.sql            # work_orders, outbox_messages, immutability trigger
│   └── ...
└── tests/WorkOrders.Tests/   # xUnit v3 acceptance tests against real PostgreSQL + RabbitMQ
```

## Versions used

| Component | Version |
| --- | --- |
| .NET SDK / ASP.NET Core runtime images | `mcr.microsoft.com/dotnet/sdk:10.0.402`, `mcr.microsoft.com/dotnet/aspnet:10.0.13` |
| PostgreSQL | `postgres:18.6-alpine` |
| RabbitMQ | `rabbitmq:4.2.9-management-alpine` |
| NuGet | Npgsql 10.0.3, RabbitMQ.Client 7.2.2, xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.11.0 |

## Prerequisites

- Docker with Compose v2.20 or later (for `include`). Tested with Docker 28.4 / Compose 2.39 on macOS (arm64).
- `curl` and `jq` on the host (only used by `run-e01.sh`).
- No local .NET SDK is needed. Everything builds and runs in containers.
- Free loopback ports: `127.0.0.1:18480` (API) and `127.0.0.1:15682` (RabbitMQ management UI/API). You can override them with `DRLAB_E01_API_PORT` and `DRLAB_RABBITMQ_UI_PORT`. PostgreSQL publishes no host port. Use `docker compose exec postgres psql -U lab -d lab`.

The compose project name `drlab-e01` keeps containers, networks and volumes separate from other local stacks.

## Run it

From the repository root:

```bash
# Scripted experiment: clean environment → A → B → C → summary → teardown (~30 s after the first build)
./experiments/transactional-outbox/run-e01.sh

# Keep the stack running afterwards (RabbitMQ UI at http://127.0.0.1:15682, lab/lab)
KEEP=1 ./experiments/transactional-outbox/run-e01.sh
docker compose -f experiments/transactional-outbox/compose.yaml down -v   # teardown when done

# Acceptance tests (xUnit, in Docker, against the compose PostgreSQL/RabbitMQ), then teardown
./experiments/transactional-outbox/test-e01.sh

# Equivalent without the wrapper
docker compose -f experiments/transactional-outbox/compose.yaml run --rm --build tests
docker compose -f experiments/transactional-outbox/compose.yaml --profile test down -v
```

`run-e01.sh` exits with a non-zero code if any observation differs from the expectation.

### Driving it by hand

```bash
C="docker compose -f experiments/transactional-outbox/compose.yaml"
$C up -d --wait postgres rabbitmq
E01_MODE=naive $C up -d --no-deps workorders            # or E01_MODE=outbox

curl -X POST http://127.0.0.1:18480/work-orders -H 'X-Operation-Id: op-1'
curl -X POST http://127.0.0.1:18480/work-orders -H 'X-Operation-Id: op-2' -H 'X-Fault-Inject: crash-after-commit'

$C exec postgres psql -U lab -d lab -c 'SELECT * FROM work_orders'
$C exec postgres psql -U lab -d lab -c 'SELECT operation_id, event_id, published_at FROM outbox_messages'
$C exec rabbitmq rabbitmqctl list_queues name messages
$C logs --no-log-prefix workorders | jq -c 'select(.phase) | {ts, phase, operation_id, event_id}'
$C down -v
```

## Failure injection points

All fault injection is local-lab only. The request header is honoured only when `E01_FAULT_INJECTION_ENABLED=true` (set in `compose.yaml`). Otherwise the API answers `400` instead of silently ignoring the header.

| Trigger | Where exactly | Effect |
| --- | --- | --- |
| `X-Fault-Inject: crash-after-commit` | Immediately after `COMMIT` returns, before any further step (`WorkOrderService.CrashAfterCommitIfRequested`) | The process sends **SIGKILL** to itself: no `finally` blocks, no graceful shutdown. Exit code 137. The client sees a dropped connection. In Docker the service runs with `init: true` so the kill is not ignored for PID 1. Log lines are flushed first; that is the only thing that happens between `COMMIT` and the kill. |
| `X-Fault-Inject: fail-before-commit` | After the INSERTs, before `COMMIT` | The transaction is rolled back and the API answers `500` with the attempted `work_order_id` / `event_id`, so you can check that they exist nowhere. |
| `docker compose stop rabbitmq` | Broker | The broker is unreachable. Outbox writes are still accepted. The publisher logs `broker_unavailable`, backs off (1 s doubling to 10 s) and leaves rows pending. |
| `E01_OUTBOX_PUBLISHER_ENABLED=false` | Outbox publisher | No publisher runs in that process ("publisher stopped"). Rows stay pending until a process with the publisher enabled starts. |

## What the script does, and what was observed

The full output of a run is long. The excerpts below are trimmed from the final 2026-10-22 run. IDs and timestamps differ on every run.

### A. Baseline (naive dual write, UNSAFE): kill after COMMIT

1. Start in `naive` mode. A control request `op-naive-ok` commits and publishes (`201`, `event_status: "published"`).
2. `op-naive-crash` with `crash-after-commit`: `curl: (52) Empty reply from server`, container `exited (exit code 137)`.

**Expected:** the work order exists, its event does not.
**Observed:**

```text
  operation_id  | write_mode | outbox_entry | event_in_broker
----------------+------------+--------------+------------------
 op-naive-ok    | naive      | (none)       | yes
 op-naive-crash | naive      | (none)       | NO  <-- no event

    queue e01.work-order-created: 1 message(s)
      message_id=01a16299-eb6e-7c9f-823f-281182da09df  type=WorkOrderCreated  operation_id=op-naive-ok  work_order_id=01a16299-eb6e-...

    07:53:36.960  info     db_committed      op-naive-crash   01a16299-ebbf-76ae-83aa-90b1ffa7dff4
    07:53:36.960  warn     fault_injected    op-naive-crash   01a16299-ebbf-76ae-83aa-90b1ffa7dff4  fault=crash-after-commit
    07:53:36.960  critical process_killed    op-naive-crash   01a16299-ebbf-76ae-83aa-90b1ffa7dff4  fault=crash-after-commit
```

There is no `publish_started` line for `op-naive-crash`.

Nothing in the system remembers that an event was ever owed: the intent to publish lived only in the memory of the killed process. Restarting the service cannot bring it back. Only an external comparison of database and broker state, like the reconciliation table above, can even detect the gap.

### B. Mitigation (transactional outbox): broker down, kill after COMMIT, restart

1. Stop RabbitMQ. Start in `outbox` mode.
2. `op-outbox-broker-down` is accepted (`201`, `event_status: "pending"`). The publisher claims it and fails: `broker_unavailable attempt=1 retry_in_ms=1000 BrokerUnreachableException`.
3. `op-outbox-crash` with `crash-after-commit`: exit code 137, the same kill point as in A.
4. Before restart: two outbox rows with `published_at` NULL, and the broker is stopped.
5. Start RabbitMQ: the queue still holds only A's single message. Then restart WorkOrders.

**Expected:** after restart and broker recovery, both pending rows are published and marked.
**Observed:**

```text
--- B6. Evidence BEFORE restart   (event_type column omitted; broker: STOPPED)
     operation_id      |               event_id               |          created_at           | published_at
-----------------------+--------------------------------------+-------------------------------+--------------
 op-outbox-broker-down | 01a16299-f9da-7cfb-baa6-139e6e98373d | 2026-10-22 07:53:40.572677+00 |
 op-outbox-crash       | 01a16299-fa81-7e34-839b-669207234905 | 2026-10-22 07:53:40.73752+00  |

--- B8. Restart WorkOrders; wait until no outbox row is pending
    pending outbox rows: 2 -> 0 (after 1 check(s) at 0.5 s intervals)

--- B9. Evidence AFTER restart
     operation_id      |               event_id               |          created_at           |         published_at
-----------------------+--------------------------------------+-------------------------------+-------------------------------
 op-outbox-broker-down | 01a16299-f9da-7cfb-baa6-139e6e98373d | 2026-10-22 07:53:40.572677+00 | 2026-10-22 07:53:48.034373+00
 op-outbox-crash       | 01a16299-fa81-7e34-839b-669207234905 | 2026-10-22 07:53:40.73752+00  | 2026-10-22 07:53:48.034373+00

    queue e01.work-order-created: 3 message(s)
      message_id=01a16299-eb6e-7c9f-...  operation_id=op-naive-ok
      message_id=01a16299-f9da-7cfb-baa6-139e6e98373d  operation_id=op-outbox-broker-down
      message_id=01a16299-fa81-7e34-839b-669207234905  operation_id=op-outbox-crash

--- B10. Reconciliation (database vs broker)
     operation_id      | write_mode | outbox_entry | event_in_broker
-----------------------+------------+--------------+------------------
 op-naive-ok           | naive      | (none)       | yes
 op-naive-crash        | naive      | (none)       | NO  <-- no event
 op-outbox-broker-down | outbox     | published    | yes
 op-outbox-crash       | outbox     | published    | yes
```

The RabbitMQ `message_id` of each delivered message equals the outbox `event_id`. The restarted process logs `outbox_claimed count=2` → `broker_connected` → `publish_confirmed` (twice) → `outbox_marked_published count=2`.

### C. Rollback: failure before COMMIT

`op-outbox-rollback` with `fail-before-commit` returns `500` with `attempted_work_order_id` and `attempted_event_id`.

**Expected:** neither ID exists, and no event is published.
**Observed:** `work_orders = 0`, `outbox_rows = 0`. The queue depth stays at 3 after two seconds of publisher polling. Logs: `fault_injected` → `db_rolled_back`.

### Summary printed by the script

```text
A. naive baseline
  OK   process killed by SIGKILL after COMMIT (exit code)             expected=137 observed=137
  OK   op-naive-crash work order committed                            expected=1 observed=1
  OK   op-naive-crash WorkOrderCreated in broker (lost event)         expected=0 observed=0
  OK   op-naive-ok control event in broker                            expected=1 observed=1
B. transactional outbox
  OK   process killed by SIGKILL after COMMIT (exit code)             expected=137 observed=137
  OK   (a) outbox work orders without an outbox entry                 expected=0 observed=0
  OK   outbox rows pending before restart                             expected=2 observed=2
  OK   (b) outbox rows pending after restart + broker restored        expected=0 observed=0
  OK   (b) outbox events that reached the broker                      expected=2 observed=2
C. rollback
  OK   (c) op-outbox-rollback work orders                             expected=0 observed=0
  OK   (c) op-outbox-rollback outbox rows                             expected=0 observed=0
  OK   (c) op-outbox-rollback events in broker                        expected=0 observed=0
  OK   queue depth unchanged by the rolled-back request               expected=3 observed=3
```

## How to read the evidence

- **Reconciliation table.** It joins `work_orders` with `outbox_messages` and with the `work_order_id`s found in the broker queue. `NO  <-- no event` marks a committed work order without a broker event. `PENDING` marks an event whose publish intent is durable but not yet delivered.
- **Queue depth** comes from `rabbitmqctl list_queues`, which asks the queue process directly. Management API message counts are sampled periodically and can lag by seconds, so the script uses the management API only to **peek** at messages (`ackmode: ack_requeue_true`, which leaves them in the queue).
- **Evidence queue.** `e01.work-order-created` is a durable queue bound to the `e01.work-orders` exchange. It stands in for a downstream consumer. Nothing consumes it, so it accumulates every event that actually reached the broker. Messages are published with `mandatory=true`, so an unroutable message counts as a failed publish, not a success.
- **Logs** are one flat JSON object per line. The fields are `ts`, `level`, `category`, `phase`, `mode`, `operation_id` (from the `X-Operation-Id` header, also stored on the rows and sent as AMQP `correlation_id`), `work_order_id`, `event_id` (stored as `outbox_messages.event_id`, sent as AMQP `message_id`), `count`, `attempt`, `retry_in_ms`, `fault`, `msg` and `error`.

| `phase` | Meaning |
| --- | --- |
| `startup` | Mode and toggles. In naive mode a `warn` line says `UNSAFE BASELINE`. |
| `db_committed` | `COMMIT` returned. In outbox mode it also says `outbox_status=pending`. |
| `fault_injected`, `process_killed` | Injected fault, then the SIGKILL. |
| `db_rolled_back` | The fail-before-commit transaction was rolled back. |
| `publish_started`, `publish_confirmed`, `publish_failed` | Broker publish and its confirm (or failure). |
| `outbox_claimed`, `outbox_marked_published` | The publisher claimed `count` pending rows, then marked `count` rows published. |
| `broker_connected`, `broker_unavailable` | Broker connection established, or connection failed (with `attempt` and `retry_in_ms`). |

Sample log lines (trimmed):

```json
{"ts":"…","level":"info","category":"WorkOrders.WorkOrderService","mode":"naive","operation_id":"op-naive-crash","work_order_id":"01a16299-ebbf-…","event_id":"01a16299-ebbf-76ae-…","phase":"db_committed","msg":"phase=db_committed Work order committed; event is NOT yet published (separate step)"}
{"ts":"…","level":"critical","category":"WorkOrders.ProcessTerminator","mode":"naive","operation_id":"op-naive-crash","work_order_id":"…","event_id":"…","phase":"process_killed","fault":"crash-after-commit","msg":"phase=process_killed fault=crash-after-commit Killing process with SIGKILL now"}
{"ts":"…","level":"warn","category":"WorkOrders.OutboxPublisherWorker","phase":"broker_unavailable","attempt":1,"retry_in_ms":1000,"msg":"phase=broker_unavailable attempt=1 retry_in_ms=1000 Outbox rows remain pending","error":"BrokerUnreachableException: None of the specified endpoints were reachable"}
```

## Automated tests

`tests/WorkOrders.Tests` runs against the real PostgreSQL and RabbitMQ from compose. It uses a dedicated database (`e01_tests`) and dedicated exchange/queue names, so it never touches the script's state. Crash tests start the real service as a **separate OS process** and SIGKILL it. A restart is a fresh process.

| Test | Criterion |
| --- | --- |
| `Baseline_naive_kill_after_commit_leaves_committed_work_order_without_event` | Baseline failure: 2 committed naive work orders, 1 broker event, and no event for the killed request. |
| `A_every_committed_work_order_has_exactly_one_durable_outbox_entry` | (a) 40 concurrent outbox writes, a quarter failing before commit. The 30 committed work orders map to exactly one matching outbox row each, with no orphans. |
| `A_B_outbox_entry_survives_kill_after_commit_and_is_published_after_restart_once_broker_is_reachable` | (a) + (b) The broker is unreachable (port 1) and the publisher fails. After the kill, 3 rows are pending. After a fresh process starts with the broker reachable, all 3 are published, and the `message_id`s equal the `event_id`s. |
| `C_rolled_back_work_order_creates_no_outbox_event` | (c) After fail-before-commit, the attempted IDs are absent from both tables. A publisher pass publishes nothing. The queue is empty. |
| `Outbox_rows_are_immutable_except_a_single_published_at_transition` | The schema trigger rejects payload updates and a second `published_at` change. |

Output of `./experiments/transactional-outbox/test-e01.sh` (2026-10-22; the app's own log lines are omitted):

```text
  Passed WorkOrders.Tests.LostEventTests.Outbox_rows_are_immutable_except_a_single_published_at_transition [83 ms]
  Passed WorkOrders.Tests.LostEventTests.Baseline_naive_kill_after_commit_leaves_committed_work_order_without_event [355 ms]
  Passed WorkOrders.Tests.LostEventTests.C_rolled_back_work_order_creates_no_outbox_event [16 ms]
  Passed WorkOrders.Tests.LostEventTests.A_every_committed_work_order_has_exactly_one_durable_outbox_entry [46 ms]
  Passed WorkOrders.Tests.LostEventTests.A_B_outbox_entry_survives_kill_after_commit_and_is_published_after_restart_once_broker_is_reachable [946 ms]

Test Run Successful.
Total tests: 5
     Passed: 5
```

As a sanity check, the suite was also run against two deliberately broken variants: one that skips the outbox INSERT and one that commits instead of rolling back on the injected failure. Each made the relevant tests fail (3 of 5 and 2 of 5).

## Publisher design

1. Inside one transaction, `SELECT … WHERE published_at IS NULL ORDER BY created_at, event_id LIMIT 10 FOR UPDATE SKIP LOCKED`. Concurrent publishers skip each other's rows. If a publisher dies, its transaction aborts, the row locks vanish and the rows are simply pending again. No lease column or stale-claim sweeper is needed.
2. For each row, publish with publisher confirms (`BasicPublishAsync` returns after `basic.ack`; nack, return or a 5 s timeout throws).
3. `UPDATE … SET published_at = now()` for the confirmed rows, then `COMMIT`. If a publish fails mid-batch, the rows confirmed so far are still marked, and the rest stay pending.

Trade-off: the transaction stays open while the publisher waits for broker confirms, so batches are small. A lease-based claim (`claimed_until`) avoids long transactions at the cost of more moving parts.

## Limitations (read before citing results)

- **No exactly-once.** The outbox gives **at-least-once publication**. If the broker confirms a message and the process dies before marking the row, or a confirm is lost or times out, the same `event_id` is published again. Consumers must deduplicate; that is **E02**. This experiment does not inject that window. The tests accept duplicates in the broker (they compare *distinct* `message_id`s) and do not claim their absence.
- **External side effects are out of scope.** Only the database write and the broker publish are covered. E-mails, HTTP calls to third parties and the like are not protected by this transaction.
- **Pending is not delivered.** In outbox mode `201` with `event_status: "pending"` means the publish *intent* is durable, not that anyone received the event. Publication latency depends on polling (500 ms) and backoff (up to 10 s).
- **The client's uncertainty is not solved.** After a kill, the client sees a dropped connection and cannot know whether the work order was committed. A blind retry would create a second work order. That is an idempotency question, not an outbox one.
- **Ordering** is best-effort (`created_at, event_id`) for a single publisher. Retries and concurrent publishers can reorder events.
- **What is simulated:** SIGKILL of the application process, and an unreachable broker (stopped container, or a refused port in tests). Not simulated: a PostgreSQL crash, an unknown `COMMIT` outcome (connection lost during `COMMIT`), network partitions, broker disk loss, multi-node RabbitMQ or quorum queues.
- **The baseline has a second failure path** that the script does not exercise: in naive mode with the broker down, `COMMIT` succeeds and the publish fails. The API answers `503` and the work order again has no event. Try it with `docker compose stop rabbitmq` in naive mode.
- **Not implemented:** retention of published outbox rows, `LISTEN/NOTIFY` wake-ups, metrics.
- Credentials (`lab/lab`) and fault injection are for local experiments only.

## Failure analysis (brief)

The baseline loses the event because the decision to publish exists in two places that cannot commit together: PostgreSQL's durable state, and the process's volatile memory. A crash between them leaves no trace of the obligation, so there is nothing to retry. The outbox moves the obligation into the same atomic commit as the state change. After that point a crash, a stopped publisher or an unavailable broker only **delays** publication; recovery is a restart that finds pending rows. The cost is moving the failure from "lost" to "possibly duplicated", which is why E02 follows.
