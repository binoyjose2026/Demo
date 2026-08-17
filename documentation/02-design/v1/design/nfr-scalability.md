# Scalability Design

**Layer:** Cross-cutting non-functional concern
**Traces to:** ANFR-05 (redirect shall be low-latency; redirect traffic significantly exceeds create traffic), ANFR-06 (system shall scale to handle high-volume read/redirect throughput), ANFR-01 (redirect path shall be highly available)
**Consistent with:** `UrlShortener/engineering-standards/guidelines/design-guidelines.md` (layered architecture, Repository pattern, Decorator pattern catalog entry), `UrlShortener/engineering-standards/guidelines/data-design-guidelines.md` (SQLite as the standard embedded database and its documented trade-offs)

---

## 1. Traffic Shape

A URL shortener's load is asymmetric by nature, and ANFR-05/ANFR-06 make that asymmetry an explicit design driver rather than an assumption left implicit:

| Operation | Relative volume | Requirement |
|---|---|---|
| **Redirect / fetch** (`GET /{code}`) | Dominant — typically 100:1 to 1000:1 over creates in real-world shorteners, since one created link is followed many times over its lifetime | ANFR-05 (low-latency), ANFR-06 (high-volume read throughput), ANFR-01 (high availability) |
| **Create** (`POST /api/short-urls`) | Minor, bursty | ANFR-09 (rate-limited, out of this document's scope — see security design) |
| **Metadata / analytics read** (AF-05, AF-10) | Low-to-moderate, not latency-critical | No dedicated SLA beyond general responsiveness |

**Design consequence:** every scalability decision in this document optimizes the **redirect read path** first. Write paths (create, deactivate, click-count increment) are optimized only enough not to bottleneck the read path — this is a deliberate asymmetric investment, not an oversight.

---

## 2. Caching Strategy for Hot Short Codes

### 2.1 Placement

A caching layer sits **in front of the repository**, on the read path used by the redirect operation, implemented as the **Decorator** pattern already named in `design-guidelines.md` Section 8 (`CachingShortUrlRepository` wrapping the EF Core-backed `ShortUrlRepository`) — no new pattern is introduced, and no consumer of `IShortUrlRepository` needs to change.

```
Controller → IShortUrlService → IShortUrlRepository (interface, unchanged)
                                        ▲
                                        │ implements
                        CachingShortUrlRepository (Decorator)
                                        │ wraps
                                ShortUrlRepository (EF Core / SQLite)
```

```csharp
public interface IShortUrlRepository : IRepository<ShortUrl>
{
    Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decorates the EF Core-backed short URL repository with an in-memory cache
/// on the hot redirect-lookup path. Falls through to the inner repository on
/// a cache miss and populates the cache with the result.
/// </summary>
public sealed class CachingShortUrlRepository : IShortUrlRepository
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IShortUrlRepository _inner;
    private readonly IMemoryCache _cache;

    public CachingShortUrlRepository(IShortUrlRepository inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(code);

        if (_cache.TryGetValue(cacheKey, out ShortUrl? cached))
        {
            return cached;
        }

        var shortUrl = await _inner.GetByCodeAsync(code, cancellationToken);

        if (shortUrl is not null)
        {
            _cache.Set(cacheKey, shortUrl, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1, // required when the cache is configured with SizeLimit
            });
        }

        return shortUrl;
    }

    // Update/Delete/AddAsync delegate to _inner, then invalidate — see Section 2.3.

    private static string BuildCacheKey(string code) => $"shorturl:code:{code}";
}
```

- Registered via DI as: `services.AddScoped<ShortUrlRepository>(); services.AddScoped<IShortUrlRepository>(sp => new CachingShortUrlRepository(sp.GetRequiredService<ShortUrlRepository>(), sp.GetRequiredService<IMemoryCache>()));`
- `IMemoryCache` itself is registered **Singleton** via `services.AddMemoryCache(...)` — consistent with `design-guidelines.md` Section 6, which names an in-memory cache instance as a textbook Singleton (stateless-to-consumers, expensive/shared app-wide), while the `CachingShortUrlRepository` decorator stays **Scoped** like every other repository (it wraps a Scoped `AppDbContext`-backed inner repository).

### 2.2 What is cached, and why `IMemoryCache`

- **Cache key:** the short code (`shorturl:code:{code}`) — the exact lookup key used on every redirect request.
- **Cache value:** the resolved `ShortUrl` entity's redirect-relevant fields (original URL, `IsDeleted`, expiry timestamp) — enough to serve a redirect or a not-found/expired response (AF-06) without a database round-trip.
- **Why `IMemoryCache` and not a distributed cache (Redis, etc.):** the project's data-design guidelines already frame SQLite as a single-process, single-file embedded store appropriate for "a single-application, low-to-moderate concurrency workload" (see Section 1 there) — a distributed cache is disproportionate infrastructure for that same deployment shape. `IMemoryCache` is in-process, requires no extra service to run/deploy, and directly attacks the ANFR-05 latency goal by avoiding the database entirely on a hot-code hit. See Section 4 for what changes if the deployment shape changes.
- **Eviction:**
  - **Time-based:** a bounded absolute expiration (`AbsoluteExpirationRelativeToNow`, default 5 minutes) so a cached entry never drifts indefinitely out of sync with the database, bounding the staleness window even if an invalidation is ever missed.
  - **Size-bounded:** `MemoryCacheOptions.SizeLimit` configured at startup so the cache cannot grow unbounded on a long-running instance under a large, varied set of hot codes; least-recently-used entries are evicted first once the limit is reached (`IMemoryCache`'s built-in compaction behavior).

### 2.3 Invalidation on update/delete/expiry

Caching a mutable lookup requires an explicit invalidation contract — this is called out explicitly rather than left implicit, per the coding guidelines' "why, not what" spirit:

| Event | Trigger | Action |
|---|---|---|
| **Update** | Not applicable in v1 — the original long URL is immutable after creation (per `01-summary.md` §B, "no editing after creation") | No cache-invalidation-on-update path is needed for the URL itself. If a future version allows editing (e.g., changing expiry), the decorator's `Update` method must call `_cache.Remove(BuildCacheKey(code))` after a successful `_inner.Update()` + `SaveChangesAsync`, so the cache never serves a stale mapping. |
| **Delete / deactivate** (AF-07) | `IShortUrlRepository.Delete(entity)` (soft delete, per `design-guidelines.md` §2) | The decorator removes the cache entry (`_cache.Remove(cacheKey)`) immediately after the soft delete is persisted, so the very next redirect request misses the cache, re-reads from SQLite, sees `IsDeleted = true`, and returns the not-found/expired response (AF-06) rather than serving a stale cached hit. |
| **Expiry** (time-boxed link) | No explicit invalidation event — expiry is a data condition (`ExpiresAtUtc < now`), not a write | Handled by the bounded cache TTL (Section 2.2): a cached entry is re-validated against the database at most every 5 minutes, so an expired link stops resolving successfully within one cache TTL window of its expiry time, even without an active invalidation trigger. This is a deliberate, bounded trade-off — see the Exception below. |

> **Exception — bounded staleness on expiry, not immediate:** A link that expires mid-cache-window can continue to redirect successfully for up to `CacheDuration` (5 minutes) after its technical expiry, because expiry isn't a write the application controls and therefore has no invalidation hook to attach to. This is accepted because ANFR-05/ANFR-06 prioritize redirect latency/throughput, and the functional requirements do not state expiry must be enforced to the millisecond (AF-06 only requires that expired links *eventually* return the defined not-found/expired response). If a future requirement demands stricter expiry enforcement, the fix is to shorten `CacheDuration` for entries with a set `ExpiresAtUtc` (e.g., cap the TTL at `min(5 minutes, ExpiresAtUtc - now)`), trading a bit of cache-hit-rate for tighter expiry accuracy — not a redesign.

### 2.4 Cache-aside, not write-through

The decorator follows the standard **cache-aside** pattern (populate on read-miss, invalidate on write) rather than write-through (populate cache on every write). This is deliberate: creates are low-volume (Section 1), so pre-warming the cache on create buys little, and keeping the invalidation surface limited to "remove on delete, bounded TTL otherwise" keeps the decorator small and easy to reason about — consistent with the Single Responsibility principle applied to the decorator itself.

---

## 3. Exception — SQLite as the Scalability Ceiling

This is documented explicitly as an **Exception**, per this project's stated practice of surfacing trade-offs rather than hiding them, and it is the single most important limitation this document has to be honest about.

> **Exception: SQLite bounds this system's write concurrency and its ability to scale the database tier horizontally.**
>
> - **What SQLite gives us:** per `data-design-guidelines.md` Section 1, SQLite is the project's chosen database specifically for its zero-server-dependency, ship-in-the-repo, single-file simplicity — an excellent fit for "a single-application, low-to-moderate concurrency workload," which matches this project's scope (a PoC/MVP per `00-getting-started/in-scope/01-summary.md`, with no formal SLA, per §H).
> - **What it costs us:** SQLite uses file-level/database-level locking. It handles many **concurrent readers** well (which is fortunate, because per Section 1 above, reads/redirects are the dominant traffic shape ANFR-05/ANFR-06 care about) but **concurrent writers serialize against each other** — there is effectively one writer at a time for the whole database file. It is explicitly *not* a fit for "high-concurrency, multi-writer production workloads" (`data-design-guidelines.md` §1).
> - **Why this is acceptable for v1:** write volume (creates, deactivations, click-count increments) is the minority traffic shape (Section 1), the project is explicitly a PoC without a contractual SLA (`01-summary.md` in-scope §H, out-of-scope §H), and read-heavy traffic — where SQLite performs adequately — is the requirement ANFR-05/ANFR-06 actually stress. The caching layer in Section 2 further reduces load reaching SQLite on the dominant read path, which is the traffic shape SQLite is least bad at handling.
> - **The ceiling this creates:** as concurrent write volume grows (more simultaneous creates, and — depending on implementation — click-count increments if they are modeled as a write per redirect rather than batched/async), write-lock contention on the single SQLite file becomes the binding constraint, independent of how much the API layer itself is scaled out (Section 4). SQLite also has no native network access (`data-design-guidelines.md` §1), so it cannot be centralized behind multiple API instances without the file itself living on shared, reliably-locking storage — which is itself a fragile, non-standard way to run SQLite at scale, not a recommended production pattern.
> - **What changes if this needs to scale beyond a prototype:** migrate to a server-based RDBMS (SQL Server or PostgreSQL, per `data-design-guidelines.md` §1's own stated escalation path) that supports genuine concurrent multi-writer access over the network. Because this project already isolates all data access behind EF Core and the Repository/Unit-of-Work abstractions (`design-guidelines.md` §§1–2 — `Application` depends on `IRepository<T>`/`IUnitOfWork` from `Domain`, never on `AppDbContext` directly), this migration is a **swap of the EF Core provider and connection string in `Infrastructure`'s DI registration**, not a rewrite of `Application` or `Api`. This is the exact escalation path `data-design-guidelines.md` §1 pre-commits to: *"If the project later needs multi-user server concurrency, horizontal scale, or remote access, that is a signal to migrate to a server-based RDBMS — EF Core's provider model makes that a swappable decision, not a rewrite."*
> - **Click-count writes specifically:** AF-08/AF-09 require recording an access event and incrementing a click count on every redirect — i.e., a write on the hot read path. To avoid turning the dominant traffic shape into a write-contention problem against SQLite, click-count recording should be decoupled from the redirect's critical path (e.g., queued/batched and flushed asynchronously) rather than an inline synchronous write per redirect. This keeps the redirect response fast (ANFR-05) and confines write pressure to a background flush cadence rather than one write per request. This detail is noted here because it is a scalability consequence of the SQLite write-serialization limitation above; the analytics recording mechanics themselves belong in the analytics design document, not this one.

---

## 4. Horizontal Scaling of the API Layer

Independent of the database-tier ceiling in Section 3, the **API layer itself** is designed to scale out:

- **Stateless controllers/services:** per `design-guidelines.md` Section 3, controllers are thin (bind → call one `Application` service → map response) and hold no per-request or cross-request mutable state. `Application` services are similarly stateless collaborators (constructor-injected dependencies, no instance fields mutated across calls). This means any number of API process instances can run concurrently behind a load balancer with no session affinity requirement — a request can land on any instance and be handled identically.
- **No server-side session state:** authentication context (per `01-summary.md` §A, an authenticated user context is assumed/mocked for the PoC) and rate-limiting (ANFR-09) are the only per-caller concerns; neither requires in-process sticky state that would prevent load-balancing across instances, as long as any future rate-limit counter store is externalized (see below) rather than kept in each instance's local memory.
- **The in-memory cache (Section 2) does not block horizontal scale-out, but it does mean each instance has its own cache**, with two consequences worth naming explicitly rather than glossing over:
  - Cache hit rate is per-instance, not shared — a code that is "hot" against instance A but has never been requested against instance B is a cache miss on B until it warms up independently. This is acceptable: each instance still avoids the database on repeat hits, which is what Section 2 is optimizing for; it does not need a global hit rate to deliver its latency benefit.
  - Invalidation (Section 2.3) only clears the cache entry on the instance that processed the delete/deactivate request — other instances still serve their (bounded, TTL-limited) cached copy until it naturally expires. This is the same bounded-staleness trade-off already accepted in Section 2.3's Exception, just multiplied across instances rather than introduced by scaling out.
  - **If stronger cross-instance cache consistency is ever required,** the swap is `IMemoryCache` → a distributed cache (e.g., Redis) behind the same `IShortUrlRepository`/decorator abstraction — again a swap at the DI-registration boundary, not a redesign of callers, because nothing outside the decorator depends on `IMemoryCache` concretely.
- **What does *not* scale out for free — the database file:** this is the direct consequence of Section 3's Exception. A SQLite file is local to one filesystem; running multiple API instances against the *same* SQLite file over shared/network storage is fragile (locking semantics over network filesystems are unreliable) and is not what makes SQLite attractive in the first place (`data-design-guidelines.md` §1: "no native network access"). Practically, this means: **the API layer can be horizontally scaled today; the database tier cannot, until the Section 3 migration to a server-based RDBMS happens.** Scaling out API instances against a single embedded SQLite file only helps if the workload is read-heavy enough that the cache (Section 2) absorbs most traffic before it reaches the file at all — which, per Section 1, it largely is for this application's actual traffic shape.

---

## 5. `Id`/`RowVersion` Conventions as Future Partitioning Groundwork

This project's standard `Id`/`RowVersion` conventions (`data-design-guidelines.md` §§2, 4) were not chosen for sharding — they were chosen for EF Core-idiomatic simplicity on a single SQLite file (auto-incrementing `long`, cheapest/smallest/fastest key type for SQLite's `rowid`-based storage). Called out here only briefly, per this document's instruction not to over-engineer for a need that does not exist yet:

- **`RowVersion` already supports incremental/delta reads** (`data-design-guidelines.md` §4: "a consumer... can record the highest `RowVersion` it has seen and later query `WHERE RowVersion > @lastSeenValue`"). If a future scale-out ever needed a read-replica or cache-warming job to catch up incrementally rather than re-reading the whole table, this column already exists to support that — no new column is needed.
- **The surrogate `long Id`, if a future migration to a server RDBMS ever needed to partition/shard by tenant or time range,** would need to move to a **composite or globally-distinguishable key** (e.g., a shard-prefixed key, or a `Guid`) — `data-design-guidelines.md` §2 already anticipates this exact scenario and pre-authorizes it: *"If a future table genuinely needs client-generated or globally-unique IDs..., use a `Guid` for that specific table's `Id` and call it out explicitly as an exception; don't mix key types silently."* This document does not propose making that change now — there is no present requirement driving it (no partitioning/sharding requirement exists in ANFR-05/ANFR-06 or elsewhere) — it only notes that the escalation path is already documented and does not require inventing a new convention later.
- **Deliberately not designed further here:** no shard key, no partition scheme, no multi-region replication strategy is proposed in this document. Per Section 3's Exception, the actual near-term scalability lever is migrating off SQLite to a server RDBMS — sharding is a follow-on concern for a scale of traffic well beyond this PoC's stated scope (`01-summary.md` — free/unmetered, no SLA, general-purpose PoC), and designing it now would be speculative, unwarranted complexity inconsistent with this project's stated preference against over-engineering.

---

## 6. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| Traffic shape | Design optimizes the redirect/read path; write path optimized only enough not to bottleneck it | ANFR-05, ANFR-06 |
| Hot-code caching | `IMemoryCache`-backed `CachingShortUrlRepository` Decorator in front of `IShortUrlRepository`, cache-aside, bounded TTL + size limit | ANFR-05, ANFR-06; `design-guidelines.md` §8 (Decorator) |
| Invalidation | Explicit `_cache.Remove` on soft delete/deactivate; bounded TTL absorbs expiry (documented Exception on staleness window) | AF-06, AF-07 |
| Database scalability ceiling | SQLite: good for concurrent reads, serializes concurrent writers, no native network access — documented as an **Exception** | `data-design-guidelines.md` §1 |
| Escalation path | Swap EF Core provider/connection string to a server RDBMS (SQL Server/PostgreSQL) — enabled by existing Repository/DI abstraction, not a rewrite | `data-design-guidelines.md` §1; `design-guidelines.md` §§1–2 |
| API horizontal scaling | Stateless controllers/services allow multiple instances behind a load balancer today; per-instance cache is an accepted trade-off; the SQLite file itself does not scale out until the RDBMS migration above | ANFR-01, ANFR-06 |
| Future partitioning | `RowVersion` already supports incremental catch-up reads; `Id` convention has a pre-authorized escalation to `Guid`/composite keys if ever needed — not designed further now | `data-design-guidelines.md` §§2, 4 |
