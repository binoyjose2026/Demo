# Functional Design — Fetch (Short URL Resolution & Redirect)

**Layer scope:** `UrlShortener.Api` (controller/routing), `UrlShortener.Application` (resolution service), `UrlShortener.Infrastructure` (repository), `UrlShortener.Domain` (entity/interfaces).
**Companion documents:** `fn-create.md` (short URL creation), `fn-analytics.md` (access-event recording, click counts), `nfr-performance-scalability.md` (caching, throughput, latency targets).
**Status:** v1 initial design.

---

## 1. Purpose & Scope

This document defines the design for resolving a short code back to its original URL and redirecting the caller — the single most frequently exercised operation in the system (ANFR-01, ANFR-05). It covers:

- The end-to-end redirect flow (AF-02).
- The lookup path and how it relies on the indexing strategy in `data-design-guidelines.md`.
- The immutability guarantee (Q7) and how this design leans on it.
- Expiration, deactivation, and not-found handling (AF-06, AF-07, Q8–Q11).
- The metadata retrieval endpoint (AF-05) as a distinct concern from the redirect.
- The redirect HTTP status code decision (301 vs 302).
- The analytics side effect triggered by a resolution (cross-reference only — see `fn-analytics.md`).

It does **not** cover short-code generation/creation (`fn-create.md`) or the mechanics of click-count recording (`fn-analytics.md`).

---

## 2. Requirements Traceability

| Requirement / Question | How this document addresses it |
|---|---|
| AF-02 | End-to-end redirect flow (Section 4). |
| AF-05 | Metadata endpoint as a separate concern (Section 9). |
| AF-06 | Defined not-found/expired response (Section 8). |
| AF-07 | Deactivation handling reuses soft-delete (Section 7). |
| ANFR-01, ANFR-05, ANFR-06 | Aggressive caching enabled by immutability (Section 6); cross-referenced to `nfr-performance-scalability.md`. |
| ANFR-02 | Consistent resolution for the mapping's lifetime — the immutability guarantee this design relies on (Section 6). |
| Q7 | Original URL is immutable once created (Section 6). |
| Q8, Q9 | Expiration is opt-in, no default, capped at a placeholder maximum (Section 7). |
| Q10 | Branded "link expired/removed" page trigger condition (Section 8). |
| Q11 | Deactivated short codes are retired permanently, never reused (Section 7). |

---

## 3. Actors & Entry Points

Fetch exposes **two distinct HTTP entry points** that must not be conflated (see Section 9 for why):

| Endpoint | Route | Purpose | Response shape |
|---|---|---|---|
| **Redirect** | `GET /{shortCode}` | Resolve + redirect a short link, as followed by an end user's browser. | HTTP redirect, or a branded HTML "unavailable" page. |
| **Metadata** | `GET /api/short-urls/{shortCode}` | Programmatic lookup of a link's status/details (AF-05); this is the `GetAsync` action already anticipated as the `CreatedAtAction` target in `design-guidelines.md` Section 3. | JSON (`ProblemDetails` on failure). |

**Design decision — routing:** The redirect endpoint intentionally lives at the **application root** (`GET /{shortCode}`), not under `/api/short-urls/{shortCode}`, even though every other resource in this API is namespaced under `/api/...`.

> **Exception:** Deviates from the standard resource-based routing convention used elsewhere in this API (`design-guidelines.md` Section 3). **Rationale:** the whole point of a "short" URL is a short path; nesting it under `/api/short-urls/` would defeat the purpose. This is the one route in the system that is a public-facing link surface rather than an API resource path.

---

## 4. End-to-End Redirect Flow (AF-02)

```
Browser              RedirectController        IShortUrlResolverService      IShortUrlRepository        AppDbContext (SQLite)
   │  GET /{code}            │                            │                          │                          │
   ├────────────────────────►                             │                          │                          │
   │                         │  ResolveAsync(code)         │                          │                          │
   │                         ├────────────────────────────►                          │                          │
   │                         │                             │  GetByShortCodeAsync(code)                          │
   │                         │                             ├─────────────────────────►                          │
   │                         │                             │                          │  SELECT ... WHERE ShortCode = @code
   │                         │                             │                          │      AND IsDeleted = 0 (global filter)
   │                         │                             │                          ├─────────────────────────►
   │                         │                             │                          ◄─────────────────────────┤
   │                         │                             ◄──────────────────────────┤ ShortUrl? (nullable)    │
   │                         │  (check ExpiresAtUtc in service — see Section 7)        │                          │
   │                         ◄────────────────────────────┤ ResolutionResult          │                          │
   │  302 Found + Location   │                             │                          │                          │
   ◄────────────────────────┤ (or 404 / 410 branded page — see Section 8)             │                          │
   │                         │  fire-and-forget: record access event (AF-08) ─────────────────► fn-analytics.md  │
```

Layering follows `design-guidelines.md` Section 3 (thin controller) and Section 1 (dependency direction):

- **`RedirectController`** (`Api`): binds `shortCode` from the route, calls a single `Application` service method, maps the result to an HTTP response. No business logic, no direct repository/`DbContext` access.
- **`IShortUrlResolverService`** (`Application`): orchestrates the lookup + expiration check; the one place resolution business rules live.
- **`IShortUrlRepository`** (`Domain` interface / `Infrastructure` implementation): the only component that talks to `AppDbContext`.

```csharp
public interface IShortUrlResolverService
{
    Task<ShortUrlResolutionResult> ResolveAsync(string shortCode, CancellationToken cancellationToken = default);
}

public sealed record ShortUrlResolutionResult(ShortUrlResolutionStatus Status, string? OriginalUrl);

public enum ShortUrlResolutionStatus
{
    Resolved,
    NotFound,
    Expired
}
```

```csharp
public class ShortUrlResolverService : IShortUrlResolverService
{
    private readonly IShortUrlRepository _repository;

    public ShortUrlResolverService(IShortUrlRepository repository) => _repository = repository;

    public async Task<ShortUrlResolutionResult> ResolveAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        var shortUrl = await _repository.GetByShortCodeAsync(shortCode, cancellationToken);

        if (shortUrl is null)
        {
            // Covers both "never existed" and "deactivated/removed" — the global
            // soft-delete query filter already excludes IsDeleted rows (Section 7).
            return new ShortUrlResolutionResult(ShortUrlResolutionStatus.NotFound, null);
        }

        if (shortUrl.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= DateTime.UtcNow)
        {
            return new ShortUrlResolutionResult(ShortUrlResolutionStatus.Expired, null);
        }

        return new ShortUrlResolutionResult(ShortUrlResolutionStatus.Resolved, shortUrl.OriginalUrl);
    }
}
```

```csharp
[ApiController]
public class RedirectController : ControllerBase
{
    private readonly IShortUrlResolverService _resolver;

    public RedirectController(IShortUrlResolverService resolver) => _resolver = resolver;

    [HttpGet("/{shortCode}")]
    public async Task<IActionResult> RedirectAsync(string shortCode, CancellationToken cancellationToken)
    {
        var result = await _resolver.ResolveAsync(shortCode, cancellationToken);

        return result.Status switch
        {
            ShortUrlResolutionStatus.Resolved => Redirect(result.OriginalUrl!),   // 302 Found — see Section 10
            ShortUrlResolutionStatus.Expired  => StatusCode(StatusCodes.Status410Gone, LinkUnavailablePage.Html),
            ShortUrlResolutionStatus.NotFound => NotFound(LinkUnavailablePage.Html),
            _ => NotFound()
        };
    }
}
```

---

## 5. Lookup Path & Indexing

The lookup is a single, indexed point read: find the one `ShortUrl` row whose `ShortCode` matches the requested code. This is an entity-specific repository method, exactly the case `design-guidelines.md` Section 2 calls out for going beyond generic `IRepository<T>` CRUD:

```csharp
public interface IShortUrlRepository : IRepository<ShortUrl>
{
    /// <summary>Standard lookup — respects the global soft-delete filter (excludes deactivated/removed links).</summary>
    Task<ShortUrl?> GetByShortCodeAsync(string shortCode, CancellationToken cancellationToken = default);

    /// <summary>Lookup including soft-deleted/expired rows — used only by metadata (Section 9), never by redirect.</summary>
    Task<ShortUrl?> GetByShortCodeIncludingInactiveAsync(string shortCode, CancellationToken cancellationToken = default);
}
```

- `ShortCode` must carry a **unique index** (`IX_ShortUrl_ShortCode`), per `data-design-guidelines.md` Section 7 ("any column used in a frequent `WHERE` ... clause"). This is the single hottest query in the system (ANFR-01, ANFR-05, ANFR-06), so it must resolve as an index seek, never a table scan.
- The lookup relies on the surrogate `Id`/`RowVersion`/soft-delete conventions from `data-design-guidelines.md` unchanged — `ShortUrl` is an ordinary `AuditableEntity`-derived table, no bespoke schema behavior.
- `GetByShortCodeAsync` deliberately does **not** join or eager-load anything beyond the `ShortUrl` row itself (no analytics aggregates, no owner details) — the redirect path only needs `OriginalUrl` and `ExpiresAtUtc` to make its decision, keeping the hot path's query as cheap as possible.

```csharp
public class ShortUrl : AuditableEntity
{
    public string ShortCode { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }   // null = no expiry (Section 7)
    // IsDeleted / DeletedAtUtc (from AuditableEntity) represent deactivation/removal — see Section 7.
}
```

---

## 6. Immutability Guarantee & Its Design Payoff (Q7, ANFR-02)

**Decision (Q7):** the `OriginalUrl` behind a given `ShortCode` never changes once created. There is no "edit URL" operation anywhere in this design — `fn-create.md` is the only writer of `OriginalUrl`, and it writes it exactly once, at insert. ANFR-02 ("a short code shall consistently resolve to the same original URL for the lifetime of that mapping") is this same guarantee stated as a reliability requirement.

This guarantee is what the fetch design is built around:

- **Cache safety without invalidation complexity.** Because `(ShortCode → OriginalUrl)` is write-once, a resolved mapping can be cached aggressively — in-process, distributed, or CDN-fronted — with no cache-invalidation-on-update problem to solve (there is no update). The only cache event this design needs to reason about is *removal* (deactivation/expiry — Section 7), not staleness of the URL value itself. The specific caching technology, TTL, and invalidation trigger are defined in `nfr-performance-scalability.md` and are not repeated here — this document only establishes that caching is *safe*, because of Q7/ANFR-02.
- **No cache-coherency/versioning need for `OriginalUrl` itself.** Unlike most cached data, there is no `RowVersion`-driven "is my cached copy stale" check required for the URL value — immutability makes that question moot for as long as the mapping is active.

> This is a deliberate reliance on a requirement decision, not an assumption: if Q7 is ever revisited (URLs become editable), every cache layer built on this guarantee must be revisited too. That coupling is called out here explicitly so it isn't rediscovered as a bug later.

---

## 7. Expiration & Lifecycle Checking (Q8, Q9, AF-06, AF-07, Q11)

Two independent lifecycle mechanisms feed into "is this short code still resolvable," and this design keeps them as two separate signals rather than collapsing them into one flag:

### 7.1 Expiration — opt-in, no default (Q8, Q9)

- `ExpiresAtUtc` is a **nullable** `DateTime` on `ShortUrl`. `null` means "no expiry" — this is the default for every link unless the creator opts in at creation time (Q8).
- When set, `fn-create.md` is responsible for enforcing the placeholder maximum cap (Q9); fetch only ever *reads* this field, never writes it.
- Expiration is **not** modeled via the standard `IsDeleted` soft-delete convention, because it is not a deletion — the row is fully valid data, just time-boxed. The resolver service (Section 4) compares `ExpiresAtUtc <= DateTime.UtcNow` explicitly, after the repository call, since the global soft-delete query filter has no awareness of expiry.

### 7.2 Deactivation/removal — reuses the standard soft-delete convention (AF-07, Q11)

**Decision:** deactivating/removing a link (AF-07) is modeled as an ordinary soft delete — `IsDeleted = true`, `DeletedAtUtc` set — using the exact `IsDeleted`/`DeletedAtUtc` columns every table already has per `data-design-guidelines.md` Section 5. No separate `IsActive`/`Status` flag is introduced.

- This is a deliberate avoidance of a redundant status column: `IsDeleted` already means "not resolvable anymore," which is precisely what deactivation means for fetch purposes. Adding a second flag with overlapping meaning would violate the "avoid redundant/duplicated design" principle this design follows throughout.
- Because `IsDeleted` is already enforced by the **global EF Core query filter** on every standard query (`data-design-guidelines.md` Section 5), `GetByShortCodeAsync` automatically excludes deactivated/removed links with zero extra code in the resolver — deactivation "just works" through the existing repository convention.
- Q11 ("a deactivated/removed short code is retired permanently and never reused") is satisfied for free: nothing in this design ever re-issues a `ShortCode` that has a row (deleted or not) — that invariant belongs to `fn-create.md`'s code-generation/collision logic, referenced here only to confirm fetch does not need to special-case it.
- Q12 (out of scope: no restore window) means fetch never needs a "reactivate" path — once `IsDeleted = true`, it is final from the resolver's point of view.

### 7.3 Why the resolver still distinguishes `NotFound` vs `Expired` internally

Even though deactivated/removed and never-existed both surface as `NotFound` (the repository returns `null` in both cases — Section 4), **expired** is distinguishable because the row still exists and is returned by the filtered query; only the resolver's explicit `ExpiresAtUtc` check catches it. This distinction is kept internally (Section 8 turns it into different HTTP status codes) even though the *visible* branded page is the same for all three (Q10).

---

## 8. Not-Found / Expired / Deactivated Response (AF-06, Q10)

**Requirement (AF-06):** return a defined not-found/expired response when a short code does not exist or is no longer valid. **Requirement (Q10):** expired, deactivated, or removed links show a simple branded message page rather than a raw error.

### 8.1 Trigger condition

The branded "link expired/removed" page is rendered whenever `RedirectController.RedirectAsync` resolves to anything other than `ShortUrlResolutionStatus.Resolved` — i.e., for all three terminal cases: code never existed, code was deactivated/removed, or code exists but is past `ExpiresAtUtc`. This document defines only the trigger condition and status-code mapping; the page's HTML/branding content is a UI-content concern, not part of this functional design.

### 8.2 Status code mapping

| Resolution status | HTTP status | Rationale |
|---|---|---|
| `NotFound` (never existed, or deactivated/removed) | **404 Not Found** | The resource genuinely does not exist at this URI as far as an external caller can tell — correct whether it never existed or was permanently retired (Q11 means a retired code is indistinguishable from one that never existed, by design — no information is leaked about a code's history). |
| `Expired` | **410 Gone** | The row demonstrably existed and had a resolvable target; `410 Gone` is the HTTP-correct signal for "this resource existed and has been intentionally, permanently removed," which matches Q11's permanence guarantee better than a generic `404`. |

Both cases return the same branded HTML body (Q10); the status code difference is for correctness toward programmatic clients/crawlers, not a user-visible distinction.

### 8.3 Exception — the redirect endpoint renders HTML in an API-only product

> **Exception:** the out-of-scope decisions record "User-facing web UI — API-only for v1" (Q30). The redirect endpoint's branded unavailability page is a deliberate, narrow exception to that decision. **Rationale:** `GET /{shortCode}` is inherently followed by a browser (it's the link end users click), not called by an API consumer — it is the one entry point in this system that is a browser navigation target rather than a JSON API resource. Returning `ProblemDetails` JSON here would render as unreadable text in a browser tab, defeating AF-02/Q10. This exception is scoped narrowly to this one route; every other endpoint in the system (including metadata, Section 9) remains JSON/`ProblemDetails`-only per Q30 and `design-guidelines.md` Section 3.

---

## 9. Metadata Retrieval — A Separate Concern from Redirect (AF-05)

`GET /api/short-urls/{shortCode}` (AF-05) and `GET /{shortCode}` (AF-02, this document's main subject) look similar but serve different purposes and must not share implementation:

| | Redirect (`GET /{shortCode}`) | Metadata (`GET /api/short-urls/{shortCode}`) |
|---|---|---|
| Consumer | End user's browser, following the link. | The link's creator, checking on it programmatically. |
| Visibility of inactive links | Deactivated/expired links are indistinguishable from "not found" (Section 8) — no lifecycle detail is exposed to an anonymous follower. | Must expose accurate lifecycle status (`Active` / `Expired` / `Deactivated`) so the creator can tell *why* a link stopped working — this is the point of AF-05. |
| Repository call | `GetByShortCodeAsync` — respects the global soft-delete filter (Section 5). | `GetByShortCodeIncludingInactiveAsync` — deliberately bypasses the filter (`IgnoreQueryFilters()`), so a deactivated/expired row is still returned for status reporting. |
| Response shape | HTTP redirect or branded HTML (Section 8). | JSON DTO (`ShortUrlMetadataResponse`), `ProblemDetails` on genuine 404. |
| Triggers AF-08 analytics event? | **Yes** — a redirect is a real access/click (Section 11). | **No** — see below. |

**Design decision — metadata retrieval must not count as a click.** AF-08 ("record an access event each time a short URL is resolved") is scoped to *resolution for redirect purposes*. If the metadata endpoint also recorded an access event, an owner checking their own link's status would silently inflate their own click count (AF-09), which would be a correctness bug in analytics, not a feature. The resolver service used by redirect (`IShortUrlResolverService`) and the read path used by metadata are therefore kept as separate `Application`-layer operations rather than one shared "get and count" method — consistent with Single Responsibility (`coding-guidelines.md` Section 8): one path answers "where should this go, and does that count as a visit," the other answers "what is the current state of this link."

```csharp
public sealed record ShortUrlMetadataResponse(
    string ShortCode,
    string OriginalUrl,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc,
    string Status); // "Active" | "Expired" | "Deactivated"
```

---

## 10. Redirect HTTP Status Code: 302, Not 301

**Decision: use `302 Found`** (ASP.NET Core's `Redirect(url)` default — a temporary redirect), not `301 Moved Permanently`.

This looks counter-intuitive given Section 6's immutability guarantee — "the URL never changes" sounds like the textbook case for a permanent redirect. It is a deliberate trade-off in the opposite direction:

- **301 is cached by browsers/intermediary proxies at the HTTP level**, potentially for a very long time. Once a browser caches a `301`, it may never re-issue the request to our server again for that short code on that client — it will navigate straight to `OriginalUrl` locally.
- That client-side caching would silently break three requirements this design must satisfy on **every** access, not just the first:
  - **AF-08** — an access event must be recorded on each resolution. A browser-cached `301` skips our server entirely on repeat visits, undercounting AF-09's click count.
  - **AF-06/AF-07/Section 7** — expiration and deactivation must take effect immediately for new requests. A browser holding a cached `301` would keep redirecting locally even after the link is deactivated or expires, since it never asks our server again.
- **302 Found** is not cached as a standing redirect by default, so every access — first or hundredth — hits `RedirectController`, gets a fresh resolution (Section 4), and has the opportunity to be blocked by Section 7/8's checks and counted by Section 11's analytics hook.

**Reconciling with Section 6:** the immutability guarantee (Q7) is what makes it *safe* to cache the `(ShortCode → OriginalUrl)` mapping aggressively **at our own application/cache layer** (Section 6) — but it says nothing about whether the *HTTP redirect response itself* should be cached by clients outside our control. Those are two different caching questions with two different answers: application-layer caching of the mapping — yes, aggressively; client-level caching of the redirect response — no, because deactivation/expiry/analytics all need every request to reach the server. 302 is the choice that keeps the server authoritative on every request while still letting the *lookup* behind that request be served from a fast, safely-cached path.

---

## 11. Analytics Side Effect (Cross-Reference Only)

A `Resolved` outcome in Section 4's flow triggers an access-event recording as a side effect of the redirect (AF-08); the recording mechanism, schema, and whether it is synchronous or fire-and-forget relative to the redirect response are defined in `fn-analytics.md` and are intentionally not duplicated here — this document's only obligation to that concern is the trigger point shown in Section 4's sequence diagram.

---

## 12. SOLID Notes

- **Single Responsibility**: `RedirectController` only shapes HTTP; `ShortUrlResolverService` only decides resolvability; `ShortUrlRepository` only queries. Metadata's read path is a separate service method (Section 9), not an overloaded resolver.
- **Open/Closed**: expiration and deactivation are each an independent check in `ResolveAsync`; a future third lifecycle rule (e.g., a rate-limited/quarantined status) can be added as another branch without changing the repository or controller.
- **Interface Segregation**: `IShortUrlRepository` adds exactly the two methods fetch needs (`GetByShortCodeAsync`, `GetByShortCodeIncludingInactiveAsync`) beyond generic `IRepository<T>`, rather than a bloated interface.
- **Dependency Inversion**: `RedirectController` and `ShortUrlsController` (metadata) depend on `IShortUrlResolverService`/`Application` service interfaces only — never on `AppDbContext` or `IShortUrlRepository` directly, consistent with `design-guidelines.md` Section 3.

---

## 13. Exceptions & Trade-offs Summary

| # | Exception/Trade-off | Deviates from | Rationale |
|---|---|---|---|
| 1 | Redirect route (`GET /{shortCode}`) lives at the root, not under `/api/...`. | Standard resource-based routing (`design-guidelines.md` §3). | A short link must actually be short (Section 3). |
| 2 | Redirect endpoint returns branded HTML, not `ProblemDetails` JSON. | API-only, no web UI (Q30). | The endpoint is a browser navigation target, not an API call (Section 8.3). |
| 3 | Metadata lookup bypasses the global soft-delete/expiry filter. | Default repository behavior (`data-design-guidelines.md` §5). | Owners need to see *why* a link is unavailable, not just that it is (Section 9). |
| 4 | 302 (temporary) redirect chosen despite the target URL being immutable. | The intuitive "immutable ⇒ permanent redirect" assumption. | Every request must still reach the server for analytics/expiry/deactivation enforcement (Section 10). |
