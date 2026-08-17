# Exponential Backoff for Retry Delays

**Scope:** v2 scalability review — one of six sibling resilience-pattern documents produced against `documentation/02-design/v2/agents/prompt@review-desig.md`: `14-timeout.md`, `15-retry.md`, `16-exponential-backoff.md` (this document), `17-jitter.md`, `18-circuit-breaker.md`, `19-bulkhead.md`. Each covers one mechanism only; this document is scoped strictly to **the delay curve between retry attempts** and does not re-litigate what counts as a retryable failure, how many attempts are allowed, when to time out a single attempt, whether to add randomization, or when to stop calling a dependency altogether — those are the other five documents' jobs, cross-referenced by filename below.

**Builds on (does not replace):** `v1/design/nfr-resilience.md` Section 4, which already introduced `BackoffType = DelayBackoffType.Exponential` on the moderation-check HTTP adapter's Polly pipeline (`MaxRetryAttempts = 2`, 2s per-attempt timeout, 5s overall pipeline timeout). v1 made exponential backoff's *existence* a settled decision for one narrow, low-volume call. This document generalizes the same delay curve to v2 scale — many more horizontally-scaled instances, several new dependencies (Redis, a server RDBMS, Kafka/outbox relay, Elasticsearch), and materially higher concurrent request volume — and gives it concrete, justified numbers.

**Traces to:** ANFR-04 (graceful degradation on backend failure), ANFR-01 (redirect availability), ANFR-05/ANFR-06 (latency and throughput at scale — the constraints that bound how large a delay curve is tolerable).

**Related v2 documents (cross-referenced by filename, not duplicated here):** `14-timeout.md` (per-attempt and overall timeout budgets that bound how long any one retry sequence is allowed to run), `15-retry.md` (which failures are retryable, and the max-attempt budget consumed in Section 3 below), `17-jitter.md` (randomization layered on top of the curve defined here), `18-circuit-breaker.md` (stops attempts altogether once a dependency is judged unhealthy, rather than retrying into it forever), `19-bulkhead.md` (isolates one dependency's retry traffic from starving resources shared with unrelated calls).

---

## 1. Why Naive Immediate Retries Make a Struggling Dependency Worse

A retry policy with no delay — "if it fails, call again right now" — is safe only when failures are rare and independent. At this system's scale, they are neither: a transient dependency blip is experienced by **every in-flight request on every instance at the same moment**, and a delay-free retry policy turns that single blip into a self-sustaining overload.

**Concrete numbers for this system, at the 5-year projection:**

- ~1,150 create events/sec sustained, and up to several thousand analytics events/sec at peak, spread across many horizontally-scaled API instances (`01-create-path-extreme-scalability.md` establishes this fleet shape; `07-redis-caching-and-invalidation.md` establishes the same shape for the cache tier).
- Assume, illustratively, 50 API instances sharing that load — roughly 23 create requests/sec per instance in steady state, more at peak, each instance independently calling out to the same shared dependency (Redis, the database, the moderation-check adapter, the outbox relay's Kafka broker).

Now suppose that shared dependency — say, the managed Redis tier or the server RDBMS — has a 2-second blip: a failover, a brief connection-pool exhaustion, a GC pause on the dependency's side. Every request in flight against it across all 50 instances fails at roughly the same instant. With **no backoff**, each of those failed calls retries immediately:

- The retry traffic lands back on the dependency in the same instant as a fresh wave of newly-arriving requests — the dependency now has to absorb roughly **double** its normal load (the retries plus ongoing new traffic) at the exact moment it is least able to, because it is still recovering from whatever caused the blip in the first place.
- If the dependency's recovery time is itself sensitive to load (true of a database rebuilding connections, or a cache warming back up), the retry wave can extend the outage rather than let it clear — a **metastable failure state**: the system never returns to healthy because the retries themselves are the thing keeping it unhealthy.
- This compounds with instance count, not request rate alone: doubling the fleet from 50 to 100 instances to handle growth doubles the number of independent retry sources hitting the dependency simultaneously, even if per-instance request rate stays flat.

This is the textbook "retry storm," and it is precisely why v1's Section 2.3 (`nfr-resilience.md`) refused to wrap the primary database write in a retry loop at all for v1's single-instance shape — and why, now that v2 introduces exactly the fan-out (many instances) that makes storms possible, a delay-free retry is not a viable option for **any** shared dependency in this design (Redis, the RDBMS, Kafka, Elasticsearch, the moderation-check adapter). The fix is not "don't retry" — `15-retry.md` still calls retrying the right response to a transient failure — it is "space retries out, and space them out *more* the more times a given call has already failed," which is exactly what exponential backoff does.

---

## 2. The Exponential Backoff Formula

```
delay(attempt) = min(base × factor^(attempt − 1), cap)
```

| Parameter | Value for this system | Why |
|---|---|---|
| **base** | 100 ms | Comparable to a healthy round-trip to any of this system's dependencies (Redis, the RDBMS, an internal HTTP call) once the blip clears — the first retry should arrive close to "as soon as plausible," not needlessly slow, while still being strictly non-zero (Section 1). |
| **factor** | 2× (doubling) | Standard, well-understood growth rate; each successive failure roughly halves the retry rate a given caller contributes to the dependency, which is the whole point — a caller that has failed 5 times in a row is statistically more likely facing a real outage than a one-off blip, and should back off accordingly rather than keep probing at a near-constant rate. |
| **cap** | 30 s | Bounds the curve so a caller that has been failing for a while doesn't end up waiting minutes between attempts — see below for why 30s specifically. |

**Resulting sequence** (`100ms × 2^(n-1)`, capped at 30s):

| Attempt | Uncapped delay | Applied delay |
|---|---|---|
| 1 | 100 ms | 100 ms |
| 2 | 200 ms | 200 ms |
| 3 | 400 ms | 400 ms |
| 4 | 800 ms | 800 ms |
| 5 | 1.6 s | 1.6 s |
| 6 | 3.2 s | 3.2 s |
| 7 | 6.4 s | 6.4 s |
| 8 | 12.8 s | 12.8 s |
| 9 | 25.6 s | 25.6 s |
| 10 | 51.2 s | **30 s (capped)** |
| 11+ | growing further | **30 s (capped, flat)** |

**Why these numbers against acceptable end-to-end latency:** this system has two very different latency budgets, and the same formula has to serve both without being tuned twice:

- **Synchronous, user-facing calls** (e.g., the moderation-check adapter on the create path, or a cache-then-DB fallback on the redirect path) have a tight overall timeout budget — v1 set 5s for the moderation-check pipeline (`nfr-resilience.md` Section 4.2), and `14-timeout.md` carries the equivalent budget forward for v2's other synchronous dependency calls. On a budget that tight, the curve never gets anywhere near the 30s cap: `15-retry.md`'s max-attempt budget for synchronous calls (illustratively, 3 attempts) exhausts the retry policy at the 100/200/400 ms rung — well inside a 5s window — long before doubling could produce a delay large enough to matter. The cap exists for the *other* case, not this one.
- **Asynchronous, decoupled calls** (the outbox relay publishing to Kafka, a cache-invalidation subscriber per `07-redis-caching-and-invalidation.md` Section 5.2, an Elasticsearch bulk-indexing job) have no caller blocked on the result, so a much longer retry window is acceptable — this is where the curve is allowed to run out to attempt 9–10 and then flatten at the 30s cap. 30s is chosen, not left unbounded, so that once a dependency does recover, a backed-off caller notices and resumes within half a minute rather than the multi-minute waits an uncapped doubling curve would eventually produce (attempt 15 uncapped would be `100ms × 2^14` ≈ 27 minutes) — a resumption delay that long would itself look like an outage to anything depending on that async path (e.g., search-index freshness, invalidation latency).

The cap is a **safety rail on the formula**, not a tuning knob per dependency — the same `100ms / 2× / 30s` triple is intended to apply uniformly across this system's dependencies, with the *attempt budget* (not the curve) being what differs between a tight synchronous call and a patient background one. That split of responsibility is exactly Section 3.

---

## 3. Composing With the Retry Document's Max-Attempt Budget

Exponential backoff answers "how long to wait between attempts"; `15-retry.md` answers "how many attempts to make before giving up." Neither is sufficient alone — a curve with no attempt limit retries forever (eventually forever at the 30s floor, which is still an unbounded liability for a synchronous caller); an attempt limit with no curve is exactly the retry-storm hazard from Section 1. The two compose by `15-retry.md`'s `MaxRetryAttempts` simply truncating the sequence defined in Section 2 above.

**Worked example 1 — synchronous path (moderation-check adapter, extended for v2 from `nfr-resilience.md` Section 4.2):**

Assume `15-retry.md` sets `MaxRetryAttempts = 3` for this call, per its own transient-failure classification.

| Event | Time (relative) |
|---|---|
| Initial call | t = 0 |
| Fails (transient) → wait attempt 1 delay | 100 ms |
| Retry 1 | t = 100 ms |
| Fails → wait attempt 2 delay | 200 ms |
| Retry 2 | t = 300 ms |
| Fails → wait attempt 3 delay | 400 ms |
| Retry 3 | t = 700 ms |
| Budget exhausted → surface failure to caller | t = 700 ms (+ this attempt's own timeout, per `14-timeout.md`) |

Total added wait from backoff alone: 700 ms, comfortably inside the 5s overall pipeline budget `14-timeout.md` carries forward from v1 — the curve never approaches its 30s cap because `15-retry.md`'s attempt budget is the binding constraint here, exactly as anticipated in Section 2.

**Worked example 2 — asynchronous path (outbox relay retrying a Kafka publish, or the cache-invalidation subscriber from `07-redis-caching-and-invalidation.md` Section 5.2):**

Here `15-retry.md` can afford a far larger budget (illustratively, attempts continue for a bounded duration or count well past what a synchronous caller could tolerate, since nothing is blocked waiting). The same curve from Section 2 plays out further:

| Attempt | Delay before this attempt | Cumulative elapsed |
|---|---|---|
| 1–4 | 100/200/400/800 ms | 1.5 s |
| 5–8 | 1.6/3.2/6.4/12.8 s | ~25.5 s |
| 9 | 25.6 s | ~51 s |
| 10 | 30 s (capped) | ~81 s |
| 11+ | 30 s (capped, flat) | +30 s per attempt |

`15-retry.md` owns the decision of where this sequence is finally cut off (or handed to `18-circuit-breaker.md` to stop attempting altogether) — this document's contribution ends at defining what each successive wait *is*, not how many of them are allowed.

---

## 4. Why Backoff Alone Is Still Insufficient at This Scale

Backoff makes each individual caller retry less often over time, but every instance computing the same deterministic curve off the same fixed `base`/`factor` means that instances which failed at the same moment (Section 1's simultaneous-blip scenario) stay **synchronized** through every retry round — all of them landing their attempt-2 retry at the same ~200ms mark, attempt-3 at the same ~400ms mark, and so on — so a recovering dependency still gets hit by synchronized waves of retry traffic instead of smoothly-spread load; breaking that synchronization by randomizing the delay is the job handed off to `17-jitter.md`.

---

## 5. Implementation in .NET: Polly's Exponential Backoff Delay Generator

Polly (via `Microsoft.Extensions.Resilience`) supports exponential backoff natively through `RetryStrategyOptions.BackoffType = DelayBackoffType.Exponential`, combined with `Delay` (the base) and `MaxDelay` (the cap) — no custom delay-calculation code is needed. This mirrors the shape v1 already used for the moderation-check adapter (`nfr-resilience.md` Section 4.2), extended here with the explicit cap and applied as the standard pipeline for any v2 dependency call:

```csharp
services.AddResiliencePipeline("redis-cache-read", builder =>
{
    builder.AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder()
            .Handle<RedisConnectionException>()
            .Handle<TimeoutException>(),

        BackoffType = DelayBackoffType.Exponential,   // Section 2: doubling curve
        Delay = TimeSpan.FromMilliseconds(100),        // base
        MaxDelay = TimeSpan.FromSeconds(30),            // cap
        MaxRetryAttempts = 3,                           // owned by 15-retry.md, not this document

        UseJitter = false,                              // deliberately off here — see 17-jitter.md,
                                                          // which layers randomization on top of this
                                                          // exact curve rather than redefining it

        OnRetry = args =>
        {
            _logger.LogWarning(
                "Retry {Attempt} for {Dependency} after {DelayMs}ms",
                args.AttemptNumber, "redis-cache", args.RetryDelay.TotalMilliseconds);
            return default;
        }
    });

    builder.AddTimeout(TimeSpan.FromSeconds(2));  // per-attempt timeout — see 14-timeout.md
});
```

- `UseJitter` is shown explicitly set to `false` and called out in comment, not omitted — this document's example intentionally stops at the plain exponential curve so the boundary with `17-jitter.md` (which flips this to `true`, or supplies a custom jitter strategy) is visible rather than implied.
- The pipeline is registered per named dependency (`"redis-cache-read"` here; a separate named pipeline per Redis, RDBMS, Kafka producer, moderation adapter, etc.), consistent with v1's existing pattern of one resilience pipeline per outbound call, not one global policy — so `MaxRetryAttempts` and any dependency-specific `ShouldHandle` classification (`15-retry.md`'s concern) can differ per dependency while the `base`/`factor`/`cap` triple from Section 2 stays uniform across all of them.

---

## 6. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| Why not immediate retry | Retry storm: many instances × many concurrent requests all retrying at once overloads a dependency exactly while it's recovering | ANFR-01, ANFR-04 |
| Formula | `delay(attempt) = min(base × factor^(attempt−1), cap)` | — |
| Base delay | 100 ms | ANFR-05 (first retry stays close to a healthy round-trip) |
| Growth factor | 2× (doubling) | ANFR-04 (progressively less pressure on a struggling dependency) |
| Cap | 30 s | ANFR-04 (bounds worst-case resumption delay for async paths); binding constraint only for long-running async retries, not synchronous ones |
| Composition with attempt budget | `15-retry.md` truncates the curve defined here; synchronous calls exhaust their budget at ~100/200/400ms, async calls can run the curve out to the 30s cap | ANFR-05, ANFR-06 |
| Residual gap | Deterministic curve keeps synchronized callers in lockstep across retry rounds — handed to `17-jitter.md` | ANFR-04 |
| .NET implementation | Polly `RetryStrategyOptions` with `BackoffType = DelayBackoffType.Exponential`, `Delay`, `MaxDelay`, per-dependency named pipelines | Consistent with `nfr-resilience.md` Section 4.2 |
