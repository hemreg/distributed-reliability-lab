# Experiment specifications

**E01 is implemented and runnable** (see [experiments/transactional-outbox](../experiments/transactional-outbox/README.md)). **E02 and E03 are planned**, not yet implemented. Each must be reproducible from a clean Docker Compose environment with explicit failure toggles, traceable correlation IDs, and assertions.

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

## E03 — Retry Storm

**Baseline:** Simulate a dependency with finite capacity, increasing latency, and transient errors. Clients retry immediately.

**Inject:** Increase failure rate and latency while maintaining a controlled request load.

**Mitigation:** Bound attempts with deadlines, exponential backoff with jitter, and a retry budget; separately consider overload rejection and circuit-breaking policies.

**Acceptance:** Report attempt amplification, completed logical operations, dependency traffic, latency distribution, and rejected/exhausted requests. Compare strategies with the same initial workload and randomized-seed assumptions; do not claim that backoff alone guarantees stability.

## Evidence format

For each experiment include: prerequisites, launch/teardown commands, precise failure trigger, expected state before/after restart, assertions, sample log fields (`event_id`, `operation_id`, `attempt`, `phase`), limitations, and a brief failure analysis.

## Implementation order

Build E01 first, then E02 using the same infrastructure, then E03. Keep unsafe examples obviously labeled and local-only.
