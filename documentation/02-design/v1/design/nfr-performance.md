# Performance Design — URL Shortener (v1)

**Status:** Draft
**Companion documents:** `nfr-scalability.md` (throughput/scale-out, caching architecture), `nfr-reliability.md` (availability), `fn-fetch.md` (redirect flow), `data-design-guidelines.md`, `design-guidelines.md`, `coding-giudelines.md`

## 1. Purpose & Scope

This document defines the performance design for the URL Shortener service. It focuses on the **redirect (fetch) path** — `GET /{code}` — because redirect traffic is expected to significantly exceed creation traffic (**ANFR-05**) and the redirect path must be highly available and low-latency by definition of being the most frequently exercised operation (**ANFR-01**, **ANFR-06**). Create (AF-01) and analytics (AF-08–AF-10) paths are addressed only where a redirect-path decision affects them (e.g., not blocking the redirect response on analytics writes); they are otherwise low-volume and are not optimization targets here, per the "avoid premature optimization" principle in Section 7.

## 2. Latency Target — Redirect Path

| Requirement | Target (server-side processing time, excluding client DNS/TLS/network RTT) |
|---|---|
| **ANFR-05** — redirect shall be low-latency | **p95 < 50 ms, p99 < 150 ms** under expected v1 load |
| **ANFR-06** — scale to high-volume redirect throughput | Design must hold the above target as read volume grows (see `nfr-scalability.md`) |

- No formal contractual SLA exists for v1 (per `00-getting-started/in-scope/01-summary.md`, Section H — best-effort standard, no formal SLA). The numbers above are the **internal design target** this document is built against, not a customer-facing commitment.
- "Server-side processing time" is measured from the moment the request reaches the redirect controller action to the moment the `302 Found` response is written — i.e., the part of the latency budget this design actually controls. It is dominated by one thing: **a single indexed lookup against the local SQLite file** (no network hop to a remote database engine, per `data-design-guidelines.md` Section 1), which is why a sub-100ms p95 target is realistic for an embedded database without additional infrastructure.
- Every decision in Sections 3–6 exists to keep that one lookup cheap and to keep everything around it from adding avoidable latency.

## 3. Indexing Strategy for the Redirect Query

The redirect path's only required query is: *given a short code, find the active target URL.* This is a direct application of the indexing guidance already established in `data-design-guidelines.md` Section 7 (index the short-code lookup column, index `RowVersion` on every table) — nothing new is introduced here, only applied to the `ShortUrl` entity:

```csharp
public class ShortUrl : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
    // ... remaining fields (CreatedBy, RowVersion, IsDeleted, etc. from AuditableEntity)
}
```

```csharp
// Infrastructure/Configurations/ShortUrlConfiguration.cs
public class ShortUrlConfiguration : IEntityTypeConfiguration<ShortUrl>
{
    public void Configure(EntityTypeBuilder<ShortUrl> builder)
    {
        // Short-code lookup column — this is the redirect path's only WHERE clause.
        // Unique because a retired code is never reused (per in-scope decision, Section B),
        // so Code is globally unique regardless of IsDeleted state.
        builder.HasIndex(s => s.Code)
            .IsUnique()
            .HasDatabaseName("IX_ShortUrl_Code");

        // RowVersion index — required on every table per data-design-guidelines.md Section 7
        // (delta/incremental-sync pattern). Not used by the redirect query itself; listed here
        // only because ShortUrl is the table it applies to.
        builder.HasIndex(s => s.RowVersion)
            .HasDatabaseName("IX_ShortUrl_RowVersion");
    }
}
```

- A **unique index on `Code`** turns the redirect lookup into an O(log n) index seek instead of a table scan — this is the single highest-leverage decision for the latency target in Section 2.
- The global soft-delete query filter (`HasQueryFilter(s => !s.IsDeleted)`, per `data-design-guidelines.md` Section 5) already excludes deactivated/removed links from this query automatically, so the redirect code path does not need to remember an extra `IsDeleted` check — it's structurally guaranteed by the model, not by repeated `WHERE` clauses at each call site.
- No composite index (e.g., `Code + IsDeleted`) is needed: because retired codes are never reused, `Code` alone is already selective enough for a unique lookup.

## 4. Avoiding N+1 Queries and Over-Fetching on the Redirect Path

**N+1 avoidance:** The redirect path resolves exactly one code per request — there is no collection of short URLs being iterated with per-item lookups, so classic N+1 (querying a related entity once per row in a loop) cannot occur here by construction. The discipline still matters for AF-05 (metadata retrieval, if ever listing multiple links with related data): any future listing endpoint must use `.Include()` or a single projecting query instead of resolving related data per item in a loop.

**Over-fetching avoidance — projection, not the full entity:** The redirect path needs three columns: `OriginalUrl`, `ExpiresAtUtc`, and enough to know the row exists and is active. It does **not** need `CreatedBy`, `LastModifiedBy`, `RowVersion`, `DeletedAtUtc`, or any navigation property. Loading the full `ShortUrl` `AuditableEntity` graph for a redirect would pull columns the response never uses and enable EF Core change tracking for a read-only operation — both pure waste on the hottest path in the system.

```csharp
// Domain — a purpose-built, minimal read shape for the redirect path only.
// Not an AuditableEntity: it is not a persisted table, just a query projection contract.
public sealed record ShortUrlRedirectTarget(string OriginalUrl, DateTime? ExpiresAtUtc);

public interface IShortUrlRepository : IRepository<ShortUrl>
{
    Task<ShortUrlRedirectTarget?> FindRedirectTargetAsync(string code, CancellationToken cancellationToken = default);
}
```

```csharp
// Infrastructure — projects at the SQL level (SELECT OriginalUrl, ExpiresAtUtc ...),
// not SELECT * followed by in-memory mapping.
public async Task<ShortUrlRedirectTarget?> FindRedirectTargetAsync(string code, CancellationToken cancellationToken = default)
{
    return await _context.ShortUrls
        .AsNoTracking()
        .Where(s => s.Code == code)
        .Select(s => new ShortUrlRedirectTarget(s.OriginalUrl, s.ExpiresAtUtc))
        .FirstOrDefaultAsync(cancellationToken);
}
```

- **`.AsNoTracking()`** — the redirect path never updates the `ShortUrl` row it just read, so EF Core's change tracker (snapshot, identity map bookkeeping) is pure overhead here and is disabled explicitly.
- **`.Select(...)` projection** — EF Core translates this to a `SELECT OriginalUrl, ExpiresAtUtc` at the SQL level, not `SELECT *`, keeping the row fetched from SQLite and the object materialized in memory both minimal.
- Recording the access event (**AF-08**) is a separate write against `AccessEvent` using only the `ShortUrlId` foreign key already known from the lookup above — it does not re-fetch the `ShortUrl` entity, and (per Section 6) is not on the response's critical path.

### Exception — repository method does not return a `Domain` entity

`design-guidelines.md` Section 2 states repositories return `Domain` entities, never `IQueryable<T>`, so `Infrastructure` stays swappable behind the abstraction. `FindRedirectTargetAsync` deviates from "returns a `Domain` entity" specifically (it still does **not** leak `IQueryable<T>` — the projection happens inside the repository, and the caller still receives a concrete, already-materialized type).

- **Rationale:** The alternative — returning the full `ShortUrl` entity and letting the caller pick two fields — satisfies the letter of "return an entity" but reintroduces exactly the over-fetching this section exists to prevent, on the one path with an explicit latency target (ANFR-05).
- **Scope of the exception:** Limited to this one method, used only by the redirect use case. Every other `IShortUrlRepository`/`IRepository<T>` method (create, metadata retrieval for AF-05, deactivation for AF-07) continues to return the full `ShortUrl` entity as the guideline specifies — those paths are not latency-critical and gain nothing from a bespoke projection.
- **Swappability preserved:** `ShortUrlRedirectTarget` is a plain record owned by `Domain`, not an EF Core type — a future `Infrastructure` implementation (different provider, or the caching decorator in `nfr-scalability.md`) can satisfy `IShortUrlRepository` without depending on EF Core, so the Dependency Inversion intent behind the original rule is kept even though its literal wording ("returns a `Domain` entity") is not.

## 5. Asynchronous I/O

All data access on the redirect path (and every other path) is `async`/`await` end-to-end, per `coding-giudelines.md` Section 5:

```csharp
[HttpGet("/{code}")]
public async Task<IActionResult> RedirectAsync(string code, CancellationToken cancellationToken)
{
    var target = await _shortUrlService.ResolveAsync(code, cancellationToken);

    if (target is null)
    {
        return RedirectToAction(nameof(NotFoundPageController.Show)); // AF-06 — defined not-found response
    }

    return RedirectPermanent(target.OriginalUrl);
}
```

- Controller → application service → repository → `DbContext` is `async` at every hop; no `.Result`/`.Wait()`/`GetAwaiter().GetResult()` anywhere in the chain (coding guidelines Section 5's explicit deadlock/blocking prohibition applies with extra force on the hottest path, where a single blocked thread has the widest blast radius under load).
- `CancellationToken` is threaded from the ASP.NET Core request pipeline through to `FirstOrDefaultAsync(cancellationToken)`, so an aborted client connection frees the request thread and the in-flight SQLite read promptly instead of running to completion for no one.
- This is a correctness-and-throughput property, not a redirect-specific one: async I/O frees the thread pool thread for the duration of the SQLite read, which is what lets a modest number of threads serve the high request *volume* implied by ANFR-06, even though each individual SQLite call is fast.

## 6. Caching

An in-memory cache in front of `IShortUrlRepository` (a `Decorator`-pattern `CachingShortUrlRepository`, per `design-guidelines.md` Section 8's design pattern catalog) is the next lever once the indexed, projected, `AsNoTracking()` query above is in place — it removes the SQLite round-trip entirely for repeat lookups of the same hot codes. The cache's shape, invalidation strategy, and consistency trade-offs (keyed to `RowVersion` bumps, per `data-design-guidelines.md` Section 4) belong to `nfr-scalability.md` and are not repeated here.

## 7. Avoiding Premature Optimization

Per `coding-giudelines.md` Section 11 ("write clear, correct code first; profile before optimizing, and optimize only the parts that measurably matter"), this design deliberately **does not**:

- Add a caching layer, projection types, or bespoke query tuning to the **create** (AF-01) or **analytics retrieval** (AF-10) paths — both are low-volume relative to redirects (ANFR-05's own premise) and use the plain `IRepository<T>` CRUD returning full entities, exactly as `design-guidelines.md` Section 2 describes as the default.
- Introduce a composite or covering index beyond `IX_ShortUrl_Code` until a profiled query pattern (per `data-design-guidelines.md` Section 7: "add indexes driven by actual query patterns, not speculatively") shows one is needed.
- Make the access-event write (AF-08) synchronous-and-blocking on the redirect response, but also does not build out a full async messaging/queue infrastructure for it in v1 — a same-process, awaited-but-independent write (or a lightweight background enqueue, detailed in `nfr-scalability.md`) is the simplest thing that keeps the redirect response from waiting on an unrelated insert, without speculatively engineering a message bus this project's scope (see `00-getting-started/out-of-scope/01-summary.md`) doesn't call for.
- Pre-optimize for a specific request volume number — no load target beyond "significantly exceeds creation traffic" is given in the requirements (ANFR-05), so the target in Section 2 is expressed as a latency budget the architecture holds at any volume the single-node SQLite deployment can reach, rather than a guessed request-per-second figure.

## 8. Summary of Design Decisions and Exceptions

| # | Decision | Traces to |
|---|---|---|
| 1 | p95 < 50 ms / p99 < 150 ms server-side redirect latency target | ANFR-05, ANFR-06 |
| 2 | Unique index on `ShortUrl.Code`; `RowVersion` index per standing convention | ANFR-05; `data-design-guidelines.md` §7 |
| 3 | Redirect query uses `.Select()` projection + `.AsNoTracking()` instead of loading the full entity | ANFR-05 |
| 4 | **Exception:** `FindRedirectTargetAsync` returns a non-entity projection record, not a `Domain` entity | ANFR-05, deviates from `design-guidelines.md` §2 (rationale in Section 4) |
| 5 | Async/await end-to-end with `CancellationToken` propagation on the redirect path | ANFR-06; `coding-giudelines.md` §5 |
| 6 | Access-event write is decoupled from the redirect response | ANFR-05; AF-08 |
| 7 | Caching architecture deferred to `nfr-scalability.md`; not duplicated here | ANFR-06 |
| 8 | No optimization applied to create/analytics paths; no speculative indexing or messaging infra | `coding-giudelines.md` §11 |
