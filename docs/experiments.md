# Experiment specifications

**E01, E02 and E03 are implemented and runnable** (see [experiments/transactional-outbox](../experiments/transactional-outbox/README.md), [experiments/idempotency](../experiments/idempotency/README.md) and [experiments/retry-resilience](../experiments/retry-resilience/README.md)). Each must be reproducible from a clean Docker Compose environment with explicit failure toggles, traceable correlation IDs, and assertions.

## E01 — Lost Event

**Baseline:** Commit a work order to PostgreSQL; publish `WorkOrderCreated` separately to RabbitMQ.

**Inject:** Terminate the API immediately after COMMIT, before publish.

**Expected baseline failure:** Work order exists without a broker event.

**Mitigation:** Persist the work order and an immutable outbox event in the same database transaction. A worker publishes pending events.

**Acceptance:** (a) Every committed work order has a corresponding durable outbox entry; (b) a restart eventually publishes pending entries when broker connectivity is restored; (c) a rolled-back work order creates no committed outbox event. Do **not** imply exactly-once delivery.

**Implementation:** [experiments/transactional-outbox](../experiments/transactional-outbox/README.md). Run it with `./experiments/transactional-outbox/run-e01.sh` and test it with `./experiments/transactional-outbox/test-e01.sh`.

## E02 — Duplicate Delivery

**Inject:** Allow broker confirmation, then terminate publisher before marking the outbox entry published.

**Expected observation:** Same **event ID** may be published again after restart.

**Mitigation:** Consumer maintains a processed-message ID with a database uniqueness constraint in the **same transaction** as its business-state update.

**Acceptance:** Broker may observe multiple deliveries; consumer's database business effect occurs once for the same event ID. External side effects are explicitly outside that transaction and require separate safeguards.

**Implementation:** [experiments/idempotency](../experiments/idempotency/README.md). Start it with `cd experiments/idempotency && docker compose up --build`, run the scripted experiment with `./experiments/idempotency/run-e02.sh` (27 checks) and test it with `./experiments/idempotency/test-e02.sh` (12 tests). The business effect is a spare-part reservation: each `WorkOrderCreated` reserves one seal kit and lowers that consumer's stock by one. Two crash points are injected as real process kills: the outbox worker after the publisher confirm and before marking the row published, and a consumer after its `COMMIT` and before its ack. A naive consumer (unsafe baseline) and an idempotent consumer (`processed_messages` with `PRIMARY KEY (consumer, event_id)`, inserted with `ON CONFLICT DO NOTHING` in the same transaction as the reservation and the stock update) receive the same deliveries side by side. Observed: two broker copies of one event ID, two reservations from the naive consumer and one from the idempotent consumer, the same after a redelivery (`redelivered=true`), and no change after restarting the worker and both consumers. Not covered: external side effects, poison messages, retention of processed IDs, concurrent consumer processes under load (concurrency is tested in process).

## E03 — Retry Storm

**Baseline:** Simulate a dependency with finite capacity, increasing latency, and transient errors. Clients retry immediately.

**Inject:** Increase failure rate and latency while maintaining a controlled request load.

**Mitigation:** Bound attempts with deadlines, exponential backoff with jitter, and a retry budget; separately consider overload rejection and circuit-breaking policies.

**Acceptance:** Report attempt amplification, completed logical operations, dependency traffic, latency distribution, and rejected/exhausted requests. Compare strategies with the same initial workload and randomized-seed assumptions; do not claim that backoff alone guarantees stability.

**Implementation:** [experiments/retry-resilience](../experiments/retry-resilience/README.md). Run both scenarios with `cd experiments/retry-resilience && docker compose up --build`, repeat them over five seeds with `./experiments/retry-resilience/repeat-e03.sh`, and test with `./experiments/retry-resilience/test-e03.sh`. All strategies share the article's retry loop (at most 4 attempts, 2 s per attempt) and a dependency with finite capacity and a bounded queue that rejects with 503.

- **E03-A — Transient failure under independent arrivals.** Open-loop Poisson arrivals (100 operations/s) and a 10 s window of higher latency and random injected 503s. Compares A (immediate retry, unsafe baseline), B (exponential backoff) and C (backoff + full jitter + a 5 s per-operation deadline). Failures are not synchronised here, so the comparison shows how the waiting changes *when* retries arrive under a sustained incident.
- **E03-B — Correlated failure and recovery pressure.** A closed-loop population of 200 clients (about 100 operations/s). The dependency freezes for 1.5 s, then restarts: every request inside (132-148 in the reported runs) fails at the same instant, and the restarted process has half capacity for 5 s. Compares A, B, C and D (C + a client-side retry budget: 0.1 token per new operation, at most 10), all with the same 5 s deadline. Reports success, total load, peak load per second and per 100 ms, requests inside the dependency, attempts, p95/p99 and recovery time as separate metrics.

Attempt limits, timeouts, deadlines, backoff with jitter and a retry budget are implemented. **A circuit breaker is not**; overload rejection is the dependency's bounded queue, and a deep-queue variant of E03-A is documented.

## Evidence format

For each experiment include: prerequisites, launch/teardown commands, precise failure trigger, expected state before/after restart, assertions, sample log fields (`event_id`, `operation_id`, `attempt`, `phase`), limitations, and a brief failure analysis.

## Implementation order

Build E01 first, then E02 using the same infrastructure, then E03. (E03 needs no shared infrastructure and was implemented before E02.) All three are now implemented. Keep unsafe examples obviously labeled and local-only.
