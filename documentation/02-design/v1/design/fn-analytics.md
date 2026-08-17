# Functional Design — Analytics

**Version:** v1
**Status:** Draft
**Layer:** Cross-cutting (triggered from the redirect/fetch flow; exposed via its own retrieval endpoint)
**Traceability:** AF-08, AF-09, AF-10 (`requirement.app.functional.md`); Q24, Q25, Q27, Q33 (`01-requirements/v1-requirements/.../02-answer.md`, summarized in `00-getting-started/02-in-scope/01-summary.md` and `00-getting-started/03-out-of-scope/01-summary.md`)
**Companion docs:** `fn-fetch.md` (redirect/resolve flow that triggers event recording), `fn-create.md` (short URL creation), `nfr-performance.md` (redirect latency budget)

---

## 1. Purpose & Scope

This document designs the **Analytics** capability of the URL shortener:

- **AF-08** — recording an access/click event each time a short URL is resolved.
- **AF-09** — tracking the total access/click count for each short URL.
- **AF-10** — exposing an API to retrieve analytics for a given short URL.

It does **not** design the redirect/resolve mechanics themselves (short-code lookup, not-found/expired handling, the actual HTTP redirect) — that belongs to `fn-fetch.md`. This document only designs what happens *as a side effect of* a successful resolve, and how that side effect's results are later retrieved.

### 1.1 Explicit non-goals (v1)

Per **Q24**, richer analytics are explicitly **out of scope** for v1 and are **not designed here**:

- Click trends over time / time-series charts.
- Referrer, device, or geographic breakdown/reporting views.
- Analytics export (CSV/report download) — Q26.
- A separate BI/reporting surface — Q25 confirms the only consumer is the link creator, via the same API.

The only analytics metric in scope for v1 is **total click count**, plus **last accessed timestamp** as a natural companion field already implied by "last accessed" in AF-10. Nothing beyond AF-09/AF-10 as literally stated should be added speculatively — see Section 5 for the retrieval contract.

---

## 2. Relationship to the Fetch/Redirect Flow

Event recording is a side effect of a successful short-code resolution: **every time `fn-fetch.md`'s redirect flow resolves a short code to an active, non-expired URL, it triggers analytics event recording (AF-08) before or alongside returning the HTTP redirect** — the mechanics of that resolution (lookup, expiry/deactivation checks, not-found handling) are fully owned by `fn-fetch.md` and are not repeated here.

Only **successful** resolutions are recorded. A request for a not-found, expired, or deactivated code does not generate a click event — there is no valid `ShortUrl` to attribute it to, and AF-09's "click count" is conceptually a count of successful redirects.

---

## 3. What Gets Captured Per Event

Capture is constrained by the in-scope privacy decision (**Q33**): no raw IP address, no other directly-identifying PII. Only non-identifying, aggregable data is stored.

| Field | Captured? | Rationale |
|---|---|---|
| Timestamp (UTC) | Yes | Needed for "last accessed" (AF-10) and is the minimal signal needed to count events. |
| Short URL reference (`ShortUrlId`) | Yes | Required to attribute the event to a link at all. |
| Referrer (`Referer` header, if present) | Yes, stored as-is (host/URL string, no truncation logic invented) | Explicitly named as an allowed, non-identifying, aggregable field in Q33. Not surfaced by any v1 API (Section 1.1), but capturing it now avoids a future schema migration if referrer breakdown is ever added — a deliberate, low-cost lean into an already-approved data point, not scope creep. |
| Device type (coarse: e.g., desktop/mobile/bot/unknown, parsed from `User-Agent`) | Yes, coarse classification only | Explicitly named as allowed in Q33. Stored, not surfaced by any v1 API — same rationale as referrer. |
| Coarse region ("if needed") | **No, not captured in v1** | Q33 says "coarse region if needed" — no requirement currently needs geographic data (no geo breakdown is in scope per Q24), so this project treats "if needed" as "not needed yet." Deferred rather than speculatively built. |
| Raw IP address | **No** | Explicitly excluded by Q33. Never logged, never persisted, not even transiently beyond the single request needed to derive device type. |
| Any other directly-identifying value (cookies, session/user identifiers of the *visitor*, full `User-Agent` string) | **No** | Not named as in-scope by Q33; the visitor is anonymous/unauthenticated (link-following requires no login per the in-scope summary, Section A), so there is no legitimate visitor identity to capture even if desired. |

> **Exception:** This document deliberately persists `Referrer` and `DeviceType` even though no v1 API surfaces them (Section 1.1 excludes device/referrer breakdowns from scope). Rationale: Q33 already approves both fields as privacy-safe, and appending columns to an existing analytics-event table later is a strictly harder migration than simply not reading two already-captured columns yet. If this is judged as over-building against Q24's "no richer analytics" intent, the fallback is to drop both columns and capture only `ShortUrlId` + `AccessedAtUtc` — a one-line entity change, called out here so the trade-off is visible rather than silent.

### 3.1 Entity design

Following the data design guidelines, every table — including this one — gets the standard `Id`/audit/`RowVersion`/soft-delete column set via `AuditableEntity`, even though an access-event log is naturally append-only.

```csharp
namespace UrlShortener.Domain.Entities;

/// <summary>
/// Represents a single successful resolution of a short URL, recorded as a
/// side effect of the redirect flow (AF-08). Append-only from the application's
/// perspective; soft-delete/audit fields exist only because they are standard
/// on every entity, not because events are expected to be edited.
/// </summary>
public class ShortUrlAccessEvent : AuditableEntity
{
    public long ShortUrlId { get; set; }
    public ShortUrl ShortUrl { get; set; } = null!;

    /// <summary>UTC instant the redirect was served.</summary>
    public DateTime AccessedAtUtc { get; set; }

    /// <summary>Raw Referer header value, if present. No PII; never an IP or identity.</summary>
    public string? Referrer { get; set; }

    /// <summary>Coarse device classification derived from User-Agent.</summary>
    public DeviceType DeviceType { get; set; }
}

public enum DeviceType
{
    Unknown = 0,
    Desktop = 1,
    Mobile = 2,
    Bot = 3,
}
```

> **Exception:** `CreatedBy`/`LastModifiedBy` (standard audit fields) will be set to a fixed system identity (e.g., `"system:redirect-pipeline"`) rather than a real user, since the visitor triggering the event is anonymous by design (Section 3). This is a controlled reuse of the standard column, not a new field — documented here so it isn't mistaken for a future "who accessed this" identity field.

- **Indexing**: per the data design guidelines' indexing section, index `ShortUrlId` (foreign key, and the column every analytics read filters/aggregates on) and `RowVersion` (standard). A composite `IX_ShortUrlAccessEvent_ShortUrlId_AccessedAtUtc` additionally supports the "last accessed" query (AF-10) — `MAX(AccessedAtUtc) WHERE ShortUrlId = @id` — without a full table scan as event volume grows.
- **Count derivation**: total click count (AF-09) is **not** stored as a separately-maintained counter column on `ShortUrl` in v1. It is derived with `COUNT(*) FROM ShortUrlAccessEvent WHERE ShortUrlId = @id` at read time. This avoids two independent sources of truth (a counter that can drift from the event log it's supposedly summarizing) — consistent with avoiding redundant/duplicated design. If read-time aggregation later proves too slow at scale, introduce a maintained counter as an explicit, documented optimization (see Section 4), not a silent addition.

---

## 4. Synchronous vs. Fire-and-Forget Recording

**Decision: event recording must not block the redirect response.** The HTTP redirect (`fn-fetch.md`'s primary responsibility) is issued as soon as the short code resolves successfully; the analytics write happens asynchronously relative to that response.

Rationale, tied to `nfr-performance.md`'s redirect latency budget:

- The redirect path is explicitly the highest-traffic, lowest-latency-budget operation in the system (ANFR-01, ANFR-05, ANFR-06 — redirect traffic significantly exceeds creation traffic and must stay low-latency and highly available). Making the visitor's redirect wait on an additional database write is a direct tax on the operation the non-functional requirements care most about protecting.
- A failed or slow analytics write must never fail or delay the redirect itself (ANFR-04, graceful degradation on backend failure) — recording a click is diagnostic/reporting data, not part of the correctness contract of "does this short code resolve." Losing an occasional click event under extreme load is an acceptable trade-off; losing or delaying a redirect is not.

**Mechanism (v1, single-process ASP.NET Core host, no external queue in scope):** the redirect endpoint enqueues the event via `IHostedService`-backed background processing (`Channel<T>` + a `BackgroundService` reading from it, or `IBackgroundTaskQueue` — the standard ASP.NET Core "queue work for background processing" pattern) rather than `await`-ing the `INSERT` inline in the request path. The controller/service returns the redirect response immediately after a successful in-memory/DB lookup; the queued background consumer performs the `AddAsync` + `SaveChangesAsync` against `AppDbContext` out-of-band.

```csharp
public interface IAccessEventRecorder
{
    /// <summary>
    /// Queues a click event for asynchronous recording. Never awaited by the
    /// redirect response path — must not throw for "recording is slow" reasons,
    /// only for genuinely invalid input.
    /// </summary>
    void Enqueue(long shortUrlId, string? referrer, DeviceType deviceType);
}
```

> **Exception:** Because `AppDbContext` is registered **Scoped** (per `design-guidelines.md` Section 6) and is tied to the HTTP request's lifetime, a background consumer cannot reuse the request's `DbContext` instance after the response completes. The background consumer resolves its **own** scoped `AppDbContext` via `IServiceScopeFactory.CreateScope()` per batch/flush, consistent with the standard ASP.NET Core background-service-with-scoped-dependency pattern. This is called out explicitly because it is a deliberate deviation from "just inject the repository" — a background service cannot depend on a Scoped service the same way a controller does.

This keeps the design consistent with the layered architecture: the redirect flow (`fn-fetch.md`) depends only on `IAccessEventRecorder` (an `Application`-layer abstraction), never on `Infrastructure` directly, and never blocks on it (fire-and-forget by contract, not just by convention).

---

## 5. Analytics Retrieval API (AF-10)

**Consumer: the link creator only.** Per **Q25**, there is no separate BI/reporting surface and no department/admin-level analytics view (explicitly out of scope) — this is the same authenticated-creator context used elsewhere in the API, not a new access model. Enforcement of "only the creator can view their own link's analytics" is authorization, which — like the rest of authorization in this PoC — is out of scope per the in-scope summary (Section A); the endpoint assumes an authenticated caller context is already established upstream.

### 5.1 Endpoint shape

```
GET /api/short-urls/{code}/analytics
```

- Thin controller, one call into an `Application`-layer `IAnalyticsService`, per `design-guidelines.md` Section 3 (thin controllers, DTOs at the boundary, never domain entities).
- Not-found/expired handling for an unknown `{code}` reuses the same defined response shape `fn-fetch.md` establishes for AF-06 — not redesigned here.

### 5.2 Response DTO

Only the fields AF-09/AF-10 literally call for — no trend/device/geo breakdown fields, per Section 1.1.

```csharp
namespace UrlShortener.Application.Analytics;

/// <summary>
/// Analytics summary for a single short URL (AF-10). Intentionally limited to
/// total click count and last-accessed timestamp — richer breakdowns
/// (trends, device, geography) are out of scope for v1 (Q24).
/// </summary>
public record ShortUrlAnalyticsResponse
{
    public required string Code { get; init; }
    public required long TotalClickCount { get; init; }
    public required DateTime? LastAccessedAtUtc { get; init; }
}
```

- `TotalClickCount` — `long`, not `int`, matching the project's `Id`/count-field convention of favoring 64-bit integers to avoid a future narrow-type migration.
- `LastAccessedAtUtc` — nullable: a link with zero accesses has no last-accessed value. This is the expected initial state for every newly created link, not an error condition.

### 5.3 Query implementation note

Consistent with Section 3's decision not to maintain a separate counter column, the service computes both fields from `ShortUrlAccessEvent` at read time:

```csharp
var totalClicks = await _accessEventRepository.CountAsync(e => e.ShortUrlId == shortUrl.Id, cancellationToken);
var lastAccessed = await _accessEventRepository.MaxAsync(e => e.ShortUrlId == shortUrl.Id, e => e.AccessedAtUtc, cancellationToken);
```

(Exact repository method shape is an `Infrastructure`-layer implementation detail behind `IShortUrlAccessEventRepository : IRepository<ShortUrlAccessEvent>`, per the repository pattern in `design-guidelines.md` Section 2 — a bespoke repository is warranted here because "count/max by `ShortUrlId`" goes beyond generic CRUD.)

---

## 6. Data Retention — Open Item (Q27)

**This document deliberately does not define a retention policy for `ShortUrlAccessEvent` rows.** Per **Q27**, a retention policy was only "Recommended," not confirmed, in the requirements answer document, and the out-of-scope summary explicitly lists it under "Pending / Not Yet Confirmed" rather than as a decided-either-way item.

Consequences of leaving this open, stated explicitly rather than assumed away:

- **v1 behavior**: access events accumulate indefinitely (no scheduled purge job, no TTL). This is a deliberate placeholder, not an oversight — the soft-delete convention (`data-design-guidelines.md` Section 5) already means no data is ever hard-deleted by default application code, so "no retention job" is the natural, guideline-consistent default in the absence of a confirmed policy.
- **Risk being flagged, not solved here**: an ever-growing `ShortUrlAccessEvent` table affects the read-time count/max query in Section 5.3 as event volume grows, and has storage-growth implications for the single-file SQLite database (`data-design-guidelines.md` Section 1). This is noted as a forward-looking risk, not designed around, since retention scope is explicitly unconfirmed.
- **When Q27 is confirmed**, the expected shape of a retention policy — a scheduled job that hard- or soft-deletes/archives `ShortUrlAccessEvent` rows older than a configured window — is a natural extension of this design (a new `Infrastructure`-layer background job, no change to the entity or retrieval API). It is intentionally not designed now to avoid building against an unconfirmed requirement.

---

## 7. Summary of Traceability

| Decision | Traces to |
|---|---|
| Record one event per successful resolve, triggered from the fetch/redirect flow | AF-08 |
| Total click count exposed via retrieval API, derived from event count | AF-09 |
| `GET /api/short-urls/{code}/analytics` returning code, total count, last accessed | AF-10 |
| No raw IP/PII; only timestamp, referrer, device type captured; coarse region deferred as not-yet-needed | Q33 |
| No trend/device/geo breakdown APIs; total click count is the only v1 metric | Q24 |
| Sole consumer is the link creator; no BI/reporting surface | Q25 |
| Event recording is fire-and-forget, never blocks the redirect response | ANFR-01, ANFR-04, ANFR-05, ANFR-06 (via `nfr-performance.md`) |
| Retention policy explicitly left open, not assumed | Q27 |
