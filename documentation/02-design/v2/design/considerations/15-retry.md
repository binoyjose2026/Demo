# Retry Pattern for Extreme-Scale Dependencies

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/agent-prompt.md`.
**Builds on (does not replace):** `v1/design/nfr-resilience.md` Section 4 (the original Polly-based retry+timeout wrapper around the moderation-check call) and Section 2.3 (the documented v1 decision *not* to retry writes against the local SQLite file).
**Traces to:** ANFR-04 (graceful degradation on backend failure), ANFR-05/ANFR-06 (low-latency, high-volume redirect throughput).
**Sibling documents (by filename, not duplicated here):** `14-timeout.md` (per-attempt and overall timeout budgets — retries in this document consume, not define, those budgets), `16-exponential-backoff.md` (the delay math between attempts), `17-jitter.md` (randomizing that delay to avoid synchronized retry storms), `18-circuit-breaker.md` (what stops retries once a dependency is *known* to be down, rather than retrying attempt-by-attempt), `19-bulkhead.md` (isolating retry-consumed connections/threads per dependency so one dependency's retries can't starve another's), `11-idempotency-keys.md` (what makes retrying a create/write request safe in the first place — referenced in Section 3, not repeated here).

---

## 1. Purpose & Scope

`nfr-resilience.md` Section 4 already established retry as the correct tool for exactly one dependency in v1: the outbound moderation-check HTTP call. It deliberately declined to retry the local SQLite write path (Section 2.3) because a local single-writer lock timeout and a genuine network failure are different failure modes with different retry economics.

At v2 scale, the dependency graph is no longer "one embedded database plus one HTTP call." It is:

- **Primary database** (per `03-elasticsearch-vs-sql-server.md` / `05-kafka-comparison.md`'s surrounding architecture — a server-based RDBMS, not SQLite)
- **Redis cache** (`07-redis-caching-and-invalidation.md`)
- **Elasticsearch** (`03-elasticsearch-vs-sql-server.md`, `04-elasticsearch-vs-mongodb.md`)
- **Message broker** (`05-kafka-comparison.md`, `20-outbox-pattern.md`)
- **External moderation-check HTTP call** (carried forward from v1)

Every one of these is now reached over a network, from multiple horizontally-scaled instances, at 100M-fetches/day-class volume. This document defines *which* failures against *which* dependency are safe to retry, how many times, and how that decision composes with idempotency, timeout, backoff, jitter, circuit breaking, and bulkheads — each of which is that sibling document's job, not this one's.

This document does **not** design backoff delay math (`16-exponential-backoff.md`), randomization (`17-jitter.md`), per-attempt/overall time budgets (`14-timeout.md`), failure-rate tripwires (`18-circuit-breaker.md`), or resource isolation (`19-bulkhead.md`). It answers one question: *given that an attempt failed, should there be another attempt at all, and under what bound?*

---

## 2. Retryable vs. Non-Retryable Failures

The single governing rule, carried forward unchanged from `nfr-resilience.md` Section 4.2: **retry only failures that are plausibly transient and where a second attempt has a realistic chance of a different outcome.** Retrying a failure that is guaranteed to repeat wastes latency, wastes capacity, and — at 100M/day scale — turns a single bad request into amplified load against an already-struggling dependency.

### 2.1 Safe to retry (transient)

| Failure | Why it's transient |
|---|---|
| Network-level errors (connection reset, DNS blip, socket timeout) | Caused by the network path or a momentarily unavailable peer, not by the request's content. A retry a few hundred milliseconds later frequently lands on a healthy path/instance. |
| `5xx` / `503 Service Unavailable` from the DB, Elasticsearch, the broker, or the moderation-check endpoint | Server-side signal that *this instance* of the dependency is overloaded or restarting — not that the request itself is invalid. Load balancers/replica sets typically route the retry elsewhere. |
| Timeouts surfaced by the per-attempt timeout policy (`14-timeout.md`) | A timeout does not tell you the operation failed — only that it didn't finish in budget. For a read, retrying is safe by default. For a write, retrying is only safe under the idempotency rule in Section 3. |
| Connection-pool exhaustion / "no connection available" | Transient capacity pressure, not a permanent condition — assuming a bulkhead (`19-bulkhead.md`) is in place so the pressure is dependency-scoped and has a chance to clear. |

### 2.2 Not safe to retry (deterministic)

| Failure | Why retrying is pointless or harmful |
|---|---|
| `4xx` validation errors (malformed URL, missing required field) | The request is wrong. The dependency will reject it identically every time; retrying only delays telling the caller the truth. |
| Business-rule conflicts — e.g., duplicate custom alias (`409 Conflict`, per `nfr-resilience.md` Section 3.3) | This is a *correct*, deterministic answer, not a transient failure. Retrying repeats a guaranteed rejection and, worse, can mask the real signal the caller needs (pick a different alias). |
| `401`/`403` authorization failures | Retrying with the same credentials produces the same denial. |
| The moderation-check endpoint's definitive "this domain is flagged" result | A real business result, not an error at all — carried forward verbatim from `nfr-resilience.md` Section 4.2's rule; never retried regardless of how it's transported (HTTP 200 with a flagged payload, not a 5xx). |
| Elasticsearch query-parsing errors / mapping conflicts | Deterministic response to the query shape sent — the tenth identical retry parses exactly as badly as the first. |

The dividing line is **"would an identical retry plausibly get a different answer?"** Anything the caller (or the create-link business logic) controls the content of — validation, uniqueness, authorization, a definitive moderation verdict — answers no. Anything about the state of the network or the dependency process at that instant answers maybe, which is where retry belongs.

---

## 3. Retry Budget: Bounding Attempts

### 3.1 Why unbounded (or generous) retry is dangerous at this scale

A single client retrying forever is a nuisance. A **fleet** of horizontally-scaled API instances, each independently retrying against a struggling dependency, is a self-inflicted denial-of-service:

- **Retry amplification.** At 100M fetches/day (~1,150 req/s average, materially higher at peak), even a modest per-request retry multiplier compounds fast. If Redis degrades and every one of N instances retries every miss 5 times, the *retry* traffic alone can exceed the original load that degraded Redis in the first place — the classic retry storm that turns a partial outage into a total one.
- **Retries hide the true failure signal from a circuit breaker.** If retries are unbounded, per-request latency degrades gracefully into "very slow" rather than surfacing a clean failure — which delays the circuit breaker (`18-circuit-breaker.md`) from tripping and cutting load to the struggling dependency. A tight retry budget is what lets a circuit breaker do its job promptly.
- **Retries consume the same finite resources (connections, threads) the bulkhead is trying to protect.** An unbounded retry loop against one dependency can, without a bulkhead (`19-bulkhead.md`), starve capacity that healthy calls to *other* dependencies need.
- **Tail latency compounds.** Each retry adds its own timeout budget (`14-timeout.md`) to the request's total latency. A generous retry count on the redirect hot path directly threatens ANFR-05's low-latency guarantee — a caller is often better served by a fast, honest failure than a slow eventual success.

### 3.2 Recommendation: a small, fixed maximum per call

**Maximum 2–3 retry attempts (3–4 total attempts including the original) per logical operation, dependency-dependent (Section 4).** This is deliberately conservative:

- It is large enough to absorb the overwhelmingly common transient case (one bad packet, one momentarily busy node in a replica set).
- It is small enough that the *added* latency and *added* load from retrying stay bounded and predictable — with the exponential-backoff delay curve and jitter spread from `16-exponential-backoff.md`/`17-jitter.md` applied between attempts, not designed here.
- It leaves room for the circuit breaker (`18-circuit-breaker.md`) to own the "this dependency has been down for a while" case. Retry answers *"is this one call transiently unlucky?"*; circuit breaker answers *"has this dependency stopped being worth calling at all?"* Conflating the two by cranking the retry count up is a design smell — it makes retry try to do a circuit breaker's job with none of its state or hysteresis.

The exact number, and the delay between attempts, is a tuning question that belongs to `16-exponential-backoff.md` and `17-jitter.md` — this document fixes only the **ceiling**, because the ceiling is what bounds worst-case amplification and worst-case latency, independent of how the delay between attempts is shaped.

---

## 4. Idempotency: The Precondition for Retrying Writes

Retrying a **read** (DB read, cache read, Elasticsearch query) is safe by default — a read has no side effect to duplicate, so re-issuing it after a transient failure changes nothing about correctness, only about latency and load (Section 3).

Retrying a **write** (create link, publish to the broker, an Elasticsearch index write) is a different question entirely: if the first attempt actually succeeded and only the *response* was lost to a timeout, a naive retry re-executes the write a second time. Whether that is safe depends entirely on whether the operation is idempotent — see `11-idempotency-keys.md` for the mechanism (client-supplied idempotency key, server-side dedup) that makes a retried create-link request collapse onto the original result instead of producing a duplicate.

Two things carried forward from v1 are still true and still matter here:

- `nfr-resilience.md` Section 5 already establishes that v1's create-link path is *safe-by-construction* for retries in the custom-alias case (uniqueness constraint turns a retry into a clean `409`, not a duplicate) but *not* fully idempotent for the system-generated-code case (a retry can produce a harmless but real duplicate mapping). At v2 scale, with a fleet of instances and a message broker fanning out downstream effects (indexing into Elasticsearch, publishing analytics events per `20-outbox-pattern.md`), an un-deduplicated retried write no longer just costs one wasted row — it can also cause a duplicate downstream event. This is precisely the gap `11-idempotency-keys.md` exists to close for v2; retry policy in this document assumes that mechanism is in place for any retried write against the DB or the broker.
- The moderation-check call remains read-like from a retry-safety point of view (`nfr-resilience.md` Section 4.2) — it is a query against a reputation service, not a write, so it needs no idempotency key of its own to retry safely.

**Rule of thumb applied per dependency below:** retry a write only if (a) the operation is naturally idempotent (e.g., an upsert keyed by short code), or (b) it is guarded by an idempotency key per `11-idempotency-keys.md`. Otherwise, a failed write surfaces as an error to the caller rather than being silently retried — consistent with `nfr-resilience.md` Section 2.2's "fail loud, never fail silently" rule, which this document does not relax.

---

## 5. Per-Dependency Retry Policy

Retry is not one policy — the cost, blast radius, and idempotency story differ per dependency. The table below is the per-dependency default; it composes with the timeout budget (`14-timeout.md`) and backoff/jitter shaping (`16-exponential-backoff.md`, `17-jitter.md`) owned elsewhere.

| Dependency | Operation | Retry? | Rationale |
|---|---|---|---|
| **Primary DB** | Read (e.g., metadata lookup, `GET` fallback on cache miss) | Yes, up to the budget (Section 3.2) | Cheap, side-effect-free, and a replica/connection-pool blip is exactly the transient case retry exists for. |
| **Primary DB** | Write (create link, deactivate) | Yes, but only under idempotency guard (Section 4) | Without an idempotency key, retrying a write risks a duplicate row/event; with one, it's safe and should use the same small budget. |
| **Redis cache** | Read (cache-aside lookup) | Yes, 1–2 attempts, short-lived | A cache miss/timeout is cheap to retry once, but a cache is *supposed* to be fast — if it's not responding, falling through to the DB read (the existing cache-aside miss path, `07-redis-caching-and-invalidation.md`) is usually a better use of the latency budget than retrying the cache itself repeatedly. |
| **Redis cache** | Write (cache population, invalidation) | Optional, low priority | The cache is not the source of truth — a lost cache write just means the next read is a miss that repopulates it. Retrying is a latency/hit-rate optimization, not a correctness requirement; skipping the retry and letting the value expire/repopulate naturally is an acceptable, simpler default. |
| **Elasticsearch** | Read (search query) | Yes, up to the budget | Read-only, side-effect-free; same transient-node/shard-unavailable case as a DB read. |
| **Elasticsearch** | Write (index a document) | Yes, if the write is naturally idempotent | Elasticsearch document writes keyed by a stable ID (e.g., short code) are upserts by nature — re-indexing the same document with the same ID is safe without a separate idempotency-key mechanism, unlike a relational `INSERT`. |
| **Message broker** | Publish | Yes, only with idempotency guard or a broker-native dedup key | A duplicate publish fans out to every consumer downstream (indexing, analytics) — see `20-outbox-pattern.md` for how the outbox pattern already bounds this by making publish itself an at-least-once, consumer-deduplicated operation. Retry policy here should lean on that existing dedup, not invent a second one. |
| **Message broker** | Consume/ack | Governed by the broker's own redelivery semantics, not this policy | Broker consumer retry (redelivery on failed ack) is a property of the consumer/broker configuration described in `05-kafka-comparison.md`, not a Polly-style client retry; out of scope here to avoid duplicating that document. |
| **External moderation-check HTTP call** | Query | Yes, small budget, transient conditions only (per `nfr-resilience.md` Section 4.2, unchanged) | Read-like and cheap to retry — but each retry costs a real external API call, which may be rate-limited or billed per-call by the provider. Unlike an internal DB/cache retry, cost here is a hard external constraint on top of the latency cost, which is why the v1 budget (2 retries) was already deliberately conservative and stays conservative at v2 scale rather than growing with traffic. |

The recurring pattern: **reads default to "retry freely within the small budget"; writes default to "retry only when idempotent"; anything with an external cost per call (the moderation-check) or a duplication blast radius (the broker) gets the most conservative treatment of all.**

---

## 6. Implementation in .NET: Polly via `Microsoft.Extensions.Resilience`

Consistent with `nfr-resilience.md` Section 4.2's recommendation, v2 continues to use the standard `Microsoft.Extensions.Http.Resilience` / Polly resilience pipeline rather than a hand-rolled retry loop, now applied consistently across every outbound dependency client (DB access via a resilient `DbCommand` interceptor or Polly-wrapped repository call, `HttpClient` for the moderation check, `StackExchange.Redis`/Elasticsearch clients wrapped the same way).

The example below shows the shape for the moderation-check `HttpClient` — a **retry strategy composed with a timeout strategy** in a single pipeline, exactly the composition this document and `14-timeout.md` both rely on: retry decides *whether* to try again, timeout (defined in its own document) bounds *how long* each attempt and the whole pipeline may take.

```csharp
// Infrastructure/DependencyInjection.cs
services.AddHttpClient<IMaliciousUrlChecker, ExternalMaliciousUrlChecker>(client =>
    {
        client.BaseAddress = new Uri(configuration["ModerationCheck:BaseUrl"]!);
    })
    .AddResilienceHandler("moderation-check", builder =>
    {
        // Per-attempt timeout: owned by 14-timeout.md, only referenced here.
        builder.AddTimeout(TimeSpan.FromSeconds(2));

        // Retry strategy: this document's concern. Bounded attempts, and
        // ShouldHandle enforces the retryable/non-retryable rule from Section 2 —
        // never retries a 4xx or a definitive "flagged" business result.
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,               // ceiling per Section 3.2
            BackoffType = DelayBackoffType.Exponential,  // shape defined in 16-exponential-backoff.md
            UseJitter = true,                    // spread defined in 17-jitter.md
            Delay = TimeSpan.FromMilliseconds(200),
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is not null ||               // network/transport failure
                args.Outcome.Result?.StatusCode is >= HttpStatusCode.InternalServerError), // 5xx only, never 4xx
        });

        // Overall pipeline timeout: also owned by 14-timeout.md; bounds total
        // time across all attempts so the retry budget can't itself become unbounded.
        builder.AddTimeout(TimeSpan.FromSeconds(5));
    });
```

- `MaxRetryAttempts` is the direct implementation of the Section 3.2 budget — a hard ceiling, not a suggestion.
- `ShouldHandle` is the direct implementation of Section 2's retryable/non-retryable table: it inspects the outcome and only classifies transport failures and `5xx` as retryable, so a `409` (duplicate alias) or `4xx` never enters the retry loop.
- `BackoffType`/`UseJitter`/`Delay` are present because Polly requires *some* value to construct the pipeline, but their tuning is explicitly out of scope here — see `16-exponential-backoff.md` and `17-jitter.md` for the reasoning behind the specific curve and spread.
- The same `AddRetry` + `ShouldHandle` shape applies to the DB, Redis, and Elasticsearch clients, with `ShouldHandle` swapped for the exception/status types each client surfaces (e.g., `SqlException` with a transient error number, `RedisConnectionException`, Elasticsearch's `Elastic.Transport` transient-response codes) and gated by the idempotency check from Section 4 for any write path.

---

## 7. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| Retryable failures | Transient only: network errors, `5xx`, timeouts from `14-timeout.md` | ANFR-04 |
| Non-retryable failures | `4xx`, business-rule conflicts (duplicate alias), definitive moderation verdicts | ANFR-04, `nfr-resilience.md` Section 4.2 |
| Retry budget | 2–3 retries (3–4 total attempts) per call, dependency-dependent | ANFR-05, ANFR-06 (bounds tail latency and amplification) |
| Idempotency for retried writes | Required; see `11-idempotency-keys.md`. Reads retry freely; writes retry only when idempotent | ANFR-03, ANFR-04, `nfr-resilience.md` Section 5 |
| Per-dependency policy | Reads liberal, writes idempotency-gated, moderation-check cost-conservative, broker publish leans on outbox dedup | Section 5 |
| Implementation | `Microsoft.Extensions.Http.Resilience` (Polly) `AddRetry` composed with `AddTimeout` in one pipeline, applied uniformly across DB/Redis/Elasticsearch/broker/HTTP clients | `nfr-resilience.md` Section 4.2 |
| Backoff, jitter, circuit breaking, bulkheads | Explicitly out of scope here — see sibling documents | `16-exponential-backoff.md`, `17-jitter.md`, `18-circuit-breaker.md`, `19-bulkhead.md` |
