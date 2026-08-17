# Circuit Breaker

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/prompt@review-desig.md`.
**Builds on (does not replace):** `v1/design/nfr-resilience.md` Section 6, which explicitly deferred circuit breakers as a v1 exception — reasoning that a single-instance, single-datastore PoC with no committed SLA has "nothing to trip away from." This document is where that deferred item gets designed, now that v2 introduces multiple dependencies (primary DB, Redis, Elasticsearch, message broker, external moderation-check HTTP call) each with its own failure mode and no guarantee of instant recovery.
**Related v2 documents (by filename, not duplicated here):** `14-timeout.md`, `15-retry.md`, `16-exponential-backoff.md`, `17-jitter.md` (the per-call resilience primitives circuit breaker sits on top of, not a replacement for), `19-bulkhead.md` (isolates *capacity*, e.g., thread/connection pools, per dependency — a different, complementary failure-containment axis from circuit breaker's *stop calling* axis), `03-elasticsearch-vs-sql-server.md` and `04-elasticsearch-vs-mongodb.md` (the Elasticsearch analytics-event store used as the worked example below), `05-kafka-comaporison.md` (the message broker referenced as a dependency), `07-redis-caching-and-invalidation.md` (the Redis cache referenced as a dependency).

---

## 1. The Problem Circuit Breaker Solves That Retry/Backoff/Jitter Don't

`15-retry.md`, `16-exponential-backoff.md`, and `17-jitter.md` all answer a version of the same question: *given that this one call just failed, how should the caller try again?* They are all designed around an implicit assumption — that the failure is **transient**: a dropped packet, a momentary GC pause on the other end, one overloaded replica behind a load balancer that will clear up in milliseconds to seconds. Under that assumption, retrying (with backoff and jitter to avoid synchronized retry storms, per those documents) is the right move, because the next attempt has a real chance of succeeding.

Circuit breaker exists for the other case: the dependency is **not transiently slow, it is down** — the Elasticsearch cluster is unreachable, the message broker's partition leader election is stuck, the moderation-check vendor is having a multi-minute outage, the primary DB has exhausted its connection pool. In that world, retry-with-backoff is not a mitigation, it is a second problem stacked on the first:

- **Every caller still pays the full latency cost of the doomed call before failing** — a 2-5s timeout budget (per `14-timeout.md`) times however many retry attempts `15-retry.md`/`16-exponential-backoff.md` configure, per request, for every request hitting the broken dependency, for the entire duration of the outage. At 10M-100M fetches/day, that is not a rare inconvenience; it's sustained added latency (or outright request pile-up) across the whole caller population for as long as the outage lasts.
- **Retrying against an already-overloaded or down dependency doesn't help it recover — it actively delays recovery.** Each retried request is still a connection attempt, still a query the struggling ES cluster or contended DB has to accept and fail (or queue and time out), still load on a moderation-check vendor that is already returning 503s. Backoff and jitter reduce *synchronized* retry storms (`17-jitter.md`'s concern) but do nothing to stop the *aggregate* retry volume from an entire fleet of API instances from continuing to hammer a dependency that needs headroom to come back up.
- **Threads/connections tied up waiting on a call that is statistically very unlikely to succeed** compound into the exact resource-exhaustion failure mode `19-bulkhead.md` defends against from the isolation side — circuit breaker attacks the same problem from the "stop trying" side instead of the "cap how much can be tried" side.

**The concrete fix:** once a dependency's failure rate crosses a threshold, stop calling it — fail fast, locally, in microseconds, without a network round-trip — and periodically test with a small trickle of traffic whether it has recovered, rather than letting every request rediscover the outage the expensive way. This is precisely what retry/backoff/jitter cannot do on their own, because they operate per-call with no memory of *how many other calls just failed the same way*. Circuit breaker adds that memory.

---

## 2. The Three States, Worked Example: Elasticsearch Analytics-Query Call

Circuit breaker is a state machine wrapped around a specific outbound call. This system needs one per dependency (primary DB, Redis, Elasticsearch, message broker producer, moderation-check HTTP client) — each gets its own instance, its own thresholds, and critically its own fallback, because "what to do when open" is different for a cache miss than for a failed write to the system of record. The worked example below is the **Elasticsearch analytics-query call** — the read path that powers a link's click-count/trend display (per `03-elasticsearch-vs-sql-server.md`) — chosen deliberately because a failure there is low-consequence: the core redirect (`GET /{code}` → target URL) does not depend on Elasticsearch at all, only the analytics-enrichment portion of a link-detail response does. That makes it the clearest illustration of "fail fast, don't fail the whole request," without the added complexity of also having to reason about data-loss risk (contrast the message broker producer or the primary DB, where "open" has to mean something more careful than "silently drop").

### 2.1 Closed (normal operation)

- The circuit is closed by default: every call to the ES analytics query executes normally, over the timeout/retry/backoff pipeline defined in `14-timeout.md`/`15-retry.md`/`16-exponential-backoff.md`.
- The breaker passively observes outcomes — success, failure (timeout, 5xx, connection refused, ES cluster-unavailable exception) — over a rolling sample window (Section 3) without altering behavior. This is the only state in which real ES traffic flows.

### 2.2 Open (tripped)

- Once the trip condition (Section 3) is met, the breaker flips to open and stays there for a fixed **break duration** (Section 3).
- While open, calls to the ES analytics query **never reach the network**. The breaker short-circuits immediately in-process (sub-millisecond) and the caller executes the fallback (Section 4) instead.
- This is the state that protects both the caller (no multi-second timeout tax per request) and Elasticsearch itself (zero additional load from this call path while it recovers).

### 2.3 Half-Open (probing)

- After the break duration elapses, the breaker moves to half-open and allows a small, deliberately limited number of trial calls through to real Elasticsearch (Section 5).
- If the trial calls succeed, the breaker closes and normal traffic resumes. If any trial call fails, the breaker reopens immediately and the break duration timer restarts — it does not wait for a fresh full sample window to fail again.

---

## 3. Trip Conditions (Concrete Numbers)

Polly's `CircuitBreakerStrategy` (the .NET 8/9 resilience library successor to Polly v7's `CircuitBreakerPolicy`) implements this as a **rolling sample window with a failure-ratio threshold and a minimum throughput floor**, rather than "N consecutive failures," which is the right primitive here — consecutive-failure counting breaks down under concurrent load (many requests in flight simultaneously means "consecutive" is ambiguous) and doesn't distinguish "2 failures out of 2 calls" (meaningless at low traffic) from "2 failures out of 200 calls" (fine).

For the Elasticsearch analytics-query call specifically:

| Parameter | Value | Rationale |
|---|---|---|
| **Sampling duration** | 30 seconds, rolling | Long enough to smooth over a single-digit-second network blip, short enough that a real outage is detected within one sampling window at this call's expected volume. |
| **Minimum throughput** | 20 calls in the sampling window | Prevents a handful of calls during a quiet period from being statistically meaningless — Polly will not evaluate the failure ratio at all until this floor is met, avoiding a false trip from "2 failures out of 3 calls right after startup." |
| **Failure ratio threshold** | 50% | Chosen deliberately looser than the primary-DB breaker would use (see Section 6 note below) — analytics-query failures are annoying, not corrupting, so this breaker should tolerate a rockier ES cluster before giving up on it, versus a breaker guarding a write to the system of record, which should trip earlier and more conservatively. |
| **Break duration** | 30 seconds | Short enough that a transient ES cluster hiccup (e.g., a shard relocation, a GC pause on a data node) self-heals without a long user-visible degradation window; long enough that repeatedly probing a genuinely down cluster every few hundred milliseconds doesn't itself become load. |

**Trip condition in one sentence:** *if at least 20 calls occur in a rolling 30-second window and 50% or more of them fail, the circuit opens for 30 seconds.*

These numbers are placeholders in the same spirit `12-distributed-rate-limiting.md` and `nfr-resilience.md` Section 4.3 use for their own thresholds — a reasonable starting point derived from the call's risk profile, not a tuned production value; they should be revisited once real ES latency/error telemetry exists (`nfr-resilience.md`'s own observability traceability, ANFR-10, is what would supply that telemetry).

---

## 4. What Happens While the Circuit Is Open: the ES Fallback

The entire value of tripping the breaker is wasted if "open" just means the caller gets an exception instead of a timeout — the fallback is what actually protects the user-facing request. For the ES analytics-query call, the fallback is chosen per-endpoint based on what data is available and how stale it's allowed to be:

- **Link-detail response including a click-count widget:** serve the **last successfully cached count** from Redis (per `07-redis-caching-and-invalidation.md`'s existing cache-aside layer for hot link metadata) if one exists, tagged with a `stale: true` / `asOf: <timestamp>` marker in the response so the caller can distinguish "live count" from "count as of the last time ES was healthy." This is strictly better than blocking the whole link-detail request on a dependency that has already been observed to be failing.
- **No cached value available** (e.g., a link nobody has viewed the analytics for recently): **omit the analytics field from the response entirely** rather than blocking or erroring the whole request — the response returns the link's core metadata (target URL, creation date, owner) with an `analyticsUnavailable: true` flag, HTTP 200, not a 503 for the whole endpoint. The redirect path itself (`GET /{code}`) never touches this call at all, so it is entirely unaffected regardless of ES health — that separation is what makes "fail fast, don't fail the whole request" true here by construction, not just by fallback design.
- **What the fallback explicitly does not do:** it does not retry against ES while open (that's the breaker's entire point), and it does not silently return a fabricated `0` click count — a stale-but-labeled count or an explicit "unavailable" flag preserves the same "never lie to the caller" principle `nfr-resilience.md` Section 2.2 establishes for the primary DB.

This is also the reason the ES breaker is a good teaching example: the fallback is genuinely cheap and safe (serve stale or omit) precisely because analytics is read-only, denormalized, and non-critical-path per `03-elasticsearch-vs-sql-server.md`'s scope split. Contrast the message broker producer (Section 6): there, "open" cannot mean "silently drop the event," because that is data loss, not degraded UX, so its fallback has to be different (buffer/spool or reject-with-retry-guidance to the caller, not a stale placeholder).

---

## 5. Half-Open Behavior: Probing Without Reopening the Floodgates

The failure mode half-open exists to prevent is: breaker closes fully the instant the break duration expires, the entire fleet's backlog of deferred/queued analytics calls floods back in simultaneously, and the still-recovering ES cluster (which may have just come back up with cold caches, a rebalancing shard, or a data node still catching up) gets immediately re-overwhelmed by the full traffic volume — tripping the breaker open again in seconds, in a cycle that never lets the cluster stabilize.

Polly's half-open state avoids this with a **limited trial-call budget**:

- On transition to half-open, only a small fixed number of calls (Polly's default is a small handful; this system sets it explicitly rather than relying on the default) are allowed through to real Elasticsearch. Every other call arriving while half-open is still short-circuited to the fallback, exactly as if the circuit were open.
- If the trial calls all succeed, the breaker transitions to closed and full traffic resumes normally.
- If any trial call fails, the breaker immediately reopens for a fresh break duration — it does not average across the trial batch or give ES a second chance mid-probe.
- Because trial calls are a small fraction of total traffic, a cluster that has *actually* recovered gets validated without being retested by the full request volume, and a cluster that has *not* recovered gets detected again cheaply (a handful of failed calls, not thousands) before the breaker reopens.

This is the mechanism that makes circuit breaker fundamentally different from "just wait N seconds and resume" — it resumes traffic gradually and conditionally, with the system's own observed behavior as the gate, not a fixed clock.

---

## 6. Implementation in .NET: Polly's CircuitBreaker Strategy

.NET 9's resilience story is `Microsoft.Extensions.Resilience` (built on Polly v8), the same library `nfr-resilience.md` Section 4.2 already recommends for the moderation-check HTTP client — this document extends that established pattern to the ES analytics client rather than introducing a second resilience library.

```csharp
// Infrastructure/DependencyInjection.cs
services.AddHttpClient<IAnalyticsQueryClient, ElasticsearchAnalyticsQueryClient>(client =>
    {
        client.BaseAddress = new Uri(configuration["Elasticsearch:BaseUrl"]!);
    })
    .AddResilienceHandler("es-analytics-query", builder =>
    {
        // Per-attempt timeout — see 14-timeout.md for the system-wide policy this follows.
        builder.AddTimeout(TimeSpan.FromSeconds(2));

        // Circuit breaker: trip on sustained failure, not a single blip.
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,                              // 50% failure ratio (Section 3)
            SamplingDuration = TimeSpan.FromSeconds(30),      // rolling 30s window
            MinimumThroughput = 20,                           // don't evaluate below 20 calls/window
            BreakDuration = TimeSpan.FromSeconds(30),         // stay open 30s before probing
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is not null ||
                args.Outcome.Result?.StatusCode is >= HttpStatusCode.InternalServerError),
            OnOpened = args =>
            {
                _logger.LogWarning(
                    "ES analytics circuit opened. FailureRatio={Ratio}",
                    args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString());
                return ValueTask.CompletedTask;
            },
            OnClosed = _ => { _logger.LogInformation("ES analytics circuit closed."); return ValueTask.CompletedTask; },
        });
    });
```

```csharp
public interface IAnalyticsQueryClient
{
    /// <summary>
    /// Returns click-count/trend data for a link, or throws BrokenCircuitException
    /// if the circuit is open/half-open-and-rejected. Callers must catch
    /// BrokenCircuitException and apply the Section 4 fallback (stale cache
    /// read or omit-analytics-field) — the client never fabricates a result.
    /// </summary>
    Task<AnalyticsSummary> GetSummaryAsync(Guid shortUrlId, CancellationToken ct);
}

public sealed class LinkDetailService
{
    private readonly IAnalyticsQueryClient _analytics;
    private readonly IDistributedCache _cache; // per 07-redis-caching-and-invalidation.md

    public async Task<LinkDetailResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var core = await _linkRepository.GetAsync(id, ct); // unaffected by ES health

        try
        {
            var summary = await _analytics.GetSummaryAsync(id, ct);
            return LinkDetailResponse.WithLiveAnalytics(core, summary);
        }
        catch (BrokenCircuitException)
        {
            var stale = await _cache.GetAsync($"analytics:summary:{id}", ct);
            return stale is not null
                ? LinkDetailResponse.WithStaleAnalytics(core, stale)
                : LinkDetailResponse.WithoutAnalytics(core); // analyticsUnavailable: true
        }
    }
}
```

`Microsoft.Extensions.Resilience`'s `CircuitBreakerStrategy` throws `BrokenCircuitException` for calls rejected while open or half-open-and-over-budget — that exception type is the caller-side signal to invoke the fallback, distinct from the underlying `HttpRequestException`/`TimeoutRejectedException` a genuine in-flight failure would throw, which keeps "the circuit is protecting me" distinguishable in logs/metrics from "this one call failed."

---

## 7. Per-Dependency Notes (Not a Full Design — See Trip Condition Caveat)

This document's worked example is Elasticsearch deliberately, because its fallback is cheap and safe. The same three-state mechanism applies to the system's other dependencies, but **the numbers and especially the fallback are not interchangeable** — each dependency's breaker must be sized and, more importantly, must fail-open in a way appropriate to what that dependency backs:

| Dependency | Failure-ratio/window (starting point) | Fallback when open |
|---|---|---|
| **Redis cache** | Looser (e.g., 60% over 30s) — cache is already an optional accelerant per `07-redis-caching-and-invalidation.md` | Bypass cache, read primary DB directly (cache-aside's natural fallback — this is closer to "circuit breaker as an optimization to skip a doomed cache round-trip" than a data-safety concern). |
| **Primary DB** | Tighter (e.g., 30-40% over a shorter window) — a struggling primary is the highest-consequence dependency in the system | No safe generic fallback for writes (per `nfr-resilience.md` Section 2.2, never fabricate success); for reads, at most a bounded stale-cache serve, not a broader trip-and-forget — this breaker exists mainly to fail fast (503 quickly) rather than hang every caller on a saturated connection pool, not to substitute a fallback data source. |
| **Message broker producer** | Tighter, similar to DB | Cannot silently drop (data loss) — fallback is a bounded local spool/outbox-style buffer (see `20-outbox-pattern.md`) or an explicit reject-with-retry-guidance to the caller, never a fabricated "accepted." |
| **Moderation-check HTTP call** | As `nfr-resilience.md` Section 4 already budgets via timeout+retry; circuit breaker is the natural v2 addition on top of that v1 exception once call volume makes repeated timeout-then-fail cycles measurably hurt create-link latency (the exact condition `nfr-resilience.md` Section 6 names as the trigger to revisit) | Fail the create-link request with a clear "moderation check unavailable, try again shortly" 503 — per that document's stance, a link must never be created having silently skipped its safety check, so "allow-all while open" is not an acceptable fallback here, unlike the ES case. |

Sizing and fully specifying each of these breakers is future work beyond this document's scope (the ES worked example above is intentionally the complete, concrete case); the point of this table is that "add a circuit breaker" is not a single decision applied uniformly — the fallback choice is the load-bearing design decision, and it is dependency-specific.

---

## 8. Summary

| Concern | Decision |
|---|---|
| Problem circuit breaker solves | Persistent (not transient) dependency failure — retry/backoff/jitter alone keep paying full latency+load cost per request for the whole outage duration; breaker adds fleet-wide memory of "this dependency is down" and fails fast instead. |
| Worked example | Elasticsearch analytics-query call — low-consequence, read-only, denormalized, separate from the core redirect path. |
| States | Closed (normal) → Open (short-circuit to fallback, zero ES load) → Half-Open (small trial batch decides close vs. reopen). |
| Trip condition (ES example) | ≥20 calls in a rolling 30s window, ≥50% failure ratio → open for 30s. |
| Fallback while open (ES example) | Serve last cached click count from Redis with a `stale`/`asOf` marker; if no cache entry, omit the analytics field (`analyticsUnavailable: true`), HTTP 200 — never block the whole link-detail request, never fabricate a count. |
| Half-open behavior | Small fixed trial-call budget probes real ES; all succeed → close; any failure → reopen immediately for a fresh break duration. |
| .NET implementation | `Microsoft.Extensions.Resilience`'s `AddCircuitBreaker` on the same `AddResilienceHandler` pipeline `nfr-resilience.md` Section 4.2 already uses for the moderation-check client; caller catches `BrokenCircuitException` to invoke the fallback. |
| Other dependencies | Same three-state mechanism, different thresholds and — critically — different fallbacks (Section 7); primary DB and message broker cannot use a "serve stale/omit" fallback the way ES can, because their failures risk data loss or silent write failure, not just stale analytics. |
