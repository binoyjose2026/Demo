# Functional Design — Create Short URL

**Version:** v1
**Status:** Draft
**Scope:** End-to-end design of the "create a short URL" use case only. Companion documents (not duplicated here): `fn-fetch.md` (metadata retrieval / redirect), `fn-analytics.md` (click tracking/reporting), and the non-functional design documents in this same `02-design/v1/design/` folder — in particular the **security design document** (malicious/phishing domain checks, rate limiting, abuse policy) which this document references but does not restate.

This document follows the layered architecture, SOLID principles, and EF Core/SQLite data conventions fixed in `UrlShortner/global/guidelines/design-guidelines.md`, `coding-giudelines.md`, and `data-design-guidelines.md`. It does not introduce any pattern not already listed in the Design Pattern Catalog (design-guidelines.md §8).

---

## 1. Traceability

| Decision in this document | Traces to |
|---|---|
| Core create/validate/generate-code behavior | AF-01, AF-03, AF-04 |
| Authenticated user context required to create | Q1, Q2 |
| Department-level conceptual ownership captured, not enforced | Q3 |
| http/https scheme allowlist, max URL length ~2048 | Q14, ANFR-07 |
| Short codes must not be trivially sequential/enumerable | ANFR-08 |
| Usage ceiling on creation (anon vs. authenticated), explicit error on limit | Q13, Q16, ANFR-09 |
| Automated malicious/phishing domain check at creation | Q17, Q18, ANFR-07 |
| Custom/vanity alias support | Q20 |
| Alias naming rules (length, charset, reserved-word/profanity blocklist) | Q21 |
| Optional expiration, opt-in, no default, placeholder cap | Q8, Q9 |
| Original URL immutable after creation | Q7 |
| No raw IP/PII captured (relevant to `CreatedBy`/ownership fields, not click logs) | Q33 |
| Full auth implementation out of scope; identity is assumed/mocked | Q2 (out-of-scope) |

---

## 2. End-to-End Flow Overview

Standard thin-controller → application-service → repository call chain (design-guidelines.md §3), all data access behind `IUnitOfWork`/`IShortUrlRepository` (design-guidelines.md §2):

```
HTTP POST /api/short-urls
        │
        ▼
[Api]  ShortUrlsController.CreateAsync(CreateShortUrlRequest)
        │  - model binding + [ApiController] automatic 400 on malformed JSON/attribute violations
        │  - ValidateModelStateFilter (design-guidelines.md §5) short-circuits basic DataAnnotations failures
        ▼
[Application]  IShortUrlService.CreateAsync(request, cancellationToken)
        │  1. Resolve current user via ICurrentUserContext          (Q1/Q2 seam)
        │  2. Enforce usage ceiling via IUsageQuotaPolicy            (Q13/Q16 — see security doc)
        │  3. Validate OriginalUrl (scheme, length, well-formed)     (AF-03, Q14)
        │  4. Malicious/phishing domain check via ILinkSafetyChecker (Q17/Q18 — see security doc)
        │  5. Resolve short code:
        │       a. Custom alias path → ICustomAliasValidator + uniqueness check (AF-01, Q20/Q21)
        │       b. System-generated path → IShortCodeGenerator + collision retry (AF-04, ANFR-08)
        │  6. Validate optional expiration (Q8/Q9)
        │  7. Map request → ShortUrl entity, stamp ownership (Q3) and audit fields
        ▼
[Domain/Infrastructure]  IUnitOfWork.Repository<ShortUrl>() / IShortUrlRepository
        │  - AddAsync(shortUrl)
        │  - SaveChangesAsync()  → AppDbContext.SaveChangesAsync sets CreatedAtUtc/CreatedBy/RowVersion=1
        ▼
[Application]  Map ShortUrl entity → ShortUrlResponse DTO
        ▼
[Api]  201 Created, Location: /api/short-urls/{code}, body: ShortUrlResponse
```

Each numbered step below is a guard-clause-style check (coding-giudelines.md §6) — the first failing step short-circuits the pipeline and returns immediately; no step after a failure runs.

---

## 3. Request DTO

Defined in `UrlShortner.Application` (design-guidelines.md §3 — DTOs at the boundary, never `Domain` entities):

```csharp
namespace UrlShortner.Application.ShortUrls;

/// <summary>
/// Request payload to create a new short URL. Bound directly from the HTTP request body.
/// </summary>
public sealed class CreateShortUrlRequest
{
    /// <summary>The long/original URL to shorten. Required. Max length enforced in validation (Q14).</summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>Optional custom/vanity alias (AF-01, Q20). Null/empty means "system-generate a code."</summary>
    public string? CustomAlias { get; set; }

    /// <summary>Optional absolute expiration instant, UTC. Null means "never expires" — there is no default (Q8/Q9).</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}
```

Note: `CustomAlias` and `ExpiresAtUtc` are both opt-in fields left `null` by default — this mirrors the in-scope decision that neither custom aliasing nor expiration has an implicit/default behavior (Q9, Q20).

---

## 4. The "Authenticated User Context" Seam (Q1 / Q2)

**Rule (in scope):** creating a short URL requires an authenticated user identity — every `ShortUrl` row must record who created it and, conceptually, which department-level group owns it (Q1, Q3).

**Exception — documented per the project's exception-callout convention:**
Full authentication/authorization (login system, token validation, role enforcement) is explicitly **out of scope** for this PoC (Q2, out-of-scope §A). Building a real auth pipeline here would contradict that scope decision and would also be thrown away once real auth is chosen. Instead, the design introduces a narrow seam so the *rule* ("creation requires an identity") is honored in code today, without hard-coupling the `Application` layer to any specific auth technology:

```csharp
namespace UrlShortner.Application.Common;

/// <summary>
/// Seam exposing the identity of the user making the current request.
/// Real implementation is deferred (Q2, out of scope for this PoC); a placeholder
/// implementation satisfies the interface until an auth scheme is selected.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>True if a user identity is present on the current request.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Stable identifier of the calling user. Throws if <see cref="IsAuthenticated"/> is false.</summary>
    string UserId { get; }

    /// <summary>Department-level owning group for links this user creates (Q3). May be null if unassigned.</summary>
    string? DepartmentId { get; }
}
```

- Interface lives in `Application` (it is a use-case-level concern, not a domain entity/repository concern).
- The **placeholder implementation** lives in `UrlShortner.Api` (the composition root already implements Application-defined seams — e.g. reading a plain `X-User-Id`/`X-Department-Id` header, or returning a fixed mock identity) and is registered `Scoped` in `Program.cs`. It performs **no real authentication or token validation** — it exists solely so `IShortUrlService` has a non-null identity to persist, and so the seam can be swapped for a real implementation later (Open/Closed — design-guidelines.md §7) without touching `ShortUrlService`.
- `IShortUrlService.CreateAsync` calls `ICurrentUserContext.IsAuthenticated` as its **first** guard clause. If false, the request fails fast with `401 Unauthorized` — enforcing "must be logged in" (Q1) as a structural rule even though the identity behind it is currently mocked.
- `ShortUrl.CreatedBy` (standard audit field, data-design-guidelines.md §3) is set from `UserId`; a `ShortUrl.OwnerDepartmentId` field records `DepartmentId` (Q3). **Enforcing** that ownership (e.g., rejecting cross-department edits) is out of scope (Q3, out-of-scope §A) and is not implemented here — only the data capture is.

---

## 5. Validation Pipeline

Executed in this order inside `ShortUrlService.CreateAsync`, cheapest/most-fail-fast checks first:

| # | Check | Rule source | Failure → HTTP response |
|---|---|---|---|
| 1 | `ICurrentUserContext.IsAuthenticated` is true | Q1, Q2 | `401 Unauthorized` |
| 2 | `IUsageQuotaPolicy` — caller has not exceeded their creation ceiling | Q13, Q16, ANFR-09 (see security doc) | `429 Too Many Requests` with an explicit `ProblemDetails` explaining the limit (Q16 — no silent throttling) |
| 3 | `OriginalUrl` is non-empty and a well-formed absolute URI (`Uri.TryCreate(..., UriKind.Absolute, ...)`) | AF-03 | `400 Bad Request` (`ValidationProblemDetails`) |
| 4 | `OriginalUrl` scheme is `http` or `https` (case-insensitive allowlist) | Q14, ANFR-07 | `400 Bad Request` |
| 5 | `OriginalUrl.Length <= 2048` | Q14 | `400 Bad Request` |
| 6 | `ILinkSafetyChecker` — domain is not flagged malicious/phishing | Q17, Q18, ANFR-07 (see security doc) | `422 Unprocessable Entity` |
| 7a | *(if `CustomAlias` supplied)* alias passes `ICustomAliasValidator` (format) and is not already taken | AF-01, Q20, Q21 | `400 Bad Request` (format/reserved word) or `409 Conflict` (already taken) |
| 7b | *(if `CustomAlias` omitted)* `IShortCodeGenerator` produces a unique code within the retry budget | AF-04, ANFR-08 | `500 Internal Server Error` (retry budget exhausted — logged, expected to be exceptionally rare; see §6) |
| 8 | *(if `ExpiresAtUtc` supplied)* is strictly in the future and `<= CreatedAtUtc + 365 days` (placeholder cap) | Q8, Q9 | `400 Bad Request` |

Steps 3–5 use `System.ComponentModel.DataAnnotations`-style attributes plus a manual scheme check (attributes alone cannot allowlist schemes), consistent with the `ValidateModelStateFilter` pattern in design-guidelines.md §5 for the structural parts; steps 2, 6, 7, 8 are business rules and therefore live in the application service, not in attributes.

```csharp
public static class UrlValidationConstants
{
    public const int MaxOriginalUrlLength = 2048; // Q14
    public static readonly string[] AllowedSchemes = { Uri.UriSchemeHttp, Uri.UriSchemeHttps }; // Q14
}

public static class ExpirationConstants
{
    // Placeholder cap pending confirmation of the exact figure (Q9).
    public static readonly TimeSpan MaxExpirationWindow = TimeSpan.FromDays(365);
}
```

---

## 6. Short-Code Generation (AF-04, ANFR-08)

### Decision

**Random generation over a base62 alphabet, with a bounded collision-retry loop against the persistence layer** — not a base62-encoded incrementing ID.

### Why not incrementing-ID + base62 encoding

That approach is the simpler of the two standard options and would technically satisfy AF-01/AF-04's "generate a unique code" requirement with zero collision risk. It is rejected here because it directly **contradicts ANFR-08** ("short codes shall not be trivially sequential/enumerable"): encoding `Id=1, 2, 3, …` as base62 produces codes (`b`, `c`, `d`, …) that are trivially guessable/enumerable, letting anyone walk the entire link table. Since ANFR-08 is an explicit, already-elaborated requirement, it takes precedence over the simplicity of the sequential approach.

### Why random + collision retry, and how it satisfies AF-04

- **Non-enumerable by construction** (ANFR-08): codes carry no relationship to insertion order.
- **AF-04 explicitly requires collision *handling***, not just uniqueness — random generation is the one approach where collisions are an expected, designed-for occurrence rather than a proof error, which makes AF-04 a first-class, testable code path instead of dead code.
- **Collision probability is negligible at this project's scale** but is still handled defensively per AF-04:
  - Alphabet: `[a-zA-Z0-9]` (62 characters).
  - Code length: **7 characters** → 62⁷ ≈ 3.5 × 10¹² possible codes. At even 10M created links, collision probability per attempt remains astronomically small (birthday-bound ≈ 10M² / (2 × 3.5×10¹²) ≈ 0.0014%) — comfortably supports the "high-volume" ambition in ANFR-06 without lengthening codes.
  - Retry policy: on a collision (an `ExistsByCodeAsync` hit, or a unique-index violation surfaced as `DbUpdateException` on insert — see §8), generate a new random candidate and retry, up to **`MaxGenerationAttempts = 5`**. Exhausting the budget is treated as an exceptional condition (not a normal validation failure) and returns `500 Internal Server Error` with the failure logged (ANFR-10) — this is expected to never realistically trigger given the math above, and its near-impossibility is exactly why a small, cheap retry budget is sufficient rather than needing a fallback strategy (e.g., lengthening the code).

### Design placement (Strategy pattern, design-guidelines.md §8)

```csharp
namespace UrlShortner.Domain.ShortUrls;

/// <summary>
/// Strategy for producing a candidate short code. Implementations are swappable via DI
/// without changing ShortUrlService (Open/Closed — coding-giudelines.md §8).
/// </summary>
public interface IShortCodeGenerator
{
    /// <summary>Produces one random candidate code. Does not guarantee uniqueness — callers must check.</summary>
    string GenerateCandidate();
}
```

- Interface: `Domain` (peer to `IRepository<T>` — a domain-level abstraction consumed by `Application`).
- Implementation (`RandomBase62ShortCodeGenerator`): `Infrastructure`, registered **Transient** (design-guidelines.md §6 lists exactly this as its Transient example) — it is stateless and cheap to construct.
- The **retry-against-persistence loop** lives in `ShortUrlService` (Application), not inside the generator itself — the generator's single responsibility (coding-giudelines.md §8, SRP) is "produce one candidate"; whether a candidate is acceptable is a persistence-aware concern the generator has no business knowing about.

```csharp
// Application/ShortUrls/ShortUrlService.cs (excerpt)
private const int MaxGenerationAttempts = 5;

private async Task<string> ResolveSystemGeneratedCodeAsync(CancellationToken cancellationToken)
{
    for (var attempt = 1; attempt <= MaxGenerationAttempts; attempt++)
    {
        var candidate = _shortCodeGenerator.GenerateCandidate();
        if (!await _shortUrlRepository.ExistsByCodeAsync(candidate, cancellationToken))
        {
            return candidate;
        }
    }

    throw new ShortCodeGenerationException(
        $"Exhausted {MaxGenerationAttempts} short-code generation attempts.");
}
```

---

## 7. Custom Alias Support (AF-01, Q20, Q21)

- Alternative to system generation, mutually exclusive with §6 for a given request (if `CustomAlias` is supplied, it is used verbatim on success — the system generator is not invoked).
- Validated by `ICustomAliasValidator` (Application), backed by configuration (`IOptions<AliasPolicyOptions>`) so limits/blocklist are tunable without a code change (Options pattern — design-guidelines.md §8):

```csharp
public sealed class AliasPolicyOptions
{
    public int MinLength { get; set; } = 3;   // placeholder, tune with real usage data
    public int MaxLength { get; set; } = 32;  // placeholder
    public string AllowedCharacterPattern { get; set; } = "^[a-zA-Z0-9-]+$"; // alphanumeric + hyphen (Q21)
    public IReadOnlySet<string> ReservedWords { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
```

Validation order for a supplied alias:
1. Length within `[MinLength, MaxLength]`.
2. Matches `AllowedCharacterPattern` (alphanumeric + hyphen only, per Q21 — no leading/trailing hyphen, enforced by the pattern).
3. Not present in `ReservedWords` (basic reserved-word/profanity blocklist, Q21). The blocklist content itself is a data/config concern, not a design concern, and is intentionally not enumerated in this document.
4. Not already assigned to an existing, non-deleted `ShortUrl` — checked via the same `IShortUrlRepository.ExistsByCodeAsync` used for system-generated codes (reuse, not a parallel uniqueness mechanism — avoids the duplicated-logic pitfall this project's guidelines caution against). A collision here is a **user input error** (`409 Conflict`), not a retryable generation event like §6 — the key difference being the code came from the caller, so silently substituting a different one would violate the caller's explicit intent.

---

## 8. Optional Expiration (Q8, Q9)

- `ExpiresAtUtc` is `null` unless the caller explicitly opts in — there is no implicit default expiration window (Q9).
- If supplied: must be strictly after the request's `CreatedAtUtc`, and no further than `ExpirationConstants.MaxExpirationWindow` (365 days — explicitly called out in the in-scope decisions as a **placeholder** pending confirmation of the exact figure, Q9) beyond it.
- Stored on `ShortUrl.ExpiresAtUtc` (`DateTime?`). Enforcement of expiry (returning the branded "expired" page, Q10) is a `fn-fetch.md` concern, not this document's — creation only validates and persists the value.

---

## 9. Malicious/Phishing Domain Check (Q17, Q18) — cross-reference only

A minimal automated check runs against `OriginalUrl`'s domain before the link is persisted (step 6, §5). This document intentionally does **not** restate the check's data source, matching strategy, caching, or failure/timeout behavior — those are owned by the security design document in this same folder. For this document, the only relevant facts are:

- The seam is `ILinkSafetyChecker` (Adapter pattern — design-guidelines.md §8's own example is literally "a link-safety-check API"), consumed by `ShortUrlService`, implemented in `Infrastructure`.
- A positive (malicious) result is a hard rejection: `422 Unprocessable Entity`. It is deliberately modeled as a business-rule rejection, not a `400` validation error — the URL is *syntactically* valid (already passed step 3–5); it is *rejected on policy grounds*, which `422` communicates more precisely per RFC 7807/9110 semantics.
- Manual human review of flagged links is explicitly out of scope (Q17, Q18, out-of-scope §C) — a rejection here is final for the request; there is no review-queue workflow in this design.

---

## 10. Usage Ceiling / Rate Limiting at Creation (Q13, Q16, ANFR-09) — cross-reference only

Two distinct concerns, both owned in detail by the security design document, only sequenced here:

- **Per-caller usage ceiling** (Q13 — different limits for anonymous vs. authenticated callers; exact numbers are placeholders pending real usage data): enforced as an application-layer business rule via `IUsageQuotaPolicy`, checked early in `ShortUrlService.CreateAsync` (step 2, §5) using `ICurrentUserContext` to distinguish caller class. Rejected requests receive `429 Too Many Requests` with a `ProblemDetails` body that explains the limit and reason — never silent throttling (Q16).
- **Transport-level abuse protection** (ANFR-09 — "protected against abusive/excessive request volume"): enforced by ASP.NET Core's built-in rate-limiting middleware ahead of the MVC pipeline (design-guidelines.md §4 middleware ordering), so clearly abusive request volume is rejected before it reaches the controller at all, independent of the business-rule quota above.

This document does not restate limit values, response headers, or the middleware configuration — see the security design document.

---

## 11. Persistence — Repository / Unit-of-Work Call Sequence

Following the Repository + Unit-of-Work pattern fixed in design-guidelines.md §2 (repositories return `Domain` entities only; `AppDbContext.SaveChangesAsync` stamps audit fields and `RowVersion` per data-design-guidelines.md §3–4):

```csharp
// Application/ShortUrls/ShortUrlService.cs (excerpt)
public async Task<ShortUrlResponse> CreateAsync(CreateShortUrlRequest request, CancellationToken cancellationToken)
{
    if (!_currentUser.IsAuthenticated)
        throw new UnauthorizedException(); // → 401

    await _usageQuotaPolicy.EnsureWithinLimitAsync(_currentUser, cancellationToken); // → 429 on violation

    var originalUrl = _urlValidator.ValidateAndNormalize(request.OriginalUrl); // → 400 on violation (AF-03, Q14)

    await _linkSafetyChecker.EnsureNotMaliciousAsync(originalUrl, cancellationToken); // → 422 on violation (Q17/Q18)

    var code = string.IsNullOrWhiteSpace(request.CustomAlias)
        ? await ResolveSystemGeneratedCodeAsync(cancellationToken)                 // §6 — AF-04
        : await _aliasValidator.ValidateAndReserveAsync(request.CustomAlias, cancellationToken); // §7 — Q20/Q21

    var expiresAtUtc = _expirationValidator.ValidateOrNull(request.ExpiresAtUtc);  // §8 — Q8/Q9

    var shortUrl = new ShortUrl
    {
        Code = code,
        OriginalUrl = originalUrl,
        ExpiresAtUtc = expiresAtUtc,
        OwnerDepartmentId = _currentUser.DepartmentId,   // Q3 — captured, not enforced
        CreatedBy = _currentUser.UserId,                 // standard audit field
    };

    await _unitOfWork.Repository<ShortUrl>().AddAsync(shortUrl, cancellationToken);
    await _unitOfWork.SaveChangesAsync(cancellationToken); // stamps CreatedAtUtc, RowVersion=1

    return _mapper.ToResponse(shortUrl);
}
```

- `IShortUrlRepository : IRepository<ShortUrl>` adds exactly one entity-specific method needed by this use case: `Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default)`, used identically by both the system-generation retry loop (§6) and the custom-alias uniqueness check (§7) — a single uniqueness mechanism, not two, per the project's stated aversion to redundant design.
- A unique index on `ShortUrl.Code` (data-design-guidelines.md §7 — index columns used in frequent lookups) is the authoritative uniqueness guarantee; the `ExistsByCodeAsync` check is a pre-flight, cheaper-than-exception-handling optimization, not the only safety net. A same-code race between the check and the insert is still possible under concurrent writers (SQLite serializes writers — data-design-guidelines.md §1); `ShortUrlService` treats a unique-constraint `DbUpdateException` on `AddAsync`/`SaveChangesAsync` as an equivalent collision signal and folds it into the same retry loop (§6) or, for a custom alias, into the same `409 Conflict` (§7).

---

## 12. Response DTO

```csharp
namespace UrlShortner.Application.ShortUrls;

/// <summary>
/// Response returned after a short URL is successfully created.
/// </summary>
public sealed class ShortUrlResponse
{
    /// <summary>The fully-qualified short URL a caller can use (e.g. https://short.ly/abc1234).</summary>
    public string ShortUrl { get; set; } = string.Empty;

    /// <summary>The short code alone (e.g. abc1234).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Echo of the original long URL.</summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>Null if the link never expires.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Creation timestamp, UTC.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
```

Deliberately excluded from the response, consistent with design-guidelines.md §3 (DTOs must not leak persistence concerns): `Id`, `RowVersion`, `IsDeleted`/`DeletedAtUtc`, `OwnerDepartmentId`/`CreatedBy` internals beyond what the caller needs. Per Q33, no IP/PII is captured at creation time in the first place, so there is nothing of that kind to exclude.

---

## 13. Error Responses Summary

All non-2xx responses use `ProblemDetails`/`ValidationProblemDetails` (design-guidelines.md §3):

| Condition | Status | Notes |
|---|---|---|
| Missing/invalid JSON body, DataAnnotations failure | 400 | Caught by `ValidateModelStateFilter` before the service is invoked |
| No authenticated user context | 401 | §4 — structural check even though real auth is mocked |
| Usage ceiling exceeded | 429 | §10 — explicit reason in body (Q16), never silent |
| `OriginalUrl` malformed / not absolute | 400 | §5 step 3 (AF-03) |
| `OriginalUrl` scheme not http/https | 400 | §5 step 4 (Q14) |
| `OriginalUrl` exceeds 2048 chars | 400 | §5 step 5 (Q14) |
| Domain flagged malicious/phishing | 422 | §9 (Q17/Q18) |
| Custom alias fails format/length/blocklist rule | 400 | §7 (Q21) |
| Custom alias already in use | 409 | §7 |
| System-generated code retry budget exhausted | 500 | §6 — logged (ANFR-10), expected to be effectively unreachable |
| `ExpiresAtUtc` in the past or beyond cap | 400 | §8 (Q8/Q9) |
| Successful creation | 201 | `Location` header set to the new resource per design-guidelines.md §3's `CreatedAtAction` example |

---

## 14. Explicit Design Exceptions (summary)

Per this project's convention of calling out trade-offs rather than hiding them:

1. **No real authentication is implemented.** `ICurrentUserContext` is a seam with a placeholder implementation (§4). This is a deliberate, scope-driven exception (Q2), not an oversight — the *rule* is honored structurally; the *mechanism* is deferred.
2. **Ownership (department-level group) is captured but not enforced.** `OwnerDepartmentId` is written on every `ShortUrl` at creation time so the data model is ready for authorization later, but no check in this flow restricts who may act on a link based on it (Q3, out-of-scope §A).
3. **All numeric limits in this document are placeholders**: 2048-char URL length is confirmed (Q14); alias length bounds, the 5-attempt collision-retry budget, the 7-character code length, and the 365-day expiration cap are engineering judgment pending real usage data, consistent with how Q9/Q13 are already flagged as placeholders in the in-scope decisions. They are implemented as named constants/`IOptions<T>` (never magic numbers, per coding-giudelines.md §4) specifically so they can be tuned without a design change.
