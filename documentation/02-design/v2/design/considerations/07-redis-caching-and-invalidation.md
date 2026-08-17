# Redis-Based Distributed Caching for Frequently-Accessed Short Codes

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/prompt@review-desig.md`.
**Builds on (does not replace):** `v1/design/nfr-scalability.md` Section 2 (the `IMemoryCache`-backed `CachingShortUrlRepository` Decorator) and `v1/design/fn-fetch.md` Section 6 (the immutability guarantee that makes aggressive caching *safe*).
**Traces to:** AF-02 (redirect flow), AF-06 (defined not-found/expired response), AF-07 (deactivation), ANFR-02 (a short code shall consistently resolve to the same original URL for the lifetime of the mapping), ANFR-05 (low-latency redirect), ANFR-06 (high-volume read throughput).
**Related v2 documents (by filename, not duplicated here):** `05-kafka-comaporison.md` (event-transport comparison), the Outbox pattern consideration document, and the create-path horizontal-scalability overview referenced in the review prompt.

---

## 1. Why Distributed Caching Is Now Required

`v1/design/nfr-scalability.md` Section 2 already made the case for caching the hot redirect lookup in front of `IShortUrlRepository`, using `IMemoryCache` as a Decorator (`CachingShortUrlRepository`). That document was explicit that the choice of `IMemoryCache` over a distributed cache was a deliberate scope decision **for a single-instance deployment shape**, and pre-authorized the exact escalation this document now makes (Section 4 there: *"If stronger cross-instance cache consistency is ever required, the swap is `IMemoryCache` → a distributed cache (e.g., Redis) behind the same `IShortUrlRepository`/decorator abstraction"*).

At v2 scale, that trigger condition is met:

- **Traffic:** 10M fetches/day today, projected to 100M/day in 5 years — squarely the traffic ANFR-05/ANFR-06 optimize for, and an order of magnitude beyond what a single process should absorb.
- **Topology:** the create-path scalability overview establishes that the API now runs as **multiple horizontally-scaled instances** behind a load balancer, not the single instance v1 assumed.

Combining those two facts breaks two guarantees the v1 design explicitly flagged as per-instance limitations (`nfr-scalability.md` Section 4):

| v1 `IMemoryCache` limitation (already documented) | Why it now matters at v2 scale |
|---|---|
| **Hit rate is per-instance.** A code "hot" against instance A is a cold miss on instance B until it warms independently. | With N instances behind a load balancer and no session affinity, the *effective* cache hit rate degrades roughly by a factor of N — every instance re-learns the same hot set independently, multiplying database load exactly where ANFR-05/ANFR-06 need it minimized. |
| **Invalidation only clears the entry on the instance that processed the write.** Other instances keep serving stale data until their own TTL lapses. | AF-07 (deactivation) requires that once a link is deactivated, redirects stop working. With per-instance caches, whichever instances didn't handle the delete keep serving a stale "resolved" result for up to the full TTL — a correctness gap that gets worse as instance count grows, not better. |

A **shared Redis cache** solves both: one hit anywhere warms the cache for every instance, and invalidation (Section 5) is a single event that reaches all instances through the shared store rather than N independent local caches. This is a **like-for-like upgrade of the same caching layer** — the `IShortUrlRepository` interface, the Decorator placement, and the cache-aside read pattern from `nfr-scalability.md` Section 2.1/2.4 are unchanged. Only the backing store swaps: `IMemoryCache` → `IDistributedCache`/`StackExchange.Redis`, at the same DI-registration seam v1 already called out.

```
Controller → IShortUrlService → IShortUrlRepository (interface, unchanged)
                                        ▲
                                        │ implements
                        CachingShortUrlRepository (Decorator, unchanged shape)
                                        │ wraps
                                ShortUrlRepository (DB-backed)
                                        │
                        [ v1: IMemoryCache (per-instance) ]
                        [ v2: Redis (shared, cluster-wide) ]  ← this document
```

---

## 2. Cache Design

### 2.1 What is cached

The same minimal, redirect-relevant projection v1 already defined (`nfr-scalability.md` Section 2.2, `fn-fetch.md` Section 5) — enough to serve a redirect decision (AF-02) without a database round-trip, and nothing more:

| Field | Purpose |
|---|---|
| `OriginalUrl` | The redirect target — immutable per `fn-fetch.md` Section 6 / ANFR-02, which is what makes caching this value safe in the first place (no cache-invalidation-on-update problem, because there is no update). |
| `ExpiresAtUtc` (nullable) | Lets the resolver apply the same expiry check it applies today (`fn-fetch.md` Section 4/7.1) without a DB read. |
| `IsActive` (derived from `IsDeleted`) | Lets the resolver short-circuit deactivated links (AF-07) from cache alone. |

No analytics aggregates, owner details, or metadata-endpoint fields are cached here — that mirrors `fn-fetch.md` Section 5's rule that the hot path's query stays as cheap as possible, and keeps the metadata endpoint (AF-05, which must show accurate lifecycle status) on its own uncached, `IgnoreQueryFilters()` read path, unchanged from v1.

### 2.2 Key naming scheme

```
shorturl:v1:code:{shortCode}
```

- Keeps the `shorturl:code:{code}` convention from `nfr-scalability.md` Section 2.1's `BuildCacheKey`, with two additions: a stable namespace prefix (`shorturl:`) so this key space can share a Redis instance/cluster with other caches without collision, and a schema version segment (`v1:`) so the cached-value shape can change in a future release without needing a flush — bump to `v2:` and old keys simply age out via TTL.
- `{shortCode}` is used verbatim (already URL-safe, fixed small alphabet) — no hashing needed; this keeps keys human-inspectable during operations (`redis-cli GET shorturl:v1:code:abc123`).

### 2.3 Serialization format

**JSON**, via `System.Text.Json`, stored as the Redis string value:

```json
{ "u": "https://example.com/some/long/path", "e": "2026-12-31T00:00:00Z", "a": true }
```

- Short property names (`u`/`e`/`a`) trade a small readability cost for a meaningfully smaller payload at 100M-fetches/day scale, where network and memory footprint compound across every cache read.
- JSON (not a binary format like MessagePack/Protobuf) is chosen deliberately over raw performance: the payload is tiny (a URL string, a timestamp, a bool), so the serialization cost is not the bottleneck: operational simplicity (values are human-readable with `redis-cli`, no schema-registry dependency) matters more at this size. If payload size or CPU ever becomes the binding constraint, this is a drop-in swap behind the same Decorator, not a redesign.
- `StackExchange.Redis`'s `IDatabase.StringGetAsync`/`StringSetAsync` are the transport; `IDistributedCache` (ASP.NET Core's abstraction) is a viable alternative wrapper but `StackExchange.Redis` directly is preferred here so the eviction/TTL controls in Section 3 map onto native Redis semantics rather than an abstraction that hides them.

### 2.4 TTL strategy

Per `fn-fetch.md` Section 6, `OriginalUrl` is immutable once created — so the *value*, once cached, is never stale. The staleness question is entirely about the two *lifecycle* signals (`ExpiresAtUtc`, `IsActive`), which are data conditions/write events, not properties of the URL itself.

- **TTL: 5 minutes** — the same figure v1's `CacheDuration` used for `IMemoryCache` (`nfr-scalability.md` Section 2.2), carried forward deliberately rather than re-derived, so the accepted staleness window doesn't silently change as part of an infrastructure swap.
- This is a **backstop**, not the primary invalidation mechanism at v2 scale — Section 5 introduces an active invalidation path that handles the common case (deactivation, deletion, expiry-edit) immediately. The TTL exists purely to bound the worst case if that active path is ever missed (message lost, subscriber down, etc.), consistent with `nfr-scalability.md`'s own framing: *"a cached entry never drifts indefinitely out of sync... bounding the staleness window even if an invalidation is ever missed."*
- **Why 5 minutes is still acceptable at 100M fetches/day:** AF-06 only requires that an invalid link *eventually* returns the defined not-found/expired response, not that it does so to the millisecond (`fn-fetch.md` Section 2.2's Exception makes the same argument for v1). A bounded 5-minute worst case, backed by an active invalidation path that closes the gap to low single-digit seconds in the normal case (Section 5), is a stronger guarantee than v1 shipped, not a weaker one.

---

## 3. Bounded Cache: Size Limit and Eviction

Redis memory is finite, and at 100M fetches/day the eligible key space (every short code ever created, not just currently-hot ones) will exceed any single node's practical memory budget over the system's lifetime. The cache must therefore stay bounded to genuinely **hot** codes — exactly the "frequently used data" framing the review scope asks for — rather than growing to hold the entire dataset.

### 3.1 `maxmemory` and eviction policy

```conf
maxmemory 4gb
maxmemory-policy allkeys-lru
```

- **`maxmemory`** caps Redis's resident data at a fixed budget sized to the working set of *hot* codes, not the total corpus. A short code is a few bytes; a cached value (Section 2.1/2.3) is well under 200 bytes serialized — a 4 GB budget comfortably holds tens of millions of hot entries, far more than the realistic "frequently accessed" subset of even a large corpus (real-world link-shortener traffic is heavily power-law distributed: a small fraction of codes account for most fetches).
- **`allkeys-lru` is recommended over `volatile-lru`**, and over eviction policies that reject writes (`noeviction`) or evict randomly (`allkeys-random`):
  - Every key this cache stores carries a TTL (Section 2.4), so `volatile-lru` (which only considers keys *with* a TTL for eviction, and otherwise behaves like `noeviction` once those run out) would degrade to `noeviction` semantics the moment a non-expiring key ever entered the same keyspace — a fragile coupling to a convention outside Redis's own visibility, rather than an explicit guarantee. `allkeys-lru` evicts the least-recently-used key regardless of TTL, and is honest about the actual requirement: keep the *hottest* codes resident, evict everything else, TTL or not.
  - This is a pure best-effort cache over a durable source of truth (the database) — losing a cold entry to eviction is free (next request re-populates it on a miss, per the existing cache-aside pattern), so `noeviction`'s write-rejection behavior would be actively harmful here (it would make Redis start failing writes under memory pressure instead of gracefully shedding cold entries).
  - `allkeys-lru` approximates true LRU via sampling (Redis does not maintain an exact LRU list, for performance reasons) — sufficient for this use case, since the goal is "keep approximately the hot set," not an exact recency ordering.
- **Result:** the cache self-regulates to the genuinely hot subset of short codes under `maxmemory`, with LRU ensuring a code that goes cold (stops being fetched) is evicted to make room for currently-hot codes, without any application-level cache-size logic needed — the same hands-off eviction philosophy v1's `IMemoryCache.SizeLimit` used (`nfr-scalability.md` Section 2.2), just enforced by Redis instead of the in-process cache.

### 3.2 Sizing note

`4gb` above is illustrative, not prescriptive — the actual budget should be set from observed hit-rate curves in staging/production (increase until the marginal hit-rate gain per additional GB flattens), not guessed upfront. This is called out explicitly so it isn't mistaken for a hard requirement.

---

## 4. Distributed Caching Topology

| Option | Verdict for this scale |
|---|---|
| **Single Redis instance** | Rejected as the steady-state design. A single instance is a single point of failure sitting directly in the redirect hot path (ANFR-01-adjacent: redirect availability); losing it means every cache lookup falls through to the database simultaneously across all API instances — a thundering-herd risk at 100M fetches/day, not just a latency regression. Acceptable only for local dev/single-box testing. |
| **Redis Cluster (self-managed, sharded + replicated)** | Technically capable of the required throughput and HA, but it shifts operational burden (shard rebalancing, failover orchestration, patching, monitoring) onto the team, for a caching layer that is explicitly a performance optimization, not the system of record. That operational cost is disproportionate unless there's a reason to avoid a managed offering (e.g., regulatory/data-residency constraints not present in this project's stated scope). |
| **Managed offering — Azure Cache for Redis (Premium/Enterprise tier) or AWS ElastiCache for Redis** | **Recommended.** |

**Recommendation: a managed Redis offering (Azure Cache for Redis Premium, or AWS ElastiCache for Redis), provisioned with clustering/sharding enabled and a replica per shard for automatic failover.**

Justification:

- **Throughput:** this cache sits on the dominant traffic shape (`nfr-scalability.md` Section 1 — redirects outweigh creates 100:1 to 1000:1), so at 100M fetches/day (~1,150 requests/sec average, with peak multiples well above that) the cache itself must not become the new bottleneck ANFR-06 is trying to eliminate. Managed Redis clustering shards the keyspace across nodes, scaling throughput roughly linearly with node count — the same lever a self-managed cluster gives, without the team building the shard-rebalancing/failover logic themselves.
- **HA, given this is a read-heavy hot path:** ANFR-01 (redirect availability) and ANFR-05 (redirect latency) are both violated if the cache tier flaps. Managed offerings provide automatic primary/replica failover, zone-redundant deployment, and patching without a customer-visible outage — directly protecting the redirect path's availability, which is exactly the path this cache exists to accelerate.
- **Cost/operational trade-off:** a managed offering costs more per GB than self-hosted Redis, but the caching layer here is explicitly *not* the source of truth (Section 2.4's TTL backstop + Section 5's invalidation mean a total cache loss is a performance event, not a data-loss event) — so the operational simplicity of "someone else handles failover and patching" is worth the premium for infrastructure that is important but not authoritative.
- **This mirrors the escalation pattern `nfr-scalability.md` already used for the database tier** (Section 3: swap SQLite for a server RDBMS when concurrency demands it) — pick standard, managed infrastructure for the piece that has outgrown a lightweight/embedded/single-instance solution, rather than building bespoke clustering logic in-house.

---

## 5. Cache Invalidation Strategy

This is the most important section, because the cache now lives on **separate infrastructure from the source of truth** (the database) — unlike v1, where the `IMemoryCache` Decorator invalidated itself in-process, in the same call stack as the write (`nfr-scalability.md` Section 2.3: `_cache.Remove(cacheKey)` called directly after the soft delete persists). At v2 scale, with N API instances each holding a connection to a shared Redis cluster, "the write happened" and "the cache is invalidated" are no longer the same event on the same instance — they must be explicitly connected.

### 5.1 The problem, precisely

A link's cached redirect decision can go stale for exactly the events AF-07 and the metadata/update surface care about:

- **Deactivation/deletion** (AF-07) — a link is soft-deleted; cached readers must stop resolving it.
- **Expiry change** — if a future version allows editing `ExpiresAtUtc` (v1's `fn-fetch.md` Section 6 already flags this as the one field that could become mutable), a cached entry with the old expiry is wrong.
- (Per `fn-fetch.md` Section 6, `OriginalUrl` itself is immutable and never needs this treatment — this section is scoped only to the two lifecycle fields.)

### 5.2 Active invalidation: publish-on-write, subscribe-and-evict

The create/update/delete API path publishes an invalidation event whenever one of the fields in Section 5.1 changes; the event carries just the short code, and a lightweight subscriber service consumes it and evicts (or refreshes) the corresponding Redis key across the whole cluster. The event transport itself (in-process outbox write, Kafka topic, or another broker) is the concern of the Outbox pattern consideration document and `05-kafka-comaporison.md` — this document treats "an invalidation event reaches interested subscribers reliably" as a given capability supplied by those documents, and defines only what happens at the cache boundary:

```
Deactivate/Delete/Update API call
        │
        ▼
Write to DB (source of truth) + write invalidation event to Outbox (same transaction)
        │
        ▼
Outbox relay publishes "ShortUrlInvalidated { ShortCode }" (see Outbox / Kafka docs)
        │
        ▼
Lightweight subscriber (any API instance, or a small dedicated worker)
        │
        ▼
DEL shorturl:v1:code:{shortCode}   (Redis, cluster-wide — one shared cache, one delete reaches every instance's view)
```

- **Why delete, not update-in-place:** evicting the key and letting the next reader repopulate it cache-aside (same pattern as v1, `nfr-scalability.md` Section 2.4) is simpler and safer than pushing a corrected value through the event — it reuses the existing read path's correctness guarantees (the repopulation read always goes through the same `GetByShortCodeAsync`/lifecycle checks as any other miss) rather than trusting the event payload to be a complete, correct replacement value.
- **Why a subscriber, not invalidating inline in the write path:** the write path (API instance handling the delete/deactivate request) already writes to the database and the outbox in one transaction — it should not also take a direct, synchronous dependency on Redis being reachable to complete a delete/deactivate request. Decoupling invalidation into an async subscriber keeps the write path's availability independent of the cache tier's availability (if Redis is down, deactivation still succeeds; the cache just serves stale entries until either the subscriber catches up or the TTL backstop expires them — see Section 5.3).
- **Idempotency:** `DEL` on a possibly-already-evicted or already-expired key is a safe no-op, so at-least-once event delivery (the norm for outbox/queue-based transports) requires no additional dedup logic at the cache boundary.

### 5.3 TTL as the backstop

Section 2.4's 5-minute TTL remains in place even with active invalidation wired up — it is the safety net for the cases active invalidation cannot cover by construction:

- The invalidation event is lost or never published (bug, outbox relay outage).
- The subscriber is down or lagging when the event is published.
- Any future write path that mutates a lifecycle field but is missed when auditing for invalidation call-sites (human error) — the TTL bounds the blast radius of that mistake automatically, the same defensive posture `nfr-scalability.md` Section 2.2 already took for v1.

### 5.4 Maximum staleness window this design guarantees

- **Normal case (invalidation pipeline healthy):** staleness is bounded by event-publish + delivery + subscriber-processing latency — typically low single-digit seconds end-to-end for an outbox-relay-based or Kafka-based transport, not the full TTL.
- **Worst case (invalidation event missed entirely):** staleness is bounded by the **5-minute TTL** (Section 2.4) — identical to the ceiling v1 already accepted for `IMemoryCache`, and for the same reason: AF-06 requires an *eventual* not-found/expired response, not a millisecond-accurate one.
- **Why this is acceptable for this use case:** a short-lived window where a just-deactivated link keeps redirecting is a low-severity, self-healing condition (it corrects itself within minutes with zero operator intervention), not a correctness failure with lasting consequences — there is no financial transaction, no security boundary, and no data-loss risk riding on the exact deactivation instant. This is the same trade-off `fn-fetch.md` Section 6 and `nfr-scalability.md` Section 2.3 already made explicitly for v1; this document's contribution is tightening the *typical* case from "up to 5 minutes, always" to "seconds, with 5 minutes as the rare-case ceiling," while keeping the guarantee cluster-wide instead of per-instance.

---

## 6. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| Why distributed cache | `IMemoryCache` (v1) → Redis (v2): required once the API is horizontally scaled, to get a consistent hit rate and invalidation guarantee across instances | ANFR-05, ANFR-06 |
| What's cached | `{OriginalUrl, ExpiresAtUtc, IsActive}` per short code — minimal redirect-decision data, no DB round-trip | AF-02, AF-06, AF-07 |
| Key scheme | `shorturl:v1:code:{shortCode}` | — |
| Serialization | JSON, short field names, via `StackExchange.Redis` | — |
| TTL | 5 minutes (unchanged from v1), acting as a backstop once active invalidation is in place | ANFR-02 |
| Eviction | `maxmemory` + `allkeys-lru` — bounds resident memory to the genuinely hot subset | — |
| Topology | Managed Redis (Azure Cache for Redis / AWS ElastiCache), clustered with replica failover | ANFR-01, ANFR-05, ANFR-06 |
| Invalidation | Create/update/delete path publishes an invalidation event (Outbox/Kafka docs) → subscriber deletes the Redis key cluster-wide; TTL backstop covers missed events | AF-07, ANFR-02 |
| Max staleness guaranteed | Seconds (normal case), 5 minutes (worst case, invalidation missed) | AF-06 |
