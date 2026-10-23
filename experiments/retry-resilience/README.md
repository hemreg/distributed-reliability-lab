# E03 — Retry Storm (retry, backoff, jitter, retry budget)

[🇹🇷 Türkçe](README.tr.md) · **English**

> **Status:** runnable. Both scenarios, the repeat script and the test suite below were executed on 2026-10-22 with the pinned versions listed here. The numbers come from one laptop. Results from other machines are not claimed.

E03 has two scenarios. They share the simulated dependency (10 workers x 40 ms = 250 req/s, a queue of 100, then 503), the article's retry loop (at most 4 attempts, 2 s per attempt) and the strategy definitions. They differ in how the failure reaches the clients.

| | Scenario | Workload | Failure | Strategies |
| --- | --- | --- | --- | --- |
| **E03-A** | [Transient failure under independent arrivals](#e03-a--transient-failure-under-independent-arrivals) | open-loop Poisson, 100 ops/s | 10 s of slower service and 50% random 503s | A, B, C |
| **E03-B** | [Correlated failure and recovery pressure](#e03-b--correlated-failure-and-recovery-pressure) | 200 clients, closed loop, about 100 ops/s | a 1.5 s freeze, then a restart that fails every request inside at the same instant, then 5 s at half capacity | A, B, C, D |

Strategies: **A** immediate retry (**unsafe baseline**, it exists only to show the failure), **B** exponential backoff 200/400/800 ms, **C** exponential backoff with full jitter (the article's code), **D** (E03-B only) C plus a client-side retry budget.

### Short answer

Backoff and jitter change the temporal distribution of retries. What that does to the system depends on the workload and on the capacity limits:

- **E03-A, independent arrivals and a sustained 10 s failure.** Every strategy sent about 1.4-1.55 requests per logical operation; backoff moved the retries in time but did not reduce their number. Immediate retry (A) had the lowest success for the operations that started during the failure, B the highest, C in between with the lowest p99 in four of five seeds. That order held within each of five seeds, but the seed moved each strategy's result by 5-8 points, more than the 2-5 points between strategies.
- **E03-B, a correlated failure** (132-148 requests fail at the same instant, the restarted process admits 110 and serves 125 req/s for 5 s). The synchronisation is real: B's retries arrived as waves exactly at +200, +600 and +1400 ms; C spread them. In the main configuration B and C completed every operation and sent the same total load (within 13 requests per run). Jitter halved the peak per 100 ms (median 78 against 146) but not the peak per second (229 against 230), had fewer rejections and a lower p99. Immediate retry lost about 7% of the affected operations, all of them within 200 ms of the restart (all but one in the first 100 ms). The retry budget (D) cut retries to 10 and the peak to about 20 requests per 100 ms, and failed about 22% of the affected operations at once: its bucket (10 tokens) is far smaller than the cohort.
- **Where the waiting mattered more (single runs, seed 42).** With a small queue (20, so the burst is about 4.5x the admission limit), B's waves kept hitting a full queue: 86.9% of the affected operations succeeded with B against 94.8% with C. With a 2 s outage after the restart, or with more load than the cold start can carry, B and C had about the same success, and C sent more requests and had a higher per-second peak than B: full jitter halves the average wait, so it de-synchronises retries but also brings them sooner.
- **No waiting strategy bounded the amount of retry traffic; only the budget did,** by failing operations instead of retrying them. Which of these trade-offs is acceptable depends on the workload and on the capacity limits, not on the retry formula alone.

## Quick start (one command)

Requires Docker with Compose v2. From this directory:

```bash
docker compose up --build
```

That single command builds the images, starts the dependency and the load generator, and runs E03-A (A, B, C) and then E03-B (A, B, C, D). It prints each scenario's tables, writes `results/e03-a/` and `results/e03-b/` (`summary.json`, `timeseries.json`, `console.txt`), then stops both containers, and `docker compose up` returns with exit code 0. It took 246 s on the test machine after the first build (the first build takes a few minutes more). Afterwards, remove the stopped containers and the network:

```bash
docker compose down
```

The images are removed with `docker compose down --rmi local`. Every parameter can be overridden from the shell, for example `E03_SEED=7 docker compose up --build`, or one scenario only: `E03_SCENARIOS=b docker compose up --build` (see the parameter tables of [E03-A](#parameters-and-why) and [E03-B](#parameters-and-why-1)). To repeat both scenarios over five seeds: `./repeat-e03.sh` (about 21 minutes, see [Repeating with several seeds](#repeating-with-several-seeds)).

> If your laptop goes to sleep during a run, Docker's VM sleeps with it and `docker compose up` simply waits. This happened twice on the test machine during E03-A's first runs. Those runs were discarded. On macOS, `caffeinate -di docker compose up --build` prevents it; `./repeat-e03.sh` does this by itself.

What you see while it runs (trimmed):

```text
loadgen-1  | [A] Immediate retry (UNSAFE BASELINE): wait none: next attempt at once
loadgen-1  |   [A] t= 5-10 s  attempts sent    104/s   ops succeeded   103/s   failed     0/s
loadgen-1  |   [A] t=10-15 s  attempts sent    215/s   ops succeeded    60/s   failed    21/s
...
loadgen-1  | [B] Exponential backoff: wait 200, 400, 800 ms (no jitter); total 5 s per operation
loadgen-1  |  [B] requests at the dependency per 100 ms, 9.5-12.0 s (restart at 10.0 s): 7 7 8 9 11 11 3 143 6 11 8 36 4 6 1 0 4 5 2 8 3 5 2 5 3
```

The progress lines are live client-side numbers. The final tables and the JSON files are authoritative.

## E03-A — Transient failure under independent arrivals

The original E03 experiment, unchanged: same parameters, same behaviour, same output format (schema v1). Only its output directory moved to `results/e03-a/`, and its committed sample to `results/sample/e03-a/` (byte-identical).

### Question

A dependency degrades for ten seconds. Its clients retry failed calls. How much extra traffic do the retries create, how many logical operations still succeed, and does the waiting strategy between attempts change that?

Three client strategies are compared against the **same** dependency, under the **same** workload (identical arrival schedule) and the **same** injected failure:

| | Strategy | Wait before attempt 2, 3, 4 | Label |
| --- | --- | --- | --- |
| **A** | Immediate retry | none | **Unsafe baseline.** It exists only to show the failure. |
| **B** | Exponential backoff, no jitter | 200, 400, 800 ms | |
| **C** | Exponential backoff + full jitter + limits | random in [0, 200], [0, 400], [0, 800] ms; at most 5 s per logical operation | The article's code |

All three share the same retry loop: at most **4 attempts**, **2 s timeout per attempt**, and retries only on **408, 429, 502, 503, 504**, attempt timeouts and `HttpRequestException`. Only the waiting differs, plus C's total-time limit.

#### Short answer (E03-A, from the runs below)

- **The retries are the storm.** During the saturated part of the failure window the dependency received about **3x the offered load** (about 280-310 req/s for about 100 new operations/s) **with every strategy**. Over the whole run each strategy sent about 1.5 requests per logical operation.
- **Backoff did not reduce the number of retries; it moved them in time.** With the same attempt limit and a failure that lasts much longer than the backoff, amplification was within 0.07x across A, B and C (1.48-1.55x).
- **Immediate retry was the worst** for success: 68% of the operations that started during the failure succeeded, against 73% (C) and 76% (B). An immediate retry hits the same full queue that just rejected it.
- **Jitter did not help here. B succeeded slightly more often than C,** because B always waits 1.4 s in total and full jitter waits about half that on average, so more of B's last attempts land after the window has closed. C had the lowest p99 latency (about 3.0 s against 3.3-3.6 s for B).
- **Recovery was immediate for all three** (0-1 s), because this dependency sheds load with a bounded queue. With a deep queue (see [Sensitivity](#sensitivity-runs)), the picture changes: timeouts, wasted work, and C's deadline trades success for bounded latency.

None of this shows that jitter is useless. The failure here is not synchronised, and jitter mainly helps when many clients fail at the same instant. It shows that **backoff and jitter alone do not cap retry traffic**. A retry budget or a circuit breaker does that. E03-A implements neither; E03-B adds a retry budget (strategy D).

### How the experiment works

```text
loadgen (one container)                                dependency (one container)
  same Poisson arrival schedule for A, B, C              10 workers x 40 ms  = 250 req/s
  each logical operation = GetWithRetryAsync(...)  --->  queue of 100, beyond that: 503 at once
  attempt counting in a DelegatingHandler                failure window 10-20 s after each reset:
  latency from the scheduled arrival (open loop)           80 ms per request (125 req/s)
                                                           50% of served requests answered 503
                                                         per-second counters, measured here
```

For each strategy the load generator:

1. creates a fresh `HttpClient` (no connections inherited from the previous strategy),
2. resets the dependency with the failure window disabled and sends 2 s of single-attempt warm-up traffic at the same rate (not measured: JIT and connection setup),
3. waits until the dependency is idle and resets it again. This starts the dependency's clock and arms the failure window,
4. starts 3038 logical operations at the pre-computed Poisson times (open loop: arrivals never wait for earlier operations),
5. waits for every operation to finish, waits until the dependency is idle, and reads the dependency's per-second counters.

Traffic numbers come from the **dependency** (`/admin/stats`), not from the client. The client's own attempt count is kept as a cross-check. They matched exactly in every run, since no attempt was lost on the way.

#### The dependency

`src/Dependency` is an ASP.NET Core minimal API with one endpoint, `GET /work`:

- **Finite capacity.** 10 workers, 40 ms service time, so 250 requests/s. The work is a timer (`Task.Delay`), not CPU, so capacity is the same on any machine.
- **Bounded queue.** Up to 100 requests wait for a worker in FIFO order. Request number 111 is rejected at once with `503` (load shedding, no work done).
- **Failure window, 10-20 s after each reset.** The service time doubles to 80 ms (capacity 125 req/s), and 50% of the requests it serves are answered `503` *after* doing the work ("injected transient failure"). Whether a request is degraded is decided when its service starts.
- **Abandoned work is not cancelled.** If a client times out, the dependency still finishes the request and counts it as `abandoned`, as many real servers do. In the main configuration this never happens (no attempt times out). It matters in the deep-queue variant.
- **Lab-only admin endpoints** with no authentication: `POST /admin/reset`, `GET /admin/stats`, `POST /admin/shutdown`. During a run you can watch it with `curl -s http://127.0.0.1:18490/admin/stats | jq .totals`.

#### The client code (and how it differs from the article)

`src/LoadGenerator/RetryLoop.cs` contains the article's `GetWithRetryAsync`, shared by A, B and C. The **only** changes:

| Article | Here | Why |
| --- | --- | --- |
| `var capMs = Math.Min(200 * (1 << (attempt - 1)), 2000);`<br>`var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, capMs + 1));` | `var delay = delayAfterFailedAttempt(attempt);` | The wait is the one thing that differs between A, B and C. For C the delegate is `Backoff.FullJitter`, which contains the article's two lines unchanged... |
| `Random.Shared` | `new Random(seed * 1_000_003 + operationIndex)`, one per logical operation | ...except for a seeded `Random`, so that runs are reproducible. A shared `Random` is not thread-safe. A per-operation seed gives operation *i* the same jitter sequence in every run. |
| `const int maxAttempts = 4;`, `TimeSpan.FromSeconds(2)` | `RetryLoop.MaxAttempts = 4`, `RetryLoop.AttemptTimeout = 2 s` | Same values. They are exposed so the report and the tests can use them. |
| total operation time bounded ("toplam işlem süresi sınırlandırılır") | C's caller passes a `cancellationToken` that is cancelled **5 s** after the operation starts | No change inside the loop: the article's code already stops on that token (it is excluded from the retry filter, and `Task.Delay` honours it). A and B get no deadline. Their only bound is implicit: 4 x 2 s + 1.4 s = 9.4 s. |

Two more details are not in the article. `HttpClient.Timeout` is set to infinite, so the per-attempt 2 s timeout is the only timeout. An instrumentation `DelegatingHandler` counts every attempt and reads the response body inside the attempt, so the body read also falls under the 2 s timeout.

Strategy **B** is the article's cap formula **without** jitter: `Math.Min(200 * (1 << (attempt - 1)), 2000)` gives 200, 400 and 800 ms. So B and C have the same caps, and full jitter draws uniformly below them. One consequence matters when you read the results: **with full jitter, the average wait is half the cap.** When all three waits happen, C waits about 0.7 s in total on average where B always waits 1.4 s. C is not the "slowest, most patient" strategy.

### Parameters and why

All values are in `compose.yaml` (`x-e03-parameters`). The code defaults are the same, and every value is recorded in `summary.json`.

| Variable | Value | Why this value |
| --- | --- | --- |
| `E03_RATE` | 100 ops/s | Round number. It is 40% of normal capacity, comfortable headroom for a healthy service. |
| `E03_DURATION_S` | 30 s | 10 s healthy, 10 s failure, 10 s after. Long enough to see before, during and after, and short enough that all three strategies run in about 100 s. |
| `E03_SEED` | 42 | Arrival schedule, jitter and injected-failure draws. The same seed gives the same arrival schedule for every strategy. |
| `E03_WORKERS`, `E03_SERVICE_TIME_MS` | 10 x 40 ms = 250 req/s | Simple to reason about: capacity = workers / service time. |
| `E03_QUEUE_LIMIT` | 100 | 0.4 s of work at normal capacity, 0.8 s inside the window. Both are below the 2 s attempt timeout, so a queued request is never abandoned and the comparison isolates the waiting behaviour. A deep queue is tested separately. |
| `E03_FAILURE_START_S`, `E03_FAILURE_END_S` | 10 s to 20 s | Ten seconds is much longer than any backoff here (at most 1.4 s per operation). This is a sustained incident, not a blip. |
| `E03_FAILURE_SERVICE_TIME_MS` | 80 ms (125 req/s) | "Half capacity." The original load (100/s) still fits (80% utilisation). Without retries the dependency would **not** be overloaded. |
| `E03_FAILURE_ERROR_RATE` | 0.5 | "Half the served requests fail." With retries, the attempts needed exceed the reduced capacity (at 50% failure, up to 4 attempts gives 1.875 attempts per operation, so about 190 req/s against 125), and the dependency starts shedding load. That is the retry storm this experiment is about. |
| `E03_C_DEADLINE_MS` | 5000 | Two full attempt timeouts plus backoff. It binds only when attempts time out. In the main configuration it never bound (0 operations). |
| `E03_WARMUP_S` | 2 s | Enough for JIT and connection setup at the test rate. |
| `E03_STRATEGIES` | `A,B,C` | Run order. The reversed order was tested (no measurable effect). |
| `E03_ATTEMPT_LOG` | `false` | `true` also writes `results/e03-a/attempts.jsonl` (and `results/e03-b/attempts.jsonl`), one JSON line per attempt (`strategy`, `operation_id`, `attempt`, `phase`, `outcome`, `sent_ms`, `duration_ms`). It is about 4,500 lines per strategy. |
| Recovery criterion | ≥ 95% for 3 s | See the definition below. Fixed in code (`Metrics.cs`). |

**How the parameters were chosen, honestly.** The first configuration used a 25% error rate. In it the queue filled only near the end of the window. Amplification was 1.15-1.18x for all three strategies and the differences were small (table in [Sensitivity](#sensitivity-runs)). That is a mild brownout, not a retry storm. The error rate was then raised to 50% ("half capacity, half errors") so that retries exceed the degraded capacity. No parameter was changed after that to favour any strategy, and the ranking did not depend on it.

### Observed results

Machine: MacBook (Apple Silicon), Docker Desktop 28.4.0 / Compose 2.39.4, 12 CPUs and about 8 GB of memory visible to the Docker VM. Date: 2026-10-22. Four valid runs of `docker compose up --build` with the default parameters (seed 42): three in a row, plus a final verification run after the image had been removed.

Console output of the third run (also in `results/sample/e03-a/console.txt`):

```text
====================================================================================================
E03 Retry Storm: same workload, same failure window, three client strategies
====================================================================================================
Strategy                       Requests   Ampl.  Success  in window      p95      p99 Peak rps Recovery
----------------------------------------------------------------------------------------------------
A  immediate retry (UNSAFE)        4535   1.49x    89.1%      68.4%  1833 ms  3542 ms      377      0 s
B  backoff 200/400/800 ms          4551   1.50x    92.0%      76.3%  2631 ms  3378 ms      339      0 s
C  backoff + jitter + limits       4648   1.53x    91.2%      73.7%  2129 ms  3033 ms      375      0 s
----------------------------------------------------------------------------------------------------
Same 3038 logical ops per strategy (Poisson 100/s, 30 s, seed 42). Dependency 250 req/s (10 x 40 ms), queue 100, then 503.
Failure 10-20 s: 125 req/s (80 ms), 50% injected 503. All: 4 attempts max, 2 s each; C also 5 s per op.
Requests: received by the dependency. Ampl.: requests per logical op. Success: ops answered 200 (in window: ops
started during the failure). p95/p99: end-to-end latency, all ops. Recovery: s after the window until per-second success >= 95% for 3 s.
```

**Variation across the four runs** (min-max):

| | Requests | Ampl. | Success | in window | p95 | p99 | Peak rps | Recovery |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 4508-4549 | 1.48-1.50x | 89.1-89.4% | 68.1-68.8% | 1814-1844 ms | 3443-3553 ms | 377-387 | 0 s |
| B | 4551-4640 | 1.50-1.53x | 91.6-92.0% | 75.2-76.3% | 2415-2832 ms | 3323-3620 ms | 339-358 | 0-1 s |
| C | 4610-4696 | 1.52-1.55x | 90.8-91.2% | 72.5-73.7% | 2070-2195 ms | 2969-3033 ms | 368-379 | 0 s |

Three earlier runs with the same client and dependency behaviour (before two reporting-only fields were added) fell inside the same ranges, for example in-window success A 68.9-69.0%, B 75.4-76.6%, C 72.7-73.4%. Within a run, the ranking B > C > A on in-window success held every time. The gaps (B over C: 2.6-3.4 points, C over A: 4.0-5.3 points) were larger than the run-to-run spread of each strategy (at most 1.2 points). The amplification differences are small (at most 0.07x within a run), but the order A < B < C held in every run, which fits C's shorter average waits leaving more attempts inside the window. B had the lowest one-second peak in every run (339-358 against 368-387).

In all runs: no attempt timed out, C's deadline cut no operation, and `abandoned` was 0. The dependency's peak in-flight was 110-111, i.e. full. Two further runs were discarded because the laptop went into idle sleep during them (verified in `pmset -g log`). Their numbers were consistent with the rest, but a paused run is not a clean measurement.

#### What the numbers say

1. **The storm comes from retries, not from the load.** Offered load stayed about 100 operations/s. In seconds 15-19 of the sample run the dependency received a mean of 281 (A), 310 (B) and 313 (C) req/s and rejected 57-61% of them at once (`rejected503`). Its peak in-flight sat at 110 (all workers busy, queue full).
2. **The waiting strategy did not change the volume.** Each operation that starts in the window has the same budget of four attempts, and the failure lasts far longer than any backoff. So in steady state every strategy sends the same attempts, only at different moments. Amplification ended within 0.07x (1.48-1.55x for the whole run, about 2.4-2.6 attempts per in-window operation).
3. **Immediate retry wastes its attempts.** When A's attempt is rejected because the queue is full, its retry arrives a millisecond later, sees the same full queue, and is rejected again. A exhausted its four attempts fastest (lowest p95, but 323-332 failed operations against 242-254 for B). For operations arriving at second 20, just after the window, A was the only strategy below 100% success (93-96%): its retries bounced off the backlog that was still draining.
4. **B beat C on success because it waits longer, not because jitter hurts.** For operations arriving in seconds 13-16, success by arrival second (`successPercentByArrivalSecond`) was in the same 38-75% band for all three. The consistent gap comes from operations that arrive in the last seconds of the window. At second 18, B reached 90-95% and C 80-83% (A: 67-73%). At second 19, B reached 100% and C 94-99% (A: 57-61%). An operation in the saturated window spends up to about 0.9 s per admitted attempt. Add B's fixed 1.4 s of waiting, and B's last attempts land after the failure has ended. C's full jitter waits about half as long on average, so more of its attempts are still inside the window.
5. **C had the lowest tail latency.** p99 was about 3.0 s, against 3.3-3.6 s for B and 3.4-3.6 s for A, because its waits are shorter on average.
6. **Recovery was immediate.** The bounded queue holds at most 0.4 s of work at normal capacity, so the backlog drained within the first second after the window for every strategy.

### Repeat over five seeds

`./repeat-e03.sh` also ran E03-A with seeds 42, 1, 2, 3 and 4: the configuration above, unchanged except the seed (5 runs, the current code, same machine, 2026-10-22). The seed changes the arrival schedule, the jitter draws and the injected-failure draws. Median [min-max] over the five runs, from `results/sample/repeat.txt` (every individual value is listed there):

| | Requests | Ampl. | Success | in window | p95 | p99 | Peak rps | Recovery |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 4274 [4164-4520] | 1.44 [1.40-1.49] | 90.6 [89.2-91.9]% | 71.4 [68.5-74.9]% | 1811 [1789-1820] ms | 3529 [3503-3608] ms | 375 [296-387] | 0 s |
| B | 4330 [4164-4656] | 1.47 [1.40-1.55] | 93.3 [91.9-94.0]% | 79.6 [76.0-81.4]% | 2762 [2607-3016] ms | 3642 [3285-3881] ms | 330 [282-350] | 0 s |
| C | 4435 [4293-4689] | 1.50 [1.44-1.55] | 91.9 [90.2-93.3]% | 75.3 [71.5-79.2]% | 2127 [2093-2135] ms | 3087 [2993-3296] ms | 351 [315-391] | 0 s |

- **Seed-to-seed variation is larger than the differences between strategies.** The seed moves each strategy's in-window success by 5-8 points (A 68.5-74.9%, B 76.0-81.4%, C 71.5-79.2%), so the ranges overlap.
- **Within each seed the order did not change.** In-window success was B > C > A in all five runs (B over C by 2.1-5.2 points, C over A by 3.0-5.1 points). Amplification was A ≤ B ≤ C in each run. C had the lowest p99 in four of five runs (seed 4: B 3285 ms, C 3296 ms). B had the lowest one-second peak in four of five (seed 3: A 296, B 330).
- No attempt timed out, nothing was abandoned, recovery was 0 s in every run. The seed-42 run of this repeat (in-window success 68.7 / 76.0 / 73.4%) fell inside the ranges of the earlier four runs.

Five runs are not a significance test. They show that the ranking above is not an artefact of seed 42, and that a single run cannot tell how large the gaps are.

### Sensitivity runs

Each was run once with `docker compose up` and the variable shown. They used the same client and dependency behaviour as the main runs.

| Variant | Command | A in window / p99 | B in window / p99 | C in window / p99 | Note |
| --- | --- | --- | --- | --- | --- |
| Different seed | `E03_SEED=7` | 66.5% / 3527 ms | 73.6% / 3531 ms | 70.4% / 3077 ms | Same ranking. Amplification 1.50 / 1.52 / 1.56x |
| Reversed order | `E03_STRATEGIES=C,B,A` | 68.7% / 3528 ms | 76.5% / 3632 ms | 73.1% / 3060 ms | No order effect |
| Milder failure | `E03_FAILURE_ERROR_RATE=0.25` | 96.0% / 1936 ms | 99.4% / 2648 ms | 98.0% / 2229 ms | Brownout, not a storm: amplification 1.15 / 1.15 / 1.18x |
| Deep queue | `E03_QUEUE_LIMIT=400` | 70.3% / 6756 ms | 89.5% / 7648 ms | 69.3% / 5003 ms | Timeouts appear; see below |

**Deep queue (`E03_QUEUE_LIMIT=400`).** Queueing delay now reaches 3.2 s inside the window, beyond the 2 s attempt timeout. From second 18-19 until the window ends, the per-second count of successful operations drops to zero while the dependency is 100% busy: it is finishing 540-660 requests whose clients have already given up (`abandoned`). This is congestion collapse caused by retries and timeouts. Recovery took 1 s (A), 2 s (B) and 3 s (C). C's 5 s deadline cut 183 operations: it bounded C's p99 at 5.0 s (A: 6.8 s, B: 7.6 s), but its in-window success fell to 69%. That is the trade-off a deadline makes: a fast, certain failure instead of a slow, possible success. B's longer fixed waits again let more operations outlive the window.

## E03-B — Correlated failure and recovery pressure

### Question

Many clients fail **at the same instant**, and a dependency with limited capacity has to recover while their retries come back. How do immediate retry, exponential backoff, full jitter and a retry budget change the *intensity* of the requests that reach the recovering dependency: how many, how concentrated in time, how many inside it at once, and how many logical operations still succeed?

E03-A could not answer this: its arrivals are Poisson and its errors random, so failures are spread out in time and there is nothing for jitter to de-synchronise. E03-B creates the synchronisation on purpose and then checks (with 100 ms buckets) that it is really there.

### Design and why

```text
loadgen: 200 clients, closed loop                         dependency: 10 workers x 40 ms = 250 req/s, queue 100 (admits 110)
  each client: one logical operation, then think            0    - 8.5 s  normal
  1-3 s (uniform), then the next operation                  8.5  - 10.0 s FREEZE: nothing completes, nothing is rejected,
  about 100 ops/s, as in E03-A                                            every request (in service, queued, new) is held
  same retry loop and strategies as E03-A                   10.0 s        RESTART: every request inside fails with 503 at once
  5 s total per logical operation for EVERY strategy        10.0 - 15.0 s COLD START: a fresh process, 80 ms per request (125 req/s)
                                                            15.0 s -      normal again
```

- **A fixed population, closed loop.** Each of the 200 clients runs one logical operation at a time and thinks 1-3 s (uniform, seeded per client) before the next one. 200 clients / 2 s gives about 100 operations/s, the same offered load as E03-A (40% of capacity). A closed loop is how a fixed set of callers behaves (worker threads, users, upstream instances): a client that is waiting for a retry sends no new work. Each client starts at a random moment in [0, 3) s, so the population does not start in lockstep. Consequence: the number of logical operations differs a little between strategies (a strategy that fails fast lets its clients start more operations), so the report shows the count of operations next to every rate.
- **A freeze, then a restart: the correlated failure.** From 8.5 s the dependency stops completing anything, as a process that hangs (for example a stuck lock, a long pause or a failover that holds connections). Requests pile up; even the admission limit does not apply, because a hung process does not run its own checks. At 10.0 s the process restarts and **every request inside fails at the same instant** (503). With the default timeline that was 132-148 requests in the runs below (`heldAtRestart`), about 70% of the population. Their retries all start from that instant.
- **The freeze is shorter than the 2 s attempt timeout.** If it were longer, each held request would time out 2 s after it was sent, one by one, spread in time like its arrival. 1.5 s keeps all of them waiting until the restart.
- **The restart creates a fresh process.** It has its own workers and an empty queue; requests that were torn down by the restart do not count against its admission limit (110 = 10 workers + 100 queued).
- **Cold start with reduced capacity.** For 5 s after the restart, service takes 80 ms instead of 40 ms (125 req/s), as caches, pools and JIT warm up. 125 req/s is above the offered load (about 100/s), so without retries and without the burst the dependency would cope. It is not enough to absorb the burst at once: the cohort of about 140 requests already exceeds the admission limit of 110, and a full queue takes 0.88 s to drain at cold speed.
- **No refusal period after the restart (outage 0 ms) in the main configuration.** A period in which every request is refused would decide the result by a coincidence of constants: with an outage longer than B's total backoff (0.2 + 0.4 + 0.8 = 1.4 s), every strategy's retries of the cohort fall into the outage; with a shorter one, B's last wave happens to land just after it. The hang already provides the outage. A 2 s refusal period is run as a sensitivity variant (`E03B_OUTAGE_MS=2000`).
- **The same conditions for every strategy:** the same 200 clients and think-time sequences, the same timeline, at most 4 attempts, 2 s per attempt and **5 s total per logical operation for all of A, B, C and D** (in E03-A only C has this deadline).
- **Capacity is a timer**, as in E03-A: the results do not depend on the host's CPU.
- **503 instead of reset connections.** A real restart drops TCP connections. Here the dependency answers 503, which the retry loop treats the same way (both are retried), keeps the connection pool intact and keeps the counting exact. Reconnect storms are not modelled.

### Strategies and the retry budget

| | Strategy | Wait before attempt 2, 3, 4 | Other limits |
| --- | --- | --- | --- |
| **A** | Immediate retry (**unsafe baseline**) | none | 4 attempts, 2 s each, 5 s per operation |
| **B** | Exponential backoff | 200, 400, 800 ms | same |
| **C** | Exponential backoff + full jitter | random in [0, 200], [0, 400], [0, 800] ms | same |
| **D** | C + retry budget | as C (the same random draws for the same operation) | same, plus: a retry needs a token from the budget |

A, B and C are the E03-A strategies with the deadline given to all of them. D is added as a fourth strategy, so that A, B and C stay comparable with E03-A and the budget's effect can be read against C, which differs only by the budget.

**The retry budget (D).** One token bucket shared by all 200 clients, as one calling service with many concurrent requests would have:

- each new logical operation adds **0.1 token** (the bucket holds at most **10** and starts full),
- each retry needs one whole token and removes it; without a token the operation ends at once with the last failure (`retry_budget_exhausted`),
- so over any period, retries are at most 10 + 0.1 x new operations: about **10% of the first attempts**, plus a burst of 10.

The ratio follows the per-client retry budget in Google's SRE book (chapter "Handling Overload": a request is retried only while retries are below 10% of requests). The deposit/withdraw token bucket is the form of Finagle's `RetryBudget` (`percentCanRetry`). gRPC's retry throttling (gRFC A6, example values `maxTokens` 10, `tokenRatio` 0.1) is a close variant that adds tokens on success and removes them on failure. The AWS Builders' Library article "Timeouts, retries, and backoff with jitter" describes limiting retries locally with a token bucket. None of these libraries is used; `src/LoadGenerator/RetryBudget.cs` is a minimal implementation of the idea. It plugs into the article's retry loop without changing it: D's wait function asks the budget before every retry and throws `RetryBudgetExhaustedException` when there is no token, which ends the loop.

### Parameters and why

| Variable | Value | Why this value |
| --- | --- | --- |
| `E03B_CLIENTS` | 200 | "Many clients": enough that about 70% of them (132-148) are inside the dependency when it restarts, which is more than the restarted process admits at once (110). |
| `E03B_THINK_MIN_MS`, `E03B_THINK_MAX_MS` | 1000-3000 ms, uniform | Mean 2 s, so about 100 operations/s, the same offered load as E03-A. Uniform and bounded, so no client is idle for very long. |
| `E03B_DURATION_S` | 30 s | 8.5 s of normal operation before the freeze, 15 s after the cold start. Short enough that A-D run in about 2.5 minutes. |
| `E03B_RESTART_MS` | 10000 | The correlated failure happens at 10.0 s, at the same moment as E03-A's window starts. |
| `E03B_FREEZE_MS` | 1500 | Below the 2 s attempt timeout (see above), long enough to collect about 70% of the population. |
| `E03B_OUTAGE_MS` | 0 | See "No refusal period" above. 2000 is a sensitivity run. |
| `E03B_COLD_MS`, `E03B_COLD_SERVICE_TIME_MS` | 5000 ms, 80 ms (125 req/s) | "Half capacity", as in E03-A's window, but no injected errors: the only errors in E03-B are the restart and the admission limit. Above the offered load, below what the burst needs. |
| `E03B_DEADLINE_MS` | 5000 | The same value as C's deadline in E03-A, now for every strategy. A held request uses up to 1.5 s of it. |
| `E03B_BUDGET_RATIO`, `E03B_BUDGET_MAX_TOKENS` | 0.1, 10 | 10% from the SRE book; 10 tokens as in gRFC A6's example. The bucket is far smaller than the cohort; that is a property of every ratio budget sized for normal traffic, not a tuning choice. |
| `E03B_STRATEGIES` | `A,B,C,D` | Run order. |
| `E03_WORKERS`, `E03_SERVICE_TIME_MS`, `E03_QUEUE_LIMIT`, `E03_SEED`, `E03_WARMUP_S` | as E03-A | Shared by both scenarios. |

**How the parameters were chosen, honestly.** All values above were fixed before the first E03-B run and were not changed afterwards. The first run (seed 42) showed the synchronisation working as intended, and one reporting problem: the client's clock started a few milliseconds after the dependency's reset, so client-side events at the restart (10.0 s) were counted in the 9.9 s bucket, and D's 127 failures at the restart landed in second 9. That made D's recovery time 0 s instead of 1 s. The fix aligns the E03-B client clock with the dependency's clock (the reset's elapsed time plus half its round trip; `clientClockOffsetMs` in `summary.json`, 1.9-14.4 ms in the reported runs). It changes bucketing only; client and dependency behaviour, and every total, were unchanged. First run before the fix: A 93.5% affected success, B and C 100%, D 78.0%; peak per 100 ms 221 / 142 / 69 / 19; requests 3024 / 2948 / 2956 / 2853; recovery 1 / 0 / 0 / 0 s. E03-A is unaffected: its clock is unchanged.

### Metrics (kept separate)

| Metric | Where | Meaning |
| --- | --- | --- |
| Success | `operations.successPercent`, table `Success` | Logical operations that ended with 200, of all started |
| Affected success | `byStartPhase.affected`, table `affected` | Success of the operations that started from the freeze to the end of the cold start (8.5-15 s) |
| Total load | `load.requestsAtDependency`, `load.amplification` | Requests received by the dependency; per logical operation |
| Load during recovery | `load.requestsDuringRecovery`, table `10-15 s` | Requests received from the restart to the end of the cold start |
| Instantaneous load | `load.peakRequestsPerSecond`, `load.peakRequestsPer100Ms` (+ `AtMs`) | Highest count in one 1 s bucket and in one 100 ms bucket. Synchronised waves are shorter than a second, so the per-second peak can hide them. |
| Concurrency | `concurrency.heldAtRestart`, `concurrency.peakActiveAfterRestart`, time series `inFlight` / `fine.active` | Requests inside the dependency: how many failed together at the restart, and the peak inside the restarted process (110 = full) |
| Attempts | `load.attemptsSent`, `load.retriesSent`, `load.retriesDenied`, `operations.attemptsHistogram` | Total attempts, retries (attempts 2-4), retries refused by the budget |
| Latency | `latencyMs.all.p95`, `.p99` | End-to-end per logical operation, from its start to its final result; **all** operations, succeeded and failed (`latencyMs.succeeded` / `.failed` hold the split) |
| Recovery | `recovery.seconds` | Seconds after the restart second (10) until the per-second success of completed operations is ≥ 95% for 3 s (same rule as E03-A) |

### Observed results (seed 42)

Same machine as E03-A (MacBook, Apple Silicon, Docker Desktop 28.4.0 / Compose 2.39.4, 12 CPUs and about 8 GB visible to Docker), 2026-10-23. Output of `E03_SCENARIOS=b docker compose up --build`, which prints the same E03-B section as the default run (also in `results/sample/e03-b/console.txt`):

```text
====================================================================================================
E03-B Correlated failure and recovery pressure: same clients, same freeze and restart, 4 client strategies
====================================================================================================
Outcome                           Ops  Success  affected      p95      p99  Recovery
----------------------------------------------------------------------------------------------------
A  immediate retry (UNSAFE)      2820    98.7%     92.9%   628 ms  1600 ms       1 s
B  backoff 200/400/800 ms        2774   100.0%    100.0%   844 ms  2132 ms       0 s
C  backoff + full jitter         2788   100.0%    100.0%   870 ms  2043 ms       0 s
D  C + retry budget              2848    95.6%     78.0%   131 ms  1192 ms       1 s

Load at the dependency         Requests  Ampl.  Retries  Denied  10-15 s  Peak/s Peak/100ms  Active*
----------------------------------------------------------------------------------------------------
A  immediate retry (UNSAFE)        3040  1.08x      220       0      619     275        232      110
B  backoff 200/400/800 ms          2956  1.07x      182       0      537     229        143      110
C  backoff + full jitter           2959  1.06x      171       0      549     225         76      111
D  C + retry budget                2858  1.00x       10     126      440     120         19       19
----------------------------------------------------------------------------------------------------
200 clients, closed loop, think 1-3 s (about 100 ops/s), 30 s, seed 42. Dependency 250 req/s (10 x 40 ms), queue 100.
Freeze 8.5-10.0 s, restart at 10.0 s fails every request inside, cold start until 15.0 s: 125 req/s (80 ms).
All: 4 attempts max, 2 s each, 5 s per op. D: retry budget 0.1 token per new op, at most 10, shared.
affected: success of ops started 8.5-15.0 s. p95/p99: end-to-end latency of all ops, succeeded and failed. Recovery: s after the restart second until per-second success >= 95% for 3 s.
Requests: received by the dependency, first attempts and retries; Ampl.: per logical op; 10-15 s: requests from the restart to the end of the cold start;
Peak/s, Peak/100ms: highest request count in one bucket; Active*: peak requests inside the restarted dependency.
```

**Is the failure really synchronised?** Requests per 100 ms at the dependency around the restart, from `results/sample/e03-b/timeseries.json` (`fine`); "rej." = rejected at once because 110 were already inside; "a2/a3/a4" = attempts 2, 3, 4 sent by the clients in that bucket:

| Strategy | Series | 9.9 | 10.0 | 10.1 | 10.2 | 10.3 | 10.4 | 10.5 | 10.6 | 10.7 | 10.8 | 10.9 | 11.0 | 11.1 | 11.2 | 11.3 | 11.4 | 11.5 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **A** | requests | 11 | 232 | 4 | 5 | 6 | 7 | 8 | 4 | 3 | 5 | 1 | 1 | 8 | 6 | 3 | 8 | 6 |
|  | rej. | 0 | 121 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 144 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 38 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 38 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | inside | 137 | 137 | 102 | 98 | 93 | 86 | 83 | 73 | 65 | 59 | 53 | 44 | 31 | 27 | 22 | 14 | 11 |
| **B** | requests | 11 | 11 | 3 | 143 | 6 | 11 | 8 | 36 | 4 | 6 | 1 | 0 | 4 | 5 | 2 | 8 | 3 |
|  | rej. | 0 | 0 | 0 | 35 | 0 | 0 | 0 | 9 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 0 | 0 | 138 | 0 | 3 | 0 | 0 | 0 | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 32 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 7 | 0 |
|  | inside | 138 | 138 | 8 | 110 | 104 | 103 | 98 | 110 | 102 | 98 | 92 | 83 | 73 | 60 | 54 | 52 | 43 |
| **C** | requests | 10 | 76 | 73 | 19 | 13 | 11 | 15 | 8 | 4 | 4 | 2 | 0 | 4 | 5 | 3 | 1 | 4 |
|  | rej. | 0 | 0 | 19 | 9 | 3 | 0 | 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 65 | 67 | 8 | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 0 | 1 | 6 | 6 | 3 | 6 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 2 | 0 | 0 | 1 | 0 | 0 | 0 | 1 | 0 | 0 |
|  | inside | 137 | 137 | 110 | 111 | 110 | 110 | 110 | 108 | 101 | 95 | 88 | 71 | 63 | 56 | 49 | 42 | 26 |
| **D** | requests | 11 | 14 | 11 | 4 | 8 | 7 | 8 | 4 | 4 | 4 | 2 | 3 | 12 | 12 | 10 | 9 | 7 |
|  | rej. | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 3 | 7 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | denied | 0 | 126 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | inside | 136 | 136 | 13 | 11 | 6 | 10 | 8 | 6 | 4 | 4 | 4 | 4 | 12 | 13 | 11 | 10 | 7 |

"inside" is the peak number of requests inside the dependency in that bucket (held requests of the old process included until the restart; 110 = the restarted process is full).

- **The cohort failed in one instant.** In this run 136-138 requests (depending on the strategy) failed in the 10.0 s bucket. Between 8.0 and 10.0 s, requests arrived at 5-14 per 100 ms.
- **A** sent all its retries in the same 100 ms: 232 requests in the 10.0 s bucket, 121 rejected at once; 38 operations used all four attempts within that 100 ms and failed (`exhausted_503`). The restarted process was full (110 inside) from the first instant.
- **B's retries arrived as waves** exactly at +200 ms (10.2 s: 138 second attempts, 35 rejected), +600 ms (10.6 s: 32 third attempts) and +1400 ms (11.4 s: 7 fourth attempts). The later waves found room, and every operation succeeded.
- **C spread the same retries** over 10.0-10.6 s: at most 76 requests per 100 ms instead of B's 143. It had fewer rejections than B (34 vs 44), and every operation succeeded.
- **D's budget had 10 tokens when 136 operations failed together.** It granted 10 retries and refused 126; those 126 operations failed at once. D sent the fewest requests and had the lowest peaks (19 per 100 ms, at most 19 inside the restarted process) and the lowest latency, and the lowest success (78.0% of the affected operations).

### Repeat over five seeds

`./repeat-e03.sh` ran both scenarios with seeds 42, 1, 2, 3 and 4 (5 runs per scenario, one `docker compose up` each, same machine, 2026-10-22). The seed changes the think times and the jitter draws; the timeline does not change. Median [min-max] over the five runs, from `results/sample/repeat.txt`:

**Outcome**

| | Ops | Success | Affected | p95 | p99 | Recovery |
| --- | --- | --- | --- | --- | --- | --- |
| A | 2797 [2778-2817] | 98.6 [98.4-98.7]% | 92.7 [91.8-93.3]% | 626 [596-664] ms | 1725 [1646-1777] ms | 1 [1-1] s |
| B | 2759 [2741-2771] | 100.0 [100.0-100.0]% | 100.0 [100.0-100.0]% | 840 [807-915] ms | 2287 [2192-2319] ms | 0 [0-0] s |
| C | 2767 [2748-2786] | 100.0 [100.0-100.0]% | 100.0 [99.8-100.0]% | 892 [843-914] ms | 2030 [1936-2150] ms | 0 [0-0] s |
| D | 2829 [2814-2845] | 95.3 [95.2-95.5]% | 77.8 [76.7-78.1]% | 170 [138-202] ms | 1253 [1204-1280] ms | 1 [1-1] s |

**Load at the dependency**

| | Requests | Ampl. | Retries | Denied | Requests 10-15 s | Peak/s | Peak/100 ms | Active after restart | Held at restart |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 3027 [3001-3055] | 1.08 [1.08-1.09] | 224 [222-242] | 0 | 629 [609-632] | 278 [271-289] | 234 [230-247] | 110 [110-110] | 141 [138-148] |
| B | 2950 [2934-2978] | 1.07 [1.07-1.08] | 193 [188-212] | 0 | 549 [543-566] | 230 [228-238] | 146 [144-155] | 110 [110-110] | 141 [139-147] |
| C | 2946 [2922-2991] | 1.06 [1.06-1.07] | 177 [174-205] | 0 | 549 [530-567] | 229 [223-248] | 78 [76-86] | 110 [110-110] | 141 [137-148] |
| D | 2839 [2824-2855] | 1.00 [1.00-1.00] | 10 [10-10] | 131 [128-138] | 441 [436-453] | 120 [117-126] | 20 [18-24] | 23 [17-28] | 141 [138-148] |

Here the seed-to-seed variation is small compared with most differences, and every within-seed comparison below held in all five runs:

- **The cohort:** 132-148 requests failed together at the restart (all E03-B runs, including the sample). Within one repeat run the four strategies' cohorts differed by at most 2 (in the sample run: 136-138).
- **Instantaneous load, 100 ms:** A 230-247 > B 144-155 > C 76-86 > D 18-24. C's 100 ms peak was 0.52-0.55 of B's in every run.
- **Instantaneous load, 1 s:** B 228-238 and C 223-248, with no consistent order between them (C was higher in two seeds). The one-second metric does not show the difference that jitter makes; the 100 ms metric does.
- **Total load:** B and C differed by at most 13 requests per run, in no consistent direction. C sent 7-19 fewer retries than B in every run and had fewer immediate rejections (33-58 against 47-65). A sent about 3% more requests than B and C; D about 3-4% fewer.
- **Success:** B 100%; C 100%, except one operation exhausted in two runs (99.8% affected). A lost 36-44 operations, every one of them by using all four attempts within 200 ms of the restart (all but one in the restart's 100 ms bucket). D lost 127-138: the budget always had exactly 10 tokens at the restart, granted 10 retries and refused the rest.
- **Latency:** p99 was lower for C than for B in every run (by 91-351 ms); p95 was lower for B than for C in four runs and equal within 1 ms in the fifth. D had by far the lowest latency, because its failed operations ended at once.
- **Recovery:** 0 s for B and C, 1 s for A and D in every run (both had failures in the restart second).
- In every E03-B run: no attempt timed out, no request was abandoned, the deadline cut no operation. The restart fired 0-5.8 ms after 10.0 s; the client clock offset was 1.9-14.4 ms; the clients' maximum wake-up lag was 6-34 ms, once 129 ms (B, seed-42 repeat run; its numbers are in line with the other runs).

### Sensitivity runs

Each variant was run once (seed 42, `E03_SCENARIOS=b`, everything else at the defaults). They are single runs: they show a direction, not a range. Console outputs: `results/sample/e03-b-sensitivity/`.

| Variant | Command | Affected success A / B / C / D | Requests A / B / C / D | Peak/s A / B / C / D | Peak/100 ms A / B / C / D | Recovery A / B / C / D |
| --- | --- | --- | --- | --- | --- | --- |
| Main configuration (median of 5) | `docker compose up` | 92.7 / 100.0 / 100.0 / 77.8% | 3027 / 2950 / 2946 / 2839 | 278 / 230 / 229 / 120 | 234 / 146 / 78 / 20 | 1 / 0 / 0 / 1 s |
| 2 s outage after the restart | `E03B_OUTAGE_MS=2000` | 63.6 / 71.9 / 71.9 / 63.6% | 3697 / 3308 / 3426 / 2873 | 631 / 403 / 559 / 120 | 463 / 157 / 105 / 18 | 2 / 2 / 2 / 2 s |
| Small queue (admits 30) | `E03_QUEUE_LIMIT=20` | 79.9 / 86.9 / 94.8 / 78.1% | 3218 / 3113 / 3168 / 2858 | 429 / 305 / 406 / 120 | 384 / 140 / 92 / 18 | 1 / 2 / 1 / 1 s |
| More load than the cold start carries | `E03B_CLIENTS=300` (about 150 ops/s) | 85.7 / 94.5 / 95.0 / 76.2% | 4571 / 4473 / 4545 / 4191 | 518 / 398 / 523 / 176 | 450 / 217 / 129 / 24 | 1 / 2 / 1 / 1 s |

- **2 s outage.** With the restarted process refusing everything until 12.0 s, every strategy's retries of the cohort fell into the outage (B's last wave at +1.4 s included), so success was decided by the outage, not by the waiting: 178-283 operations of A, B and C exhausted their attempts on refusals. Backoff did reduce load here: A sent 3697 requests (1.30x) and 463 in its first 100 ms, B 3308 (1.22x). C sent more than B (3426) and had a higher per-second peak (559 against 403): its shorter average wait lets each client finish a failed operation sooner and start the next one sooner (closed loop), so it tried more often during the outage. D failed as many affected operations as A (63.6%) with 24 retries against A's 849.
- **Small queue.** The cohort (137) was about 4.5 times the admission limit (30). B's synchronised waves (140, 115, 93 requests per 100 ms at +200, +600, +1400 ms) each overflowed it, and 65 operations exhausted all four attempts. C spread the same retries and lost 30. A lost 115. Here jitter changed the outcome, not only the shape, and B also had the longest recovery (2 s) and p99 (2.5 s).
- **300 clients.** The cohort was 207-210, and the offered load (about 150 ops/s) exceeded the cold-start capacity (125 req/s) for 5 s, so the dependency was overloaded regardless of retries. B and C ended with about the same success (38 and 34 operations lost); C again sent more requests and retries than B and had the higher per-second peak, B the higher 100 ms peak.

### What E03-B shows

1. **A correlated failure turns fixed backoff into sub-second waves,** at exactly +200, +600 and +1400 ms after the failure. Per-second metrics hide them (B and C had the same one-second peak); 100 ms buckets show them (B's peak was about twice C's in every run).
2. **Whether the waves hurt depends on the capacity limits.** In the main configuration the first wave exceeded the admission limit by about 30 requests and the later waves found room, so B lost nothing to synchronisation: jitter changed the shape of the load (peak per 100 ms, rejections, p99), not the success or the total. With a queue small enough that each wave overflowed it several times over, B lost more than twice as many operations as C (one run).
3. **Full jitter de-synchronises retries; it does not delay them.** Its average wait is half of B's. Where clients were limited by an outage or by an overloaded cold start, C sent more requests and had a higher per-second peak than B.
4. **Immediate retry concentrates the whole retry budget into one instant:** 230-247 requests in the first 100 ms (about 20 times the normal rate) against a restarted process that admits 110, and every operation that does not get in within that instant fails.
5. **A ratio-based retry budget bounds retry traffic by design and pays for it in failed operations.** Sized for normal traffic (10% plus 10 tokens), it covered 10 of about 140 simultaneous failures. D had the lowest load, peaks and latency in every run, and the lowest success. Whether that is the right trade depends on whether failed operations can be retried later, by the user or a higher layer, without harm; E03-B does not model that.
6. **Recovery time (1 s resolution) barely separates the strategies here;** success, load and the 100 ms series do.

### What E03-B does not show

- **A larger burst than the dependency can queue, from many independent processes.** Here one load generator plays 200 clients, and the burst (132-148) is a little above the admission limit (110). A fleet of thousands of instances, each with its own small budget and its own timers, and bursts many times the admission limit, can behave differently. The smaller-queue sensitivity run points in that direction; it is one run.
- **Connection-level effects.** The restart answers 503 instead of dropping connections, so there is no reconnect storm, no TLS handshakes, no SYN backlog.
- **Real warm-up behaviour.** The cold start is a fixed 5 s at half speed; real warm-up depends on load, caches and JIT.
- **Circuit breakers, adaptive throttling, `Retry-After`, server-side admission control by priority.** None is implemented.
- **Statistical significance.** Five seeds on one machine show the spread; they are not a confidence interval.

## Outputs and how to read them

All files are written to `results/` (bind-mounted, so they appear on the host): `results/e03-a/` and `results/e03-b/`, each with `summary.json`, `timeseries.json` and `console.txt` (the console output of that scenario, written by the load generator itself), and `results/repeat/` from `./repeat-e03.sh`. `results/` is ignored by git, except `results/sample/`: `e03-a/` (the E03-A run shown above), `e03-b/` (the seed-42 E03-B run shown above), `repeat.txt` (the five-seed repeat) and `e03-b-sensitivity/` (console output of the three single-run variants).

### E03-A files (schema v1)


#### Console summary (Figure 2)

| Column | Meaning |
| --- | --- |
| Requests | Requests **received by the dependency** during the measured run (admitted + rejected) |
| Ampl. | Requests / logical operations (3038). 1.00x would mean no retries. |
| Success | Logical operations that ended with `200` |
| in window | Success rate of the operations whose **scheduled start** was inside the failure window (10-20 s) |
| p95, p99 | End-to-end latency of **all** logical operations (succeeded and failed), from the scheduled arrival to the final result (nearest-rank percentile) |
| Peak rps | Highest one-second request count at the dependency |
| Recovery | Seconds after the window ends until the per-second success ratio stays ≥ 95% for 3 consecutive seconds (definition below) |

**Recovery time, precisely.** Completed logical operations are bucketed by the second in which they **completed**. Recovery is the first second *s* ≥ 20 such that seconds *s*, *s+1* and *s+2* each have at least one completion and `succeeded / (succeeded + failed) ≥ 0.95`. Recovery time = *s* − 20 in whole seconds, so `0 s` means the first second after the window already qualified. Only seconds before the load stops (≤ 29) count. If no streak fits, the table shows `>7 s`.

#### `summary.json`

The run metadata (`startedAtUtc`, runtime, images, CPU count, every parameter, the recovery definition), then one object per strategy:

| Field | Content |
| --- | --- |
| `operations` | `started`, `succeeded`, `failed`, `successPercent`, `failedBy` (`exhausted_503`, `exhausted_timeout`, `exhausted_http_error`, `deadline_exceeded`), `attemptsHistogram` (operations by number of attempts used) |
| `attempts` | `sentByClient`, `receivedByDependency`, `amplificationClient`, `amplificationDependency`, `responsesByStatus`, `timeouts`, `cutByDeadline`, `httpErrors` |
| `dependency` | Dependency-side totals: `received`, `ok`, `injected503`, `rejected503`, `abandoned`, `peakInFlight`, `peakQueued`, `peakReceivedPerSecond` |
| `latencyMs` | `p50`, `p95`, `p99`, `max` for `all`, `succeeded` and `failed` operations |
| `byArrivalPhase` | Operations grouped by scheduled start: `beforeWindow`, `inWindow`, `afterWindow`, each with success % and attempts per operation |
| `successPercentByArrivalSecond` | Success % of the operations scheduled in each second 0-29 |
| `recovery` | `recovered`, `seconds`, `firstHealthySecond` |
| `maxDispatchLagMs` | How late the load generator started an operation compared with its schedule (included in latency) |

#### `timeseries.json` (Figure 1)

`failureWindow: {startS: 10, endS: 20}`, `arrivalsEndS: 30`, a `fields` dictionary that documents every field, and per strategy one point per second (this one is strategy A, second 15 of the sample run):

```json
{ "t": 15, "dependencyRequests": 311, "succeeded": 61, "failed": 51, "inFlight": 110,
  "started": 109, "attemptsSent": 311, "rejected503": 195, "injected503": 59, "queued": 100 }
```

- `t`: second since the run started (bucket [t, t+1)).
- `dependencyRequests`: requests received by the dependency in that second (measured at the dependency). This is the series Figure 1 plots, one line per strategy.
- `succeeded`, `failed`: logical operations that **completed** in that second (client side).
- `inFlight`: peak number of requests inside the dependency (in service + queued) during that second. 110 = 10 workers + a full queue.
- Extras: `started` (offered load, identical for every strategy, a useful reference line in the chart), `attemptsSent` (client cross-check), `rejected503`, `injected503`, `queued`.

Seconds after 30 contain only the drain (operations that started before 30 s and were still retrying). As CSV for plotting:

```bash
jq -r '.strategies[] | .id as $s | .points[] | [$s, .t, .dependencyRequests, .started] | @csv' results/e03-a/timeseries.json
```

#### Logs

The dependency writes one JSON object per line (`docker compose logs --no-log-prefix dependency`). It is not attached to the console, which keeps the summary readable. Fields: `ts`, `level`, `phase` (`startup`, `run_reset`, `failure_window_started`, `failure_window_ended`, `freeze_started`, `restarted`, `outage_ended`, `cold_ended`, `shutdown`), `run_id` (`A`, `B`, `C`, `warmup-A`, `b-A` ... `b-D`, `warmup-b-A`, ...), plus the configuration at `startup` and `in_system` at the window edges:

```json
{"ts":"2026-10-22T09:54:15.582Z","level":"info","category":"Dependency","run_id":"A","phase":"failure_window_ended","t_s":20,"in_system":110,"detail":"Recovered: service time 40 ms, no injected errors","msg":"phase=failure_window_ended t_s=20 in_system=110 Recovered: service time 40 ms, no injected errors"}
```

Each attempt also carries `X-Operation-Id` and `X-Attempt` request headers. With `E03_ATTEMPT_LOG=true` the load generator writes them to `results/e03-x/attempts.jsonl` together with each attempt's outcome.

### E03-B files (schema v2)

`results/e03-b/` holds the same three files. E03-A keeps schema v1, unchanged, so its committed sample is still what the current code writes; v2 is used where the fields differ.

**`summary.json`** (`drlab-e03/summary/v2`): `scenario`, run metadata as in v1, `parameters` (`load` with the closed-loop settings, `dependency`, `failure` with the timeline in ms, `client` with the deadline for every strategy and the `retryBudget` rule, `recovery`, `affectedOperations`, `requestsDuringRecovery`), then one object per strategy:

| Field | Content |
| --- | --- |
| `retryBudget` | D only: `ratio`, `maxTokens`, `firstAttempts`, `granted`, `denied`, `tokensLeft` |
| `operations` | as v1, plus the outcome `retry_budget_exhausted` in `failedBy` |
| `byStartPhase` | `beforeFreeze`, `affected` (8.5-15 s), `afterColdStart`, by the operation's start |
| `load` | `requestsAtDependency`, `amplification`, `attemptsSent`, `firstAttempts`, `retriesSent`, `retriesDenied`, `requestsDuringRecovery`, `peakRequestsPerSecond`, `peakRequestsPer100Ms`, `peakRequestsPer100MsAtMs`, `responsesByStatus`, `timeouts`, `cutByDeadline`, `httpErrors` |
| `concurrency` | `peakActive` (includes the requests held by the freeze), `heldAtRestart`, `peakActiveAfterRestart` |
| `dependency` | `received`, `ok`, `rejected503`, `restart503`, `refused503`, `abandoned`, `restartedAtMs` (when the restart timer actually fired, 0-5 ms late) |
| `latencyMs`, `recovery` | as v1; recovery counts from the restart second |
| `maxWakeLagMs`, `clientClockOffsetMs` | how late a client woke up compared with its plan; the offset that aligned the client clock with the dependency |

**`timeseries.json`** (`drlab-e03/timeseries/v2`): `failure` (`freezeStartMs`, `restartMs`, `outageEndMs`, `coldEndMs` and a `phases` list), `fields` and `fineFields` documenting every field, and per strategy:

- `points`, one per second, as in v1 (`t`, `dependencyRequests`, `succeeded`, `failed`, `inFlight`, `started`, `attemptsSent`, `rejected503`, `queued`) plus `retriesSent`, `retriesDenied`, `restart503`, `refused503`;
- `fine`, 100 ms buckets as arrays (element *i* covers [*i* x 100, (*i* + 1) x 100) ms): `dependencyRequests`, `active`, `queued`, `ok`, `rejected503`, `restart503`, `refused503`, `attempt1` ... `attempt4`, `retriesDenied`, `succeeded`, `failed`.

```bash
# requests per 100 ms around the restart, one line per strategy
jq -r '.strategies[] | [.id, (.fine.dependencyRequests[95:120] | map(tostring) | join(" "))] | join(": ")' results/e03-b/timeseries.json
```

The dependency logs `freeze_started`, `restarted` (with `held`, the number of requests failed at the restart, and `actual_ms`), `outage_ended` (only with an outage) and `cold_ended`.

## Repeating with several seeds

```bash
./repeat-e03.sh                          # E03-A and E03-B, seeds 42 1 2 3 4: about 21 minutes
E03_REPEAT_SEEDS="7 8 9" ./repeat-e03.sh
E03_SCENARIOS=b ./repeat-e03.sh          # E03-B only
```

The script empties `results/repeat/`, runs `docker compose up` once per seed (output in `results/repeat/seed-N/` and `results/repeat/seed-N.log`), then runs `LoadGenerator aggregate`, which reads every `summary.json`, prints per scenario, strategy and metric the median and min-max, followed by every individual value in seed order, and writes `results/repeat/repeat.txt` and `repeat.json`. All other `E03_*` / `E03B_*` variables are passed through. On macOS it keeps the machine awake with `caffeinate` while it runs.

Medians and ranges over five runs are a description of the spread, not a statistical test. Within one seed the strategies see the same think-time and jitter sequences, so comparing strategies within a seed (the per-seed values) is more informative than comparing ranges.

## Automated tests

`tests/RetryResilience.Tests` (xUnit v3, 105 tests, no network or containers; 55 from E03-A, 50 added with E03-B):

| Area | What is checked |
| --- | --- |
| Delay schedules | A never waits. B waits exactly 200/400/800 ms (and follows the article's cap beyond). C stays in [0, cap] for the article's caps 200, 400, 800, 1600, 2000, 2000, reaches both ends, has a mean of about cap/2, and is reproducible per operation. |
| Transient classification | 408/429/502/503/504 are retried. 200/400/404/409/500/501 are not. |
| Retry loop (scripted handler) | Retries 503 until success. Stops after 4 attempts. Does not retry 500/404. Retries `HttpRequestException` and rethrows it on attempt 4. An attempt that hangs is cancelled after 2 s and retried. B's gaps between attempts are at least 200/400/800 ms. The operation deadline cancels a pending wait and an attempt in flight. |
| Retry budget | Starts full and grants exactly `maxTokens` retries, then refuses. Ten new operations at 0.1 pay for one retry (despite binary rounding). Tokens never exceed the cap. Over a random 100,000-step sequence, granted retries never exceed 10 + 0.1 x operations. Eight concurrent callers never overspend. Invalid parameters are rejected. |
| Strategy D | Same waits as C for the same operation while tokens last. Throws instead of waiting when the bucket is empty. With one token the article's loop sends two attempts and stops. A refused retry ends the operation as `retry_budget_exhausted` and is counted. In E03-B all strategies share the 5 s deadline and only D has a budget; E03-A's A, B, C are unchanged. |
| E03-B failure model | Timeline boundaries (freeze 8.5 s, restart 10 s, cold start until 15 s, with and without an outage). Cold service time only during the cold start. The default freeze is shorter than the 2 s attempt timeout. Invalid timelines are rejected. Driven directly against the dependency's request path: every request inside at the restart (in service, queued, and beyond the admission limit) fails with 503 within a few ms of the restart; before the freeze it serves and sheds load normally; after the restart a fresh process admits workers + queue and serves at cold speed; during an outage every request is refused; the E03-A window mode ignores the freeze settings. |
| Fine-grained buckets | Dependency and client map milliseconds to the same 100 ms buckets (boundaries, clamping). Ten 100 ms buckets add up to their second. The client counts attempts per 100 ms by attempt number on the aligned clock. Peak and range-sum helpers. |
| Repeat aggregation | Median (odd, even, single, empty). Min/max/median ignore missing values and count them. Dotted JSON paths. Reading `seed-N/e03-b/summary.json` from a directory: numeric seed order, per-strategy values, spreads, formatted cells, missing scenario. |
| Think times | Same seed and client give the same sequence, within bounds; first starts are spread over [0, max). |
| Metrics | Nearest-rank percentiles (1..100, small lists, empty, invalid p). Recovery time: immediate, delayed streak, exactly 95% vs 94.9%, a second without completions, a streak that does not fit before the load stops. |
| Workload and dependency | Same seed gives the same arrival schedule. Arrivals are ascending and near the rate. Admission at workers + queue. FIFO workers. Capacity arithmetic. |

```bash
./test-e03.sh                                  # in Docker, no local .NET SDK needed
docker compose run --rm --build tests          # the same without the wrapper
```

Output on 2026-10-22 (trimmed):

```text
  Passed RetryResilience.Tests.CorrelatedWorkTests.The_E03_A_window_mode_is_not_affected_by_the_freeze_settings [289 ms]
  Passed RetryResilience.Tests.RetryLoopTests.An_attempt_that_exceeds_2_s_is_cancelled_and_retried [2 s]
  ...
Test Run Successful.
Total tests: 105
     Passed: 105
```

As a sanity check, the suite was run against deliberately broken variants. For E03-A (`RetryLoop.cs`): jitter that never reaches the cap (`Next(0, capMs)`) failed 6 tests, 500 classified as transient failed 2, 5 attempts instead of 4 failed 5. For the retry budget: allowing a retry with less than one token failed 4 tests, removing the cap failed 1, D ignoring the budget failed 3, and removing the lock in `TryRetry` failed 1 (the concurrency test; a race, so it may not fail every time). Each variant was restored and the suite passed again.

**What the tests guarantee, and what they do not.** They run in-process, without containers, network or real time pressure (except one 2 s timeout test), on the .NET version pinned below. They check that the lab's building blocks behave as specified: the delay schedules, the transient classification, the retry loop's limits, the retry budget, the failure timeline, the bucketing and the aggregation. They do **not** check the experiment's results. No test asserts that one strategy beats another, that a number in this README is reproduced, or that the dependency model resembles any real service. Those results come only from running the experiment, and they depend on the machine, the parameters and the seed (see Limitations). Passing tests also say nothing about production use: the code is a lab, not a library.

## Layout

```text
experiments/retry-resilience/
├── compose.yaml              # project "drlab-e03": `docker compose up --build` runs both scenarios; parameters; test profile
├── Dockerfile                # build / test / runtime stages; one image with both programs
├── test-e03.sh               # runs the xUnit suite in Docker, cleans up
├── repeat-e03.sh             # both scenarios over several seeds, then median and min-max
├── RetryResilience.slnx
├── src/Dependency/           # simulated dependency
│   ├── Program.cs            # GET /work, /health, /admin/{reset,stats,shutdown}; restart timer
│   ├── WorkProcessor.cs      # the request path: E03-A window, E03-B freeze / restart / cold start
│   ├── CorrelatedFailure.cs  # E03-B timeline
│   ├── CapacityModel.cs      # workers + bounded queue, admission
│   ├── ServerRun.cs          # per-second and per-100 ms counters since the last reset
│   └── DependencyOptions.cs
├── src/LoadGenerator/        # load generator and report
│   ├── RetryLoop.cs          # the article's retry loop + Backoff (A, B, C)
│   ├── Strategies.cs         # A, B, C, D definitions
│   ├── RetryBudget.cs        # strategy D's token bucket
│   ├── StrategyRunner.cs     # E03-A: warm-up, reset, open-loop dispatch, drain
│   ├── ClosedLoopRunner.cs   # E03-B: the client population
│   ├── RunRecorder.cs        # client-side counters (1 s and 100 ms), attempt-counting handler
│   ├── Metrics.cs            # percentile, recovery time, peaks
│   ├── Report.cs             # E03-A: summary.json, timeseries.json (v1), console table
│   ├── CorrelatedReport.cs   # E03-B: summary.json, timeseries.json (v2), console tables
│   ├── Repeat.cs             # `aggregate`: median and min-max over seeds
│   └── ...
├── tests/RetryResilience.Tests/
└── results/                  # output (git-ignored) + sample/ (committed runs)
```

## Versions and prerequisites

| Component | Version |
| --- | --- |
| .NET SDK / ASP.NET Core runtime images | `mcr.microsoft.com/dotnet/sdk:10.0.402`, `mcr.microsoft.com/dotnet/aspnet:10.0.13` (runtime reported: .NET 10.0.13) |
| NuGet (tests only) | xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.11.0 |

- Docker with Compose v2. `attach: false` needs Compose 2.20 or later. Tested with Docker 28.4 / Compose 2.39 on macOS (arm64).
- No local .NET SDK is needed.
- Free loopback port `127.0.0.1:18490` (dependency, for `curl` during a run). Override it with `DRLAB_E03_DEPENDENCY_PORT`. The load generator publishes no port.
- The load generator runs as root inside its container so that it can write into the bind-mounted `results/` on Linux hosts. The subdirectories it creates (`e03-a/`, `e03-b/`, `repeat/...`) are made world-writable, so you can still delete their files without root.

The compose project name `drlab-e03` keeps containers and the network separate from E01 and other local stacks.

## Limitations (read before citing results)

- **One machine, one synthetic dependency.** The dependency's capacity is a timer model: no CPU contention, no garbage collection pressure, no connection limits, no downstream dependencies. Latencies include Docker networking on one host. The ranking was stable here. Absolute numbers will differ elsewhere.
- **No claim that jitter is always better, or worse.** In E03-A failures are not synchronised, so there is little to de-synchronise. E03-B tests one correlated failure with one set of parameters (and four single-run variants); it shows what jitter changed there, not in general.
- **Backoff alone does not cap retry traffic.** In E03-A a retry budget is not applied; in E03-B only strategy D has one. A circuit breaker and client-side adaptive throttling are **not implemented**.
- **One client process.** E03-A is open loop; E03-B is a closed loop of 200 clients, but in one process with one shared budget. Real callers are spread over many processes, each with its own timers and budget.
- **`Retry-After` is not sent or honoured.** The article's code ignores it, so the dependency does not send it.
- **Failure-window edges.** A 10 s window is much longer than the waits (≤ 1.4 s), so it is a sustained incident. With failures shorter than the backoff, longer waits would help more and A would look worse.
- **Recovery has 1 s resolution** and depends on the definition above. With load shedding, the dependency recovers immediately once the cause is gone. In E03-B the 100 ms series show what the 1 s metrics hide.
- **The load generator and the dependency share the machine.** E03-A records the maximum dispatch lag per strategy (6-46 ms in every run, and once 120 ms: strategy A of the sample run); latency is measured from the scheduled arrival, so lag is included and not hidden. E03-B records the maximum wake-up lag of a client (`maxWakeLagMs`) and the restart timer's actual time (`restartedAtMs`, 0-5 ms after 10.0 s).
- The admin endpoints have no authentication. Strategy A is deliberately unsafe. Both are for local use only.

## Failure analysis (brief)

**E03-A.** The dependency could carry the original load even in its degraded state (80% utilisation). Half of its answers were errors, and every error became another request. Retries pushed the demand past the reduced capacity, the queue filled, and the dependency began rejecting requests, which caused yet more retries. Because every operation keeps the same attempt budget and the incident outlasts any backoff, the three strategies fed the dependency about the same number of requests. Backoff changed only *when* they arrived. That timing still mattered at the edges. Immediate retries hit the same full queue and failed fastest. Longer fixed waits let some operations outlive the incident. Shorter random waits gave lower tail latency. Bounding the *amount* of retry traffic needs a mechanism that looks at more than one request: a retry budget, a circuit breaker or load-aware throttling. A deep queue without load shedding makes the same storm worse: work continues for clients that have already left.

**E03-B.**
A freeze held about 140 requests, and the restart failed them in one instant. What reached the restarted process next was decided by the clients' waiting rule. Immediate retry delivered the whole cohort and its retries within milliseconds into a process that admits 110; the overflow burned its attempts in the same instant. Fixed backoff moved the cohort as a block to +200 ms, +600 ms and +1400 ms. Each block overflowed the admission limit once more, but with a queue of 100 the overflow was small and the next block found room. Full jitter spread the same requests over about half a second. The total number of requests hardly depended on the waiting rule, because every operation had the same four attempts. What changed was how they were spread over time, and that mattered when a short peak exceeded a hard limit (the admission limit, an outage). Only the retry budget changed the *amount*, and it did so by refusing retries for most of a cohort that it was not sized for.
