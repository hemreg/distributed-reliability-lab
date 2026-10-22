# Distributed Reliability Lab

**A hands-on laboratory for exploring failure modes, consistency boundaries, and recovery strategies in distributed systems.**

[🇹🇷 Türkçe](README.tr.md) · **English**

This repository accompanies the *Reliability in Distributed Systems* article series at [emregorgulu.com](https://emregorgulu.com). The objective is not to promote a stack: it is to reproduce failures, observe their consequences, and evaluate architectural countermeasures.

> **Project status:** Design and experiment specifications. Runnable demonstrations have not yet been implemented.

## Research questions

- What happens when a database transaction commits but the corresponding event is not published?
- Can a message be delivered twice even if an outbox is used?
- What guarantees can an idempotent consumer provide—and where do those guarantees end?
- When do retries aid recovery, and when do they amplify an outage?

## Planned experiments

| Scenario | Failure injection | Objective |
| --- | --- | --- |
| **Lost Event** | Crash after database commit, before broker publish | Compare naïve dual-write with transactional outbox |
| **Duplicate Delivery** | Crash after broker ack, before outbox status update | Observe at-least-once publication and consumer deduplication |
| **Retry Storm** | Induce overload, latency and transient errors | Compare immediate retries with bounded backoff and jitter |

Read the [experiment specifications](docs/experiments.md) for invariants and acceptance criteria.

## Proposed stack

- **.NET** for API and workers
- **PostgreSQL** for transactional state and outbox
- **RabbitMQ** for asynchronous messaging
- **Docker Compose** for repeatable local experiments
- **Automated tests and structured logging** for evidence

The stack is an implementation choice, not the subject of the research. Reliability principles should remain technology-independent.

## Planned layout

```text
distributed-reliability-lab/
├── experiments/
│   ├── idempotency/
│   ├── transactional-outbox/
│   └── retry-resilience/
├── infrastructure/
├── docs/
│   ├── experiments.md
│   └── experiments.tr.md
├── README.md
└── README.tr.md
```

Experiment and infrastructure directories will be introduced with runnable code rather than empty placeholders.

## Principles

1. **Make failure explicit:** document injection points and observable outcomes.
2. **Compare baseline and mitigation:** demonstrate the unsafe behavior before introducing a pattern.
3. **Avoid exaggerated guarantees:** outbox does not mean exactly-once delivery; retries are not inherently safe.
4. **Plan for recovery:** address uncertain outcomes, process restarts, and stale claims.
5. **Show evidence:** do not claim an experiment has passed until it has been executed and tested.

## Accompanying articles

- Idempotency in Distributed Systems — duplicate logical requests and business effects.
- Transactional Outbox — making event publication intent durable.
- Retry, Timeout, and Backoff — planned follow-up on safe retries.

Links to the individual bilingual articles will be added when verified.

## Contributions

Well-scoped issues, failure reproductions, test cases, and corrections to architectural claims are welcome.

## License

No license has been selected yet. Public visibility does not by itself grant permission to reuse or modify the code. A suitable license will be added before releasing reusable implementations.
