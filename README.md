# Distributed Reliability Lab

**A hands-on laboratory for exploring failure modes, consistency boundaries, and recovery strategies in distributed systems.**

[🇹🇷 Türkçe](README.tr.md) · **English**

This repository accompanies the *Reliability in Distributed Systems* article series at [emregorgulu.com](https://emregorgulu.com). The objective is not to promote a stack: it is to reproduce failures, observe their consequences, and evaluate architectural countermeasures.

> **Project status:** E01 (Lost Event) and E03 (Retry Storm) are runnable. Their experiments and automated tests have been executed; see [experiments/transactional-outbox](experiments/transactional-outbox/README.md) and [experiments/retry-resilience](experiments/retry-resilience/README.md). E02 is specified but not yet implemented.

## Research questions

- What happens when a database transaction commits but the corresponding event is not published?
- Can a message be delivered twice even if an outbox is used?
- What guarantees can an idempotent consumer provide—and where do those guarantees end?
- When do retries aid recovery, and when do they amplify an outage?

## Experiments

| Scenario | Failure injection | Objective | Status |
| --- | --- | --- | --- |
| **E01 Lost Event** | Crash after database commit, before broker publish | Compare naïve dual-write with transactional outbox | [Runnable](experiments/transactional-outbox/README.md) |
| **E02 Duplicate Delivery** | Crash after broker ack, before outbox status update | Observe at-least-once publication and consumer deduplication | Planned |
| **E03 Retry Storm** | E03-A: a window of latency and random transient errors; E03-B: a freeze and restart that fails many clients at the same instant | Compare immediate retries, bounded backoff, jitter and a retry budget | [Runnable](experiments/retry-resilience/README.md) |

Read the [experiment specifications](docs/experiments.md) for invariants and acceptance criteria.

### Try an experiment

Each runnable experiment starts with a single command from its own directory; only Docker (Compose v2) is needed:

```bash
cd experiments/transactional-outbox    # E01: starts both write paths; the README walks you through the crash
docker compose up --build

cd experiments/retry-resilience        # E03: runs scenarios E03-A and E03-B (~4-5 min), prints the tables, then stops
docker compose up --build
```

Each experiment's README explains the failure that is injected, what is measured and how to read the result. Clean up with `docker compose down` (E01: `docker compose down -v`, which also removes its data).

## Stack

- **.NET** for API and workers
- **PostgreSQL** for transactional state and outbox
- **RabbitMQ** for asynchronous messaging
- **Docker Compose** for repeatable local experiments
- **Automated tests and structured logging** for evidence

The stack is an implementation choice, not the subject of the research. Reliability principles should remain technology-independent.

## Layout

```text
distributed-reliability-lab/
├── experiments/
│   ├── transactional-outbox/   # E01 Lost Event: .NET service, scripted run, xUnit tests
│   └── retry-resilience/       # E03 Retry Storm (E03-A, E03-B): simulated dependency, load generator, xUnit tests
├── infrastructure/
│   └── compose.yaml            # shared PostgreSQL + RabbitMQ for local experiments
├── docs/
│   ├── experiments.md
│   └── experiments.tr.md
├── README.md
└── README.tr.md
```

`experiments/idempotency/` (E02) is planned. Like the existing directories, it will be added together with runnable code, not as an empty placeholder.

## Principles

1. **Make failure explicit:** document injection points and observable outcomes.
2. **Compare baseline and mitigation:** demonstrate the unsafe behavior before introducing a pattern.
3. **Avoid exaggerated guarantees:** outbox does not mean exactly-once delivery; retries are not inherently safe.
4. **Plan for recovery:** address uncertain outcomes, process restarts, and stale claims.
5. **Show evidence:** do not claim an experiment has passed until it has been executed and tested.

## Accompanying articles

1. [Idempotency in Distributed Systems](https://emregorgulu.com/en/articles/idempotency-in-distributed-systems/) ([Türkçe](https://emregorgulu.com/tr/makaleler/dagitik-sistemlerde-idempotency/)) — duplicate logical requests and business effects.
2. [Transactional Outbox](https://emregorgulu.com/en/articles/transactional-outbox/) ([Türkçe](https://emregorgulu.com/tr/makaleler/transactional-outbox/)) — making event publication intent durable; E01 provides its evidence.
3. [Retry, Timeout, and Backoff: When Should We Stop Retrying?](https://emregorgulu.com/en/articles/retry-timeout-and-backoff/) ([Türkçe](https://emregorgulu.com/tr/makaleler/retry-timeout-ve-backoff/)) — safe retries, backoff, jitter and retry budgets; E03 provides its evidence.

## Contributions

Well-scoped issues, failure reproductions, test cases, and corrections to architectural claims are welcome.

## License

Released under the [MIT License](LICENSE). The experiments are teaching material: unsafe baselines are deliberately unsafe and intended for local use only.
