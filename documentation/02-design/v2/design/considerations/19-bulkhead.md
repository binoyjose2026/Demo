# Bulkhead Isolation for Shared Resource Pools

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/agent-prompt.md`.
**Builds on (does not replace):** `v1/design/nfr-resilience.md` Section 6, which explicitly deferred bulkheads/connection-pool isolation as a v1 exception ("v1 has no other outbound calls to isolate against and a single `AppDbContext` pool is already scoped per-request") — this document is where that deferred item gets designed, now that v2 introduces exactly the additional outbound calls (Elasticsearch indexing, moderation-check) that make the deferral no longer valid.
**Traces to:** ANFR-01/ANFR-05/ANFR-06 (redirect availability and low latency — `v1/design/fn-fetch.md` establishes this as "the single most frequently exercised operation in the system"), ANFR-04 (graceful degradation on backend failure).
**Related v2/v1 documents (by filename, not duplicated here):** `18-circuit-breaker.md` (failure-state-based protection — complements, does not overlap with, this document — see Section 3), `14-timeout.md`, `15-retry.md`, `16-exponential-backoff.md`, `17-jitter.md` (per-call resilience shaping, orthogonal to the concurrency-partitioning concern here), `03-elasticsearch-vs-sql-server.md` / `04-elasticsearch-vs-mongodb.md` (the Elasticsearch indexing dependency this document isolates), `v1/design/nfr-resilience.md` Section 4 (the moderation-check `HttpClient` and its existing timeout/retry pipeline, which this document adds a concurrency cap to, not replaces), `v1/design/fn-fetch.md` (the redirect path this document protects).

---

## 1. The Problem, Concretely, for This System

`fn-fetch.md` is unambiguous about the redirect path's priority: `GET /{shortCode}` is "the single most frequently exercised operation in the system" (ANFR-01, ANFR-05), and at v2 scale (10M-100M fetches/day vs. 1M-5M creates/day) that gap only widens. Nothing about the redirect path's own logic changed for v2 — it is still one indexed point read plus a fire-and-forget analytics write. What changed is everything running *around* it in the same process.

v2 adds outbound dependencies that v1 never had:

- **Elasticsearch indexing** on the create path (and possibly a background reindex/backfill job) — see `03-elasticsearch-vs-sql-server.md`.
- **The moderation-check HTTP call** on the create path — already designed in `v1/design/nfr-resilience.md` Section 4, with a timeout+retry pipeline, but **no concurrency cap**.
- Redis calls for caching (`07-redis-caching-and-invalidation.md`) and distributed rate-limit counters (`12-distributed-rate-limiting.md`).

All of these share the same process: the same CLR thread pool, the same default `HttpClient`/`SocketsHttpHandler` connection limits, and — if nothing is done — the same implicit "however many concurrent calls happen to be in flight" ceiling. **This is the concrete failure mode a bulkhead exists to prevent:** if Elasticsearch degrades (a slow cluster, a GC pause, a hot shard) or the moderation-check provider degrades (exactly the scenario `nfr-resilience.md` Section 6 already names as the trigger for revisiting circuit breakers), every in-flight call to that dependency holds a thread and a connection slot for the duration of its timeout window. Under sustained load, *outstanding calls to the slow dependency accumulate faster than they drain*, and without a hard ceiling on how many can be outstanding at once, they are free to grow until they exhaust a shared, finite resource — the thread pool's available worker threads, or the process's outbound connection capacity.

The redirect path has **nothing to do with Elasticsearch or moderation-check** — it only touches SQLite/RDBMS and Redis (per `fn-fetch.md` and `07-redis-caching-and-invalidation.md`). But if the thread pool is exhausted by threads blocked waiting on a slow Elasticsearch call, the redirect controller's request cannot get a thread to even *start* running, regardless of how fast its own dependencies are. This is the textbook definition of the failure this pattern is named for: one compartment flooding sinks the whole ship because there are no watertight bulkheads between compartments. A dependency used exclusively by lower-priority, lower-volume work (indexing, moderation) starves the highest-priority, highest-volume path (redirect) of a resource — CPU/thread-pool capacity, outbound connections — that has no logical connection to the failing dependency at all.

This is worse than an ordinary cascading failure, because it is **invisible in the failing dependency's own health metrics**. Elasticsearch can be reporting degraded-but-not-down while the real damage — redirect latency spiking or requests timing out — shows up in a completely different subsystem's dashboard. Without partitioned resource pools, on-call engineers chasing a redirect-latency alert would have no reason to look at the analytics-indexing pipeline.

---

## 2. Resource Partitioning Design

The fix is to give each workload/dependency its own **bounded, independent** concurrency ceiling, so no one workload can consume resources beyond its allotted share — even in the worst case where it is failing continuously. Sized correctly, a bulkhead's ceiling is a **capacity budget**, not a throughput target: it should comfortably clear normal peak concurrency for that workload, with headroom, while still being low enough that pegging it out cannot touch anyone else's share.

| Compartment | What it protects | Starting concurrency limit | Reasoning |
|---|---|---|---|
| **Redirect path — DB/cache reads** | `IShortUrlRepository.GetByShortCodeAsync` (SQLite/RDBMS) and the Redis cache read (`07-redis-caching-and-invalidation.md`) | **200** concurrent calls | Highest volume, highest priority path in the system (`fn-fetch.md` Section 1). Sized to the largest pool in the process — this is the compartment that must never be the one running out of room. A point-read index seek plus a cache `GET` is a single-digit-millisecond operation, so 200 concurrent in-flight calls represents very high sustained throughput before the ceiling is even approached; this is deliberately generous headroom, not a tight budget. |
| **Create path — DB writes** | `SaveChangesAsync` via `IUnitOfWork` for the create-link write | **50** concurrent calls | Much lower volume than redirect (1M-5M/day vs. 10M-100M/day) and already serialized in part by the database itself; 50 is generous relative to expected concurrent create load while still being a real ceiling, not "unbounded." |
| **Moderation-check HTTP client** | `IMaliciousDomainChecker` (`v1/design/nfr-resilience.md` Section 4) | **10** concurrent calls | Lower volume by construction — one call per create, and create is already the lower-volume path. Deliberately kept **small and tight**: this is exactly the dependency `nfr-resilience.md` Section 6 flags as a candidate for future circuit-breaker protection because a third-party provider's outages are the least predictable failure mode in the system. A small ceiling means that even if the provider degrades to 100% failures held open for the full timeout budget, at most 10 threads/connections are ever tied up on it — not "unbounded growth," which is the exact phrase this document's task exists to prevent. |
| **Elasticsearch indexing** | The write-side indexing call from the create/analytics pipeline (`03-elasticsearch-vs-sql-server.md`) | **20** concurrent calls | Asynchronous/fire-and-forget relative to the caller in most designs, so it can tolerate a queue better than a synchronous call can — but still capped, because an unbounded indexing backlog is exactly what would eventually exhaust the pool if the cluster degrades. 20 gives it real throughput for catch-up/backfill without granting it a share comparable to the redirect or create paths. |
| **Redis — rate-limit counters** | `12-distributed-rate-limiting.md`'s sliding-window Lua script calls | **50** concurrent calls | Shares the same Redis deployment as the cache read above but is logically distinct traffic (`ratelimit:*` namespace); given its own pool so a Redis slowdown affecting rate-limit checks cannot borrow capacity from — or starve — the cache-read pool that redirect depends on. |

**Reasoning behind the shape of this table, not just the numbers:**

- **The partition boundary is drawn per dependency *and* per priority tier, not just per dependency.** Redirect's DB/cache calls and create's DB write both eventually touch the same physical database, but they are still isolated from each other, because create-path DB pressure (e.g., a burst of link creation) must not be able to starve redirect-path DB pressure — the two workloads have very different priority even though they share infrastructure underneath.
- **The numbers are relative, not absolute, and are a starting point, not a tuned SLA.** Consistent with this project's established practice of naming placeholder thresholds explicitly (`v1/design/nfr-resilience.md` Section 4.3, `12-distributed-rate-limiting.md` Section 4), these figures are sized by *relative* priority and volume (redirect > create-write > Elasticsearch/rate-limit > moderation-check), not derived from load-tested capacity numbers, because no production load test exists yet at v2 scale. They should be revisited once real concurrency/latency telemetry exists per compartment.
- **The sum of all ceilings must fit comfortably inside the process's actual resource budget** (thread-pool max, `SocketsHttpHandler` connection limits, host CPU/memory) — a bulkhead only protects against one compartment overrunning *its own* allotment; it does nothing if the allotments themselves are set higher, in aggregate, than the box can actually sustain. Sizing bulkheads is therefore a joint exercise with the deployment/hosting sizing, not a purely per-dependency decision.

---

## 3. How This Differs From, and Complements, Circuit Breaker

Bulkhead and circuit breaker (`18-circuit-breaker.md`) are both concurrency/failure-control patterns and are easy to conflate, but they answer different questions and this system needs both, not either:

| | Bulkhead | Circuit Breaker |
|---|---|---|
| **What it limits** | *How many* calls to a dependency may be in flight at once, regardless of whether those calls are succeeding or failing. | *Whether new calls are attempted at all*, based on the dependency's recent failure/success history. |
| **Trigger** | Concurrency count crossing a fixed ceiling. | Failure rate/count crossing a threshold. |
| **Behavior when active** | The (N+1)th concurrent call is rejected or queued (Section 5) — calls that are within the ceiling still go through normally, even if every one of them is about to fail. | Calls are short-circuited immediately without attempting the dependency at all, once the breaker trips open. |
| **Protects against** | Resource exhaustion (threads, connections) caused by *volume* of concurrent calls — including volume of calls that are individually succeeding but slow, or volume of calls to a dependency that is 100% healthy but simply under heavy legitimate load. | Wasted latency/resources spent repeatedly retrying a dependency that is *known*, from recent history, to be failing. |

**Why both are needed together, concretely for the moderation-check dependency:** a bulkhead alone caps the moderation-check pool at 10 concurrent calls, but if the provider is failing 100% of requests and each failure only surfaces after the full timeout budget (`nfr-resilience.md` Section 4.2's 2s/5s budget), those 10 slots stay **permanently occupied by calls that are guaranteed to fail** — the bulkhead prevents unbounded growth but does not prevent the bounded pool from being *saturated* by useless work. A circuit breaker layered on top solves exactly this: once failures cross its threshold, it trips open and new calls fail fast without ever occupying a bulkhead slot, freeing that small 10-slot pool to serve any calls that would actually have a chance of succeeding (or to sit idle, which is strictly better than being clogged with doomed calls). Conversely, a circuit breaker alone, with no bulkhead, does nothing to prevent a *healthy* dependency from being overwhelmed by legitimate concurrent volume — a circuit breaker only reacts to failures, and a dependency under load that is still succeeding (just slowly) will not trip a failure-rate-based breaker, yet can still exhaust a shared, unpartitioned resource pool exactly as described in Section 1.

The two patterns compose cleanly in a Polly resilience pipeline: bulkhead as the outermost concurrency gate, circuit breaker inside it deciding whether an admitted call is even attempted. See `18-circuit-breaker.md` for the circuit breaker's own state machine, thresholds, and configuration — not repeated here.

---

## 4. Implementation in .NET

**Recommendation:** use Polly's rate limiter strategy (`AddConcurrencyLimiter`, the modern replacement for the legacy `Policy.Bulkhead` API in Polly v8/`Microsoft.Extensions.Resilience`) per dependency, layered onto the same `AddResilienceHandler` pipelines `nfr-resilience.md` Section 4.2 already established for the moderation-check `HttpClient` — consistent with this project's stance of using standard, well-known .NET building blocks rather than hand-rolled infrastructure.

```csharp
// Infrastructure/DependencyInjection.cs
// Extends the moderation-check HttpClient pipeline from nfr-resilience.md
// Section 4.2 with a concurrency bulkhead — timeout/retry are unchanged
// and not repeated here (see nfr-resilience.md, 15-retry.md, 14-timeout.md).
services.AddHttpClient<IMaliciousDomainChecker, MaliciousDomainChecker>(client =>
    {
        client.BaseAddress = new Uri(configuration["ModerationCheck:BaseUrl"]!);
        // Separate SocketsHttpHandler connection ceiling per dependency —
        // isolates this client's TCP connections from every other HttpClient
        // in the process (Elasticsearch, Redis uses its own multiplexer).
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 10,   // mirrors the bulkhead ceiling below
    })
    .AddResilienceHandler("moderation-check", builder =>
    {
        builder.AddConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 10,      // Section 2: moderation-check ceiling
            QueueLimit = 5,        // small queue, not unbounded — Section 5
        });

        // Existing timeout + retry from nfr-resilience.md Section 4.2 go here,
        // inside the concurrency gate, so a queued-then-admitted call still
        // gets the full per-attempt/overall timeout budget.
        builder.AddTimeout(TimeSpan.FromSeconds(2));
        builder.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 2 /* ... */ });
        builder.AddTimeout(TimeSpan.FromSeconds(5));
    });
```

For a non-`HttpClient` dependency without a first-party Polly integration (e.g., isolating the redirect path's DB access from the create path's DB access, both against the same underlying connection pool), a plain `SemaphoreSlim` gate around the call achieves the same effect without pulling in the full resilience-pipeline machinery for a single concern:

```csharp
public sealed class BulkheadGate
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _queueLimit;
    private int _queuedCount;

    public BulkheadGate(int permitLimit, int queueLimit)
    {
        _semaphore = new SemaphoreSlim(permitLimit, permitLimit);
        _queueLimit = queueLimit;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _queuedCount) > _queueLimit)
        {
            Interlocked.Decrement(ref _queuedCount);
            throw new BulkheadRejectedException(); // mapped to 503 (Section 5)
        }

        try
        {
            await _semaphore.WaitAsync(ct);
            try { return await operation(); }
            finally { _semaphore.Release(); }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedCount);
        }
    }
}
```

Registered as a singleton per compartment (`services.AddSingleton<BulkheadGate>(_ => new BulkheadGate(permitLimit: 200, queueLimit: 50));` for the redirect DB/cache gate, a separately-named instance for create-path writes), and injected into the repository/service layer that makes the call it protects — never shared across compartments, or the isolation this whole pattern exists for is lost.

---

## 5. Behavior When a Bulkhead Is Full

A bulkhead that queues without limit is not a bulkhead — it has just moved the unbounded-growth problem from "concurrent executing calls" to "concurrent queued calls," and a large enough queue still exhausts memory and still leaves callers waiting indefinitely, which is exactly the graceful-degradation failure `nfr-resilience.md` Section 2.2 already rejects for the database path ("a clean, typed 503, not a hang or a garbled response"). This document applies the same principle to concurrency limits:

- **Admit up to the permit limit** (Section 2's numbers) to execute immediately.
- **Queue a small, fixed number beyond that** — not unbounded — sized as a fraction of the permit limit (e.g., 5 for moderation-check's 10-permit pool, 50 for redirect's 200-permit pool): enough to smooth a brief burst, not enough to let latency creep unboundedly upward while callers wait.
- **Reject immediately once the queue is also full**, rather than adding a request that has no realistic chance of completing within the caller's own timeout budget. The rejection surfaces as `503 Service Unavailable`, consistent with the existing status-code mapping table in `nfr-resilience.md` Section 3.3 (`DbUpdateException`/`TimeoutException` → 503, "transient; safe to retry with backoff") — a bulkhead rejection is exactly this kind of transient condition, and reusing the same status code/caller-guidance means clients do not need a new error category to handle it. `15-retry.md`/`16-exponential-backoff.md`/`17-jitter.md` govern how a caller *should* retry that 503 — not repeated here.

**Why fail fast rather than let requests pile up:** a rejected request fails in microseconds and frees the caller (and, on the server side, whatever thread/connection was allocated to accept the request) immediately. A request left queued indefinitely behind a saturated, possibly-failing dependency instead ties up resources for the full duration of its wait *and* the eventual attempt — worse than rejecting it outright, and precisely the "unbounded growth exhausts the pool" failure mode from Section 1, just relocated one level up the stack. This is also why the bulkhead's queue limit must be small relative to its permit limit: a large queue delays the failure without preventing it, giving a false sense of resilience while still eventually producing the same resource exhaustion, only after a longer, more confusing latency ramp for whoever is debugging it.

---

## 6. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| Whether bulkheads are needed at v2 scale | Yes — v1's deferral rationale (single dependency, no isolation needed) no longer holds once Elasticsearch and moderation-check are added as real outbound dependencies alongside the redirect path | `nfr-resilience.md` §6 |
| Partitioning boundary | Per dependency *and* per priority tier (redirect DB/cache, create-write, moderation-check, Elasticsearch, rate-limit Redis) — five independent pools, never shared | Section 2 |
| Starting concurrency ceilings | Redirect 200, create-write 50, Elasticsearch 20, rate-limit Redis 50, moderation-check 10 — sized by relative volume/priority, explicitly a placeholder pending real telemetry | Section 2 |
| Relationship to circuit breaker | Complementary, not overlapping — bulkhead caps concurrency regardless of failure state; circuit breaker stops attempts based on failure state; both layered together in one Polly pipeline | Section 3; `18-circuit-breaker.md` |
| .NET implementation | Polly `AddConcurrencyLimiter` inside `AddResilienceHandler` for `HttpClient`-based dependencies (with per-client `SocketsHttpHandler.MaxConnectionsPerServer`); `SemaphoreSlim`-based `BulkheadGate` for non-HTTP dependencies (DB pools) | Section 4 |
| Behavior on saturation | Small bounded queue beyond the permit limit, then immediate `503 Service Unavailable` — reuses the existing `nfr-resilience.md` §3.3 status-code contract, no new error category | Section 5 |
