# Timeout Pattern for Outbound Calls at Extreme Scale

**Scope:** v2 scalability review — one of six sibling resilience documents produced against `documentation/02-design/v2/agents/prompt@review-desig.md`. This document covers **Timeout only**. Retry, Exponential Backoff, Jitter, Circuit Breaker, and Bulkhead are each a separate document — see `15-retry.md`, `16-exponential-backoff.md`, `17-jitter.md`, `18-circuit-breaker.md`, `19-bulkhead.md`. Where those patterns matter, they are cross-referenced by filename, not designed here.
**Builds on (does not replace):** `v1/design/nfr-resilience.md` §4, which already introduced Polly with a basic timeout + retry wrapper around the moderation-check HTTP call (2s per-attempt / 5s overall budget). That is the seed this document formalizes and extends to every outbound call the v2 architecture adds.
**Justifies against:** `v1/design/nfr-performance.md` §2 — the redirect path's **p95 < 50 ms / p99 < 150 ms** server-side latency target — plus the write-path and dependency shape introduced across `01-create-path-extreme-scalability.md`, `03-elasticsearch-vs-sql-server.md`, `05-kafka-comaporison.md`, and `07-redis-caching-and-invalidation.md`.
**Traces to:** ANFR-04 (graceful degradation on backend failure), ANFR-05/ANFR-06 (redirect latency and throughput), ANFR-10 (observability of errors/latency).

---

## 1. Why Every Outbound Call Needs an Explicit Timeout

`nfr-resilience.md` §4 already made this case for one call (the moderation check). At extreme scale it generalizes to **every** outbound call the v2 architecture makes — primary DB, Redis, Elasticsearch, the broker, and the moderation-check HTTP call — for a reason that is easy to underestimate: **a dependency that is slow is a worse failure mode than a dependency that is down.**

A dependency that is fully down fails fast — connections refuse immediately, the caller finds out in milliseconds and can react. A dependency that is *slow* (a DB under lock contention, Redis under GC pause, Elasticsearch mid-shard-rebalance, a broker with a full replication queue) does not fail — it hangs. Without an explicit timeout, every caller wraps the same defaults ADO.NET/`HttpClient`/Redis clients ship with (30s, 100s, or "no limit" depending on the client), and under load this compounds into the specific failure this system cannot afford at 1,157 fetches/sec average (100M/day) or bursts several multiples above that:

1. **Thread-pool exhaustion.** Each in-flight call holds a thread (or, for `async` code, an outstanding continuation and its associated connection) until it completes or times out. If calls that should take 5-50 ms instead take 30 seconds because nothing bounds them, the number of concurrent in-flight requests needed to exhaust the thread pool drops by three orders of magnitude — a dependency running at 10% of normal speed can take down the whole API tier, not just the requests actually talking to it.
2. **Connection-pool exhaustion.** `AppDbContext`'s SQL connection pool, `StackExchange.Redis`'s multiplexer, the Elasticsearch client's connection pool, and the Kafka producer's connection to brokers are all finite. A slow dependency holds pool members open longer than expected, starving *unrelated* requests that need the same pool for a healthy dependency — this is the cross-request blast radius `19-bulkhead.md` addresses directly; timeout is the precondition that makes bulkhead isolation meaningful (a bulkhead with no timeout inside it just isolates the hang instead of preventing it).
3. **Cascading latency, not cascading failure.** Without a timeout, a slow dependency doesn't show up as an error rate spike — it shows up as p99 (then p95, then p50) latency creeping upward across the whole fleet, which is harder to alert on and diagnose than a clean failure. An explicit timeout converts "slow" into "failed," which is a strictly more actionable signal for both automated recovery (§5) and human on-call response (ANFR-10).

The design rule that follows: **no outbound call in this system runs without an upper bound on how long it may take**, sized deliberately per dependency (§2), not left at a client-library default.

---

## 2. Timeout Budget per Dependency

Values are **operation timeouts** — the bound on a single logical call, not counting retry (`15-retry.md`/`16-exponential-backoff.md` layer retry *outside* these budgets, they do not extend them). §3 below defines the connection-timeout figures separately, since they are a different axis, not a subset of the same number.

| Dependency | Call | Timeout | Justification |
|---|---|---|---|
| **Redis** (`07-redis-caching-and-invalidation.md`) | `GET shorturl:v1:code:{code}` on the redirect hot path | **30 ms** | This call sits inside the ANFR-05 p95 < 50 ms server-side budget, on the *cheapest* leg of the request. A healthy in-region Redis `GET` is typically sub-5 ms; 30 ms is generous headroom (≈6x nominal) while still leaving the remaining ~20 ms of the 50 ms p95 budget for the rest of the pipeline (routing, serialization, response write) if this call has to fall through to a cache miss. It must be short specifically *because* the whole point of the cache tier is to stay off the slower DB path — a slow-but-not-failed Redis that isn't bounded defeats the latency goal caching exists to hit. |
| **Primary DB** (server-based RDBMS per `data-design-guidelines.md` §1's migration path, e.g. SQL Server/Postgres) | Redirect-path lookup (`FindRedirectTargetAsync`, cache-miss fallback) | **100 ms** command timeout | Cache-miss reads must still respect the ANFR-05 **p99 < 150 ms** ceiling. A single indexed seek (`IX_ShortUrl_Code`, unchanged from v1's `nfr-performance.md` §3) is normally single-digit milliseconds even on a server-based RDBMS with a network hop; 100 ms leaves room for the round-trip plus contention while still failing well inside the p99 target rather than consuming the whole budget on one dependency. |
| **Primary DB** | Create-path write (`SaveChangesAsync`, `ShortUrl` insert / ID-block allocation per `01-create-path-extreme-scalability.md` §2) | **500 ms** command timeout | Creates have no sub-100ms target (`nfr-performance.md` §7: creates are not a latency-optimization target), but they are far more latency-tolerant than redirects, not latency-*unbounded*. 500 ms is enough for a write plus index maintenance and typical lock-wait under the write concurrency this document's sibling (`01-create-path-extreme-scalability.md` §1.1) describes, without letting one contended write monopolize a pooled connection for seconds while other creates queue behind it. |
| **Elasticsearch** (`03-elasticsearch-vs-sql-server.md`) | Metadata/analytics search or aggregation query (AF-05, AF-10) | **2 s** request timeout | `nfr-performance.md` §7 explicitly scopes analytics/metadata reads out of the latency-critical path ("no dedicated SLA beyond general responsiveness"). 2 s is generous relative to the redirect path on purpose — Elasticsearch queries here are aggregations/search over large result sets, not single-key lookups, so the right failure mode is "fail after a real attempt," not "fail as fast as Redis." It is still bounded, not left at client-default (30-60 s), because an analytics dashboard hanging for a minute is still an unacceptable UX and still holds a connection-pool slot the whole time (§1). |
| **Broker** (Kafka or managed equivalent, `05-kafka-comaporison.md`) | Producer publish (access-event / outbox relay, off the redirect response's critical path per `nfr-performance.md` §6 and `20-outbox-pattern.md`) | **5 s** (`request.timeout.ms`/equivalent produce-request bound) | This call is already decoupled from the caller-facing response (fire-and-forget from the redirect/create handler's point of view), so it can tolerate a looser bound than anything on the request/response critical path. 5 s is not "generous because it doesn't matter" — it still matters: an unbounded produce call held open indefinitely will eventually back up the local producer buffer/outbox-relay worker pool (§1's connection-exhaustion argument applies to background workers exactly as it does to request threads), so it is bounded specifically to keep the relay's own throughput predictable, not to protect user-facing latency. |
| **Moderation-check HTTP call** | `IMaliciousDomainChecker.IsMaliciousAsync` (per-attempt) | **2 s** (unchanged from `nfr-resilience.md` §4.2) | Reconfirmed, not re-derived: v1's per-attempt figure was sized for a third-party HTTP round-trip and nothing about v2's scale changes what a *single* moderation-check round-trip should cost — creates run at 12-58/s average (1-5M/day), which does not change per-call latency expectations, only aggregate volume (handled by connection pooling/`IHttpClientFactory`, not by loosening this timeout). |
| **Moderation-check HTTP call** | Overall pipeline budget (per-attempt + retries) | **5 s** (unchanged from `nfr-resilience.md` §4.2) | Same reasoning — this is the *create* request's total tolerance for the moderation dependency before it fails the create outright; retry mechanics inside that budget belong to `15-retry.md`/`16-exponential-backoff.md`, not this document. |

**Reading the table as a whole:** timeouts get *tighter* the closer a call sits to the redirect hot path (Redis tightest, DB-on-cache-miss next) and *looser* the further a call sits from a human waiting on a response (broker publish, analytics search) — the budget is allocated against `nfr-performance.md`'s latency targets, not against a uniform policy, because a uniform timeout would either be too loose for Redis (defeating the cache) or too tight for Elasticsearch aggregations (causing false failures on legitimately slower, but healthy, queries).

---

## 3. Connection Timeout vs. Operation Timeout

These are two different failure windows and conflating them is a common source of either false failures (declaring an operation dead while it was only slow to *start*) or hangs (a client that bounds the operation but not the connection attempt underneath it):

- **Connection timeout** bounds how long the client waits to *establish* a usable connection (TCP handshake + TLS negotiation + protocol handshake, or — for pooled clients like `StackExchange.Redis`'s multiplexer or EF Core's connection pool — how long to acquire an already-open connection from the pool). This fires rarely in steady state (connections are typically long-lived and pooled) but is exactly what fires during a cold start, a network partition, or pool exhaustion under load — the scenario where the dependency isn't slow to *answer*, it's unreachable or the pool is starved.
- **Operation timeout** (the values in §2) bounds how long the client waits for the *call itself* to complete once a connection is already in hand — the query to run, the `GET` to return, the message to be acknowledged. This is the one that catches a dependency that accepted the connection but is slow to do the actual work (lock contention, GC pause, shard rebalance).

**Both must be set, and both must be tighter than client-library defaults**, because they catch different failure shapes:

| Timeout type | Typical v2 setting | What it alone would miss if unset |
|---|---|---|
| Connection timeout | 500 ms – 1 s (Redis `ConnectTimeout`, DB pool acquisition, ES/Kafka connect) | Without it: a partitioned or overloaded dependency that never completes the handshake hangs the caller indefinitely — the operation timeout never even starts its clock because the call never got a connection to run the operation over. |
| Operation timeout | Per §2's table | Without it: a dependency that *is* reachable but has gone slow (the more common failure mode per §1) hangs the caller for as long as the client's arbitrary default allows — often far longer than the connection-timeout window, since most clients default the operation bound loosely or not at all. |

A connection timeout of 1 s and an operation timeout of 30 ms (Redis, §2) are not a contradiction — they answer different questions ("can I even reach it" vs. "did it answer fast enough") and a caller needs both answered before it can distinguish "Redis is down" from "Redis is up but overloaded," which matters directly to `18-circuit-breaker.md`'s health signal.

---

## 4. Implementation in .NET

Two mechanisms cover every dependency in §2: **Polly's `Timeout` strategy** (via `Microsoft.Extensions.Resilience`, already the project's stated preference per `nfr-resilience.md` §4.2) for calls that go through an `HttpClient` or that benefit from a uniform resilience-pipeline shape, and **native client-level timeout configuration** (`CommandTimeout`, `StackExchange.Redis`'s `ConnectTimeout`/`SyncTimeout`, the Elasticsearch client's `RequestTimeout`, the Kafka producer's `request.timeout.ms`) for calls where the client already exposes a first-class timeout knob that maps directly onto the connection/operation split in §3. `CancellationToken` propagation ties both mechanisms together end to end, unchanged in spirit from `nfr-performance.md` §5's redirect-path threading of the token from the ASP.NET Core pipeline down to the data-access call.

### 4.1 Redis (native client timeout + `CancellationToken`)

```csharp
// Infrastructure/DependencyInjection.cs
var options = ConfigurationOptions.Parse(configuration["Redis:ConnectionString"]!);
options.ConnectTimeout = 1_000;   // connection timeout (§3) — pool/handshake acquisition
options.SyncTimeout = 30;         // operation timeout (§2) — bounds each GET/SET
options.AsyncTimeout = 30;

services.AddSingleton<IConnectionMultiplexer>(
    sp => ConnectionMultiplexer.Connect(options));
```

```csharp
public async Task<CachedRedirect?> GetAsync(string code, CancellationToken cancellationToken)
{
    var db = _multiplexer.GetDatabase();

    // StackExchange.Redis honors SyncTimeout/AsyncTimeout above; the token still
    // propagates so an aborted client request frees this call promptly too.
    var value = await db.StringGetAsync(BuildCacheKey(code)).WaitAsync(cancellationToken);
    return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<CachedRedirect>(value!);
}
```

### 4.2 Primary DB (EF Core `CommandTimeout`)

```csharp
// Infrastructure/DependencyInjection.cs
services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlServer(configuration.GetConnectionString("Primary"), sql =>
        sql.CommandTimeout(1))); // seconds; overridden per-call below where the §2 split differs
```

```csharp
// Redirect-path read — tighter than the write path's default above.
public async Task<ShortUrlRedirectTarget?> FindRedirectTargetAsync(
    string code, CancellationToken cancellationToken)
{
    _context.Database.SetCommandTimeout(TimeSpan.FromMilliseconds(100)); // §2
    return await _context.ShortUrls
        .AsNoTracking()
        .Where(s => s.Code == code)
        .Select(s => new ShortUrlRedirectTarget(s.OriginalUrl, s.ExpiresAtUtc))
        .FirstOrDefaultAsync(cancellationToken); // still threaded through, per nfr-performance.md §5
}
```

### 4.3 Moderation-check HTTP call (Polly `AddTimeout`, unchanged shape from v1)

This is `nfr-resilience.md` §4.2's pipeline, reconfirmed at v2 scale (§2), reproduced here only to show it fits the same `AddTimeout` primitive the rest of this document uses — not redesigned:

```csharp
services.AddHttpClient<IMaliciousDomainChecker, MaliciousDomainChecker>(client =>
    {
        client.BaseAddress = new Uri(configuration["ModerationCheck:BaseUrl"]!);
        client.Timeout = Timeout.InfiniteTimeSpan; // let the Polly pipeline own the clock, not HttpClient's own default
    })
    .AddResilienceHandler("moderation-check", builder =>
    {
        builder.AddTimeout(TimeSpan.FromSeconds(2));  // per-attempt operation timeout (§2)
        // Retry sits here in the real pipeline — see 15-retry.md / 16-exponential-backoff.md.
        // Not designed in this document.
        builder.AddTimeout(TimeSpan.FromSeconds(5));  // overall budget across attempts (§2)
    });
```

`client.Timeout = Timeout.InfiniteTimeSpan` is deliberate: when a Polly `AddTimeout` stage wraps the call, `HttpClient`'s own built-in timeout must not also be racing it with a different, uncoordinated value — exactly the "two timers, one clock" mistake §3 warns against. One mechanism owns the operation-timeout clock per call; the other stays out of the way.

### 4.4 The common thread: `CancellationToken` as the propagation backbone

Every example above accepts and threads a `CancellationToken`. Polly's `AddTimeout` and the native client timeouts above both work by internally deriving a linked `CancellationTokenSource` and cancelling it when the budget elapses — which only reaches the actual I/O if the underlying call is written to observe the token (as `FirstOrDefaultAsync(cancellationToken)` and `WaitAsync(cancellationToken)` do above). A timeout configured on a client that ignores the token it's given degrades to "eventually stops waiting for the response" without actually cancelling the in-flight work — still leaks the connection/thread §1 warns about. This is the same discipline `nfr-performance.md` §5 already requires end-to-end on the redirect path; this document extends it to be non-negotiable on every dependency, not just that one path.

---

## 5. What Happens When a Timeout Fires — the Handoff

A fired timeout must surface as a **specific, distinguishable failure**, not get folded into a generic exception, because everything downstream of this document depends on being able to tell "this call timed out" apart from "this call failed for some other reason":

- **Polly's `Timeout` strategy** throws `Polly.Timeout.TimeoutRejectedException` when its budget elapses — distinct from an `HttpRequestException` (connection refused, DNS failure) or a non-2xx response, and distinct from `OperationCanceledException` caused by the *caller's* cancellation (e.g., the end user's browser disconnecting) rather than the resilience pipeline's own clock.
- **Native client timeouts** (EF Core `CommandTimeout`, `StackExchange.Redis`'s `SyncTimeout`/`AsyncTimeout`) throw their own typed exceptions (`Microsoft.Data.SqlClient.SqlException` with a timeout-specific error number, `StackExchange.Redis.RedisTimeoutException`) rather than a generic `Exception` — the type itself is the signal.
- `nfr-resilience.md` §3.3's global exception-handling middleware already maps `TimeoutException` (and, by the same logic, needs its mapping extended to `TimeoutRejectedException` and `RedisTimeoutException` as they're introduced in v2) to a `503 Service Unavailable` with "transient; safe to retry" caller guidance — the pattern generalizes as-is, not a new mapping shape.

**This document stops at the point of a distinguishable failure being raised.** What happens next — whether the caller retries, how many times, with what backoff, whether a circuit trips open to stop trying altogether, or whether a bulkhead's isolated pool is what actually contained the blast radius while the timeout was the trigger — is designed in `15-retry.md`, `16-exponential-backoff.md`, `17-jitter.md`, and `18-circuit-breaker.md` respectively. The contract this document guarantees to those documents is narrow and specific: **every outbound call in this system fails within a known, bounded time, as a typed exception that unambiguously means "this was a timeout," never as an indefinite hang.** That contract is what makes retry-with-backoff, jitter, and circuit-breaking well-defined problems in the first place — none of those patterns can reason about "how many times has this failed and how fast" if "failed" can also silently mean "still running, three minutes later."

---

## 6. Summary of Decisions

| # | Decision | Traces to |
|---|---|---|
| 1 | Every outbound call (DB, Redis, Elasticsearch, broker, moderation-check) has an explicit operation timeout — no client-library default is left in place | §1; ANFR-04 |
| 2 | Redis redirect-path lookup: 30 ms operation timeout | §2; ANFR-05 (p95 < 50 ms) |
| 3 | Primary DB redirect-path (cache-miss) lookup: 100 ms command timeout | §2; ANFR-05/06 (p99 < 150 ms) |
| 4 | Primary DB create-path write: 500 ms command timeout | §2; `01-create-path-extreme-scalability.md` |
| 5 | Elasticsearch metadata/analytics query: 2 s request timeout | §2; `nfr-performance.md` §7 (non-latency-critical) |
| 6 | Broker publish: 5 s produce-request timeout | §2; `05-kafka-comaporison.md`, `20-outbox-pattern.md` |
| 7 | Moderation-check HTTP: 2 s per-attempt / 5 s overall — reconfirmed unchanged from v1 | §2; `nfr-resilience.md` §4.2 |
| 8 | Connection timeout and operation timeout are configured as two distinct bounds, not one | §3 |
| 9 | `HttpClient.Timeout` disabled (`InfiniteTimeSpan`) wherever Polly's `AddTimeout` already owns the clock, to avoid two uncoordinated timers on one call | §4.3 |
| 10 | `CancellationToken` propagation is mandatory end-to-end for every timeout to actually cancel in-flight I/O, not just stop waiting for it | §4.4 |
| 11 | Timeout failures surface as specific, typed exceptions (`TimeoutRejectedException`, `RedisTimeoutException`, DB timeout `SqlException`) mapped to `503`, distinguishable from other failure types | §5; `nfr-resilience.md` §3.3 |
| 12 | Retry, backoff, jitter, and circuit-breaker reactions to a fired timeout are explicitly out of scope here | §5; `15-retry.md`, `16-exponential-backoff.md`, `17-jitter.md`, `18-circuit-breaker.md` |
