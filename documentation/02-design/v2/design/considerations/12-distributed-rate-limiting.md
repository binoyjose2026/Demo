# Distributed Rate Limiting for a Horizontally-Scaled API

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/prompt@review-desig.md`.
**Builds on (does not replace):** `v1/design/nfr-security.md` Section 5 (rate limiting policy shape: per-caller limit + creation-volume ceiling, anonymous < authenticated, explicit rejection over silent throttling, numeric thresholds left as placeholders) and Section 10 (creation requires an authenticated user context).
**Traces to:** ANFR-09 (`requirement.app.non-functional.md`, "the URL-creation endpoint shall be protected against abusive/excessive request volume"); Q13, Q16 (`01-requirements/v1-requirements/agents/review@agent/review/02-answer.md` and cross-referenced in `nfr-security.md` Section 5).
**Related v2 documents (by filename, not duplicated here):** `07-redis-caching-and-invalidation.md` (the same Redis instance/cluster and `IDistributedCache`/`StackExchange.Redis` seam this document reuses for a different purpose), `01-create-path-extreme-scalability.md` (establishes the API runs as many horizontally-scaled instances behind a load balancer — the topology change this document responds to).

---

## 1. Why v1's Approach Breaks at This Scale

`nfr-security.md` Section 5.1 specified ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting` middleware with `RateLimitPartition.GetFixedWindowLimiter`, keyed by user identity or IP. That code sample is correct as written — but it was written against **v1's single-instance deployment**. The built-in partitioned limiters store their counters **in-process, in memory**. Nothing about the API surface changes at v2 scale; what changes is that `01-create-path-extreme-scalability.md` puts the API behind a load balancer fanning out to many horizontally-scaled instances, each running its own copy of that middleware with its own independent counter state.

**This is the core problem this document solves:** with N instances, each enforcing the same nominal limit (e.g., 10 requests/minute) independently and in ignorance of every other instance, a client that spreads its requests across all N instances gets up to **N× the intended limit**, not because the limit was misconfigured but because there is no shared notion of "how many requests has this caller made" anywhere in the system. A load balancer using round-robin or least-connections routing makes this trivial to hit *by accident*, not just by a deliberately evasive attacker — a caller retrying on transient errors will naturally get redistributed across instances. At 1M-5M creates/day spread across, say, 10-20 API instances for availability and throughput, an in-memory limiter is not a weaker version of rate limiting — it is close to no rate limiting at all for a caller determined to exceed it, and an inconsistent, load-balancer-routing-dependent limit even for callers who aren't.

The fix is not a different algorithm bolted onto the same storage — it is moving the counter itself out of process into a store every instance shares.

---

## 2. The Distributed Mechanism: Redis-Backed Counters

### 2.1 Why Redis

Consistent with `07-redis-caching-and-invalidation.md`, this design reuses **Redis** as the shared store — not because rate limiting and caching are the same problem, but because they have the same infrastructural requirement (a fast, shared, TTL-capable key-value store reachable from every API instance) and reusing the already-justified Redis deployment avoids introducing a second piece of distributed infrastructure for a second cross-cutting concern. Redis's atomic `INCR`/`EXPIRE` and Lua scripting (`EVAL`) make it possible to implement the read-check-increment sequence as a single atomic operation server-side, which is what makes it viable as a rate-limit counter store under concurrent access from many instances — a naive "GET counter, check it, SET counter+1" from the API side would race under concurrent requests from different instances.

### 2.2 Algorithm: Sliding-Window Counter (not pure token bucket)

Two realistic candidates:

| Algorithm | Behavior | Cost |
|---|---|---|
| **Token bucket** | Each caller has a bucket that refills at a fixed rate and drains per request; bursts up to the bucket size are allowed even after idle periods. | Cheap — one counter + one timestamp per key, refill computed on read. |
| **Sliding-window counter** | Weights the previous fixed window's count by how much of it overlaps the current window, approximating a true sliding window without storing a timestamp per request. | Slightly more computation per check (two counters + a weighted-average calculation), still O(1) in Redis via a small Lua script. |

**Decision: sliding-window counter**, not token bucket.

**Justification for this system specifically:**

- **Fixed-window/token-bucket boundary bursting is the exact failure mode this document exists to close.** A pure fixed-window counter allows a caller to send the full limit in the last second of one window and the full limit again in the first second of the next — a 2× burst at the boundary. Token bucket has a related property by design: after an idle period, the bucket is full and the caller can legitimately burst up to the bucket capacity in one moment. That is a reasonable trade for many APIs, but the whole point of ANFR-09 is bounding *worst-case creation volume* to protect downstream capacity (DB writes, domain-reputation checks in `nfr-security.md` Section 4) — a permitted burst is exactly the abuse pattern (T5, scripted bulk creation) the v1 threat model calls out. Sliding-window counter keeps the effective rate close to the configured limit at all times, including across window boundaries, which is a materially tighter guarantee against the T5 threat than token bucket's intentional burst allowance.
- **The redirect endpoint's traffic profile (10M-100M/day) does not need burst tolerance either** — it needs a cheap, approximately-accurate check that doesn't add meaningful latency to ANFR-05's low-latency redirect path. Sliding-window counter's O(1) Redis cost (two `GET`s and a weighted sum) is close enough to token bucket's cost that the precision gain isn't paid for with a meaningfully more expensive operation, which is why this document picks one algorithm for both endpoints rather than splitting by endpoint.
- **Token bucket is the better choice when legitimate burst usage is a product goal** (e.g., a batch-import feature that should be allowed to submit 50 links at once after being idle). That is not a stated requirement here — Q13/Q16 describe steady-state abuse protection, not burst accommodation — so token bucket's main advantage doesn't apply, and its main disadvantage (boundary/idle bursting) does.

If a future version needs burst tolerance for a specific, product-approved use case (e.g., a bulk-import API), that is a new, narrower policy layered on top of this one for that specific route, not a reason to change the default algorithm.

---

## 3. What Gets Rate-Limited, and at What Granularity

Consistent with `nfr-security.md` Section 5.1's two independent controls (per-caller rate limit, creation-volume ceiling) and Section 10 (creation requires an authenticated user context) — this document fixes the **key scheme** those controls partition by, now that the store is shared:

| Endpoint | Partition key | Rationale |
|---|---|---|
| **Create** (authenticated only, per `nfr-security.md` §10) | `ratelimit:create:user:{userId}` | Every creation request carries an authenticated identity by design (v1 §10.1) — there is no anonymous-create case to key by IP. `userId` is the stable identity already used for `CreatedBy` and the ownership model, so no new identity concept is introduced. |
| **Create — creation-volume ceiling** | `ratelimit:createvolume:user:{userId}:{yyyyMMdd}` | A coarser, longer-window counter distinct from the per-minute rate limit (`nfr-security.md` §5.1's second control) — a daily key with a 24h TTL, separate from the short-window rate-limit key so the two controls don't share state or interfere with each other's window resets. |
| **Fetch/redirect** (ungated by identity per v1 §10.1 — "anyone with the short URL can use it") | `ratelimit:fetch:ip:{clientIp}` | No authenticated identity exists on this path by design, so IP is the only available caller-identifying key, same fallback v1 already named (`nfr-security.md` §5.1's partition-key table). Applied as a much higher ceiling than create, and only to guard against redirect-endpoint abuse (e.g., scraping/enumeration, T4) — not to throttle normal traffic, since ANFR-01/ANFR-05 require the redirect path to stay low-latency and highly available. Consistent with v1's framing that redirect is "intentionally excluded from aggressive limiting." |

The `ratelimit:` prefix and colon-delimited segments follow the same key-naming convention as `07-redis-caching-and-invalidation.md`'s `shorturl:v1:code:{shortCode}` scheme, kept in a separate top-level namespace (`ratelimit:*` vs. `shorturl:*`) so the two concerns never collide on a key and can be reasoned about, monitored, and evicted independently even though they share the same Redis deployment.

IP-keying has the known limitation that NAT/CGNAT or corporate proxies put many distinct users behind one IP — this is an accepted trade-off for the anonymous fetch path (there is no better caller-identifying signal available without requiring auth on redirect, which v1 explicitly decided against, §10.1), and is why the fetch ceiling is set high relative to normal single-user traffic rather than tuned tightly.

---

## 4. Implementation Approach in .NET

**Decision: ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting` middleware, with a custom Redis-backed partition/counter store**, rather than adopting a third-party library like `RedisRateLimiting`.

**Justification:**

- `nfr-security.md` Section 5.1 already committed to `Microsoft.AspNetCore.RateLimiting` as the middleware layer for v1. Keeping that dependency and swapping only the counter *storage* — the same "swap the backing store behind an unchanged abstraction" move `07-redis-caching-and-invalidation.md` makes for `IDistributedCache`/`IMemoryCache` — is a smaller, lower-risk change than replacing the whole middleware pipeline with a different package's request pipeline integration. The endpoint-level `[EnableRateLimiting("policy-name")]` attributes, policy names, and `Program.cs` registration shape from v1 stay unchanged; only what runs inside the partition's permit-check logic changes.
- The built-in middleware supports a **custom `PartitionedRateLimiter`**, which is the extension point used here: instead of `RateLimitPartition.GetFixedWindowLimiter` (in-memory), a custom limiter factory calls into Redis (via a small Lua script for the atomic sliding-window check) and returns a `RateLimitLease` based on the result.
- `RedisRateLimiting` (the third-party library) is a reasonable alternative and implements a similar idea out of the box — it is called out here as the deliberate alternative considered, not ignored. It is not selected because: (a) it is a smaller, less-widely-audited community package for a security-relevant control, versus Microsoft's first-party middleware with the store as the only custom piece; (b) the sliding-window Lua script this system needs is small enough (~20 lines) that owning it directly avoids taking on a whole external library's API surface, versioning, and .NET 9 compatibility lifecycle for a single algorithm; (c) it keeps the "policy shape lives in `Microsoft.AspNetCore.RateLimiting`, storage is swappable" abstraction consistent with how `07-redis-caching-and-invalidation.md` treats `IDistributedCache`. A team with less appetite for owning a Lua script could reasonably choose `RedisRateLimiting` instead — that is a legitimate alternative, not a wrong one, just not the one this document picks.

```csharp
// Program.cs — Redis-backed sliding-window partition, replacing the v1
// in-memory GetFixedWindowLimiter shown in nfr-security.md Section 5.1.
// Numeric limits remain placeholders per nfr-security.md Section 5.2 —
// not finalized by this document either.

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        }
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            title = "Rate limit exceeded",
            detail = "Too many creation requests for this account. Retry after the time in Retry-After.",
        }, cancellationToken);
    };

    options.AddPolicy("short-url-creation", httpContext =>
        new RedisSlidingWindowRateLimiter(
            partitionKey: $"ratelimit:create:user:{httpContext.User.GetUserId()}",
            permitLimit: 10,                 // PLACEHOLDER — see nfr-security.md 5.2
            window: TimeSpan.FromMinutes(1),
            redis: httpContext.RequestServices.GetRequiredService<IConnectionMultiplexer>()));
});

// RedisSlidingWindowRateLimiter : RateLimiter — wraps a Lua EVAL that
// atomically reads/weights the previous+current window counters and
// increments, mirroring the GetFixedWindowLimiter call shape from
// nfr-security.md 5.1 but backed by IConnectionMultiplexer instead of
// in-process state. Returns a RateLimitLease carrying the RetryAfter
// metadata used by OnRejected above.
```

---

## 5. Behavior When the Limit Is Exceeded

Unchanged from `nfr-security.md` Section 5.3 / Q16 — this document does not re-litigate the decision, only confirms it still holds under the distributed implementation: exceeding the limit returns **HTTP 429 with a `ProblemDetails` body and a `Retry-After` header**, computed from how much of the current sliding window remains rather than a fixed guess. No request is queued or silently throttled (`QueueLimit = 0` in v1's terms carries forward — the custom limiter never queues, it grants or rejects). The `OnRejected` handler in Section 4's example is where this is enforced at the distributed-limiter layer, replacing v1's default `QueueLimit = 0` fixed-window behavior with the equivalent explicit-rejection behavior for the custom limiter.

---

## 6. Failure Mode: Redis Unavailable

**Decision: fail open on the fetch/redirect path, fail closed on the create path.** This system does not have one uniform answer — the two endpoints have different risk profiles, and v1 already established why they are treated asymmetrically.

| Path | Failure mode | Why |
|---|---|---|
| **Create** | **Fail closed** — reject with `503 Service Unavailable` (distinct from `429`, so callers and monitoring can tell "rate-limited" apart from "rate limiter is down") rather than silently allowing unlimited creation. | Creation is the endpoint ANFR-09 exists to protect, and it is already gated behind an authenticated context and a domain-reputation check (`nfr-security.md` Section 4) that itself may depend on outbound calls — a system already tolerant of create-path friction during degraded conditions. Failing open here means a Redis outage becomes a free pass for exactly the scripted-bulk-creation abuse (T5) this document was written to close, at the worst possible moment (Redis outages correlate with general infrastructure stress, when abuse attempts are also more likely to be probing for weaknesses). Create traffic is also the lower-volume side of this system (1M-5M/day vs. 10M-100M/day fetch) and per v1 is not held to the strict low-latency/high-availability bar that redirect is (ANFR-01/ANFR-05 name redirect specifically) — so refusing creates during a Redis outage is a bounded, acceptable-availability cost, not a violation of a stated NFR. |
| **Fetch/redirect** | **Fail open** — serve the redirect without a rate-limit check when Redis is unreachable. | ANFR-01 and ANFR-05 name the redirect path specifically as needing to stay highly available and low-latency; `nfr-security.md` Section 5.1 already excludes it from aggressive limiting for that reason even in the happy path. The redirect path's rate limit exists to guard against enumeration/scraping abuse (T4), not to protect a scarce write resource — letting it run unmetered for the (expected to be short) duration of a Redis outage is a bounded, recoverable exposure. Refusing redirects because the *rate limiter* is down would fail the product's core promise (a link that stops resolving) for a reason unrelated to the link's own validity, which is a worse outcome than temporarily under-enforcing an abuse control. This mirrors `07-redis-caching-and-invalidation.md`'s own cache-aside failure handling: a Redis miss or outage falls through to the source of truth rather than failing the request, and rate limiting on this path adopts the same "Redis is an accelerator/guard, not a dependency" posture.

**Operational note:** both cases require the API to detect a Redis failure cheaply (e.g., a short circuit-breaker/timeout around the Lua `EVAL` call, not a slow per-request timeout) so a Redis outage degrades to one of the two behaviors above quickly rather than adding latency to every request while it fails. Correlation-ID logging (`nfr-security.md` Section 9) should tag requests served under either degraded mode so the failure window is visible in incident review, distinct from ordinary 429s.
