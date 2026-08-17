# Security Design — URL Shortener (v1)

**Layer:** Non-functional / cross-cutting concern
**Applies to:** `UrlShortner.Api`, `UrlShortner.Application`, `UrlShortner.Infrastructure`
**Traces to:** ANFR-07, ANFR-08, ANFR-09 (`requirement.app.non-functional.md`); Q1, Q2, Q13, Q14, Q16, Q17, Q18, Q33 (`00-getting-started/in-scope/01-summary.md`); Q2, Q3, Q17, Q18, Q19 (`00-getting-started/out-of-scope/01-summary.md`); Section 10 of `coding-giudelines.md`; Section 4 of `design-guidelines.md` (middleware pipeline)

---

## 1. Purpose & Scope

This document defines the security design for v1 of the URL shortener. A URL shortener has a narrow but sharp threat surface: it is, by design, a public system that takes arbitrary URLs from largely untrusted callers and turns them into a trusted-looking, short, easily-shared link. Nearly every URL-shortener-specific risk (phishing, open redirect, abuse-at-scale) follows directly from that one fact.

This document covers only what is **in scope for v1** per the requirement decisions above. Where a mitigation is a well-known best practice but was explicitly ruled out of scope by the product owner (e.g., manual moderation, full auth), it is called out as an **Exception** rather than silently designed around.

---

## 2. Threat Model

### 2.1 Assets

| Asset | Why it matters |
|---|---|
| The short-code → original-URL mapping | Integrity here is the core value proposition (ANFR-02). |
| The redirect endpoint's trust/reputation | A short link inherits the shortener's domain reputation — attackers want to borrow that trust for phishing. |
| Click/analytics data | Business value data; also a privacy liability if mishandled (Q33). |
| Creation endpoint capacity | A finite resource attackers can exhaust (ANFR-09). |

### 2.2 Threat Actors

- **Anonymous public caller** — can hit both creation and redirect endpoints; the product is public-facing (Q29), so this is the default, not an edge case.
- **Authenticated-but-malicious user** — has a valid user context (Q1/Q2) but uses it to submit malicious links, script bulk creation, or scrape analytics they don't own.
- **Automated bot/scraper** — enumerates short codes or hammers the creation endpoint.

### 2.3 Threats & Mitigations

| # | Threat | Description | Mitigation (this document) |
|---|---|---|---|
| T1 | **Open-redirect abuse** | The shortener itself becomes an open redirector: `https://short.example/abc123` silently forwards to an attacker-controlled site, which attackers embed in phishing emails because the visible domain is the trusted shortener's, not the malicious destination's. | This is not preventable by *blocking* redirects (that's the product's entire function) — it's mitigated by tightening what's *accepted at creation time* (§3), running the malicious/phishing check at creation (§4), and never redirecting based on any input other than the stored, validated `OriginalUrl` for a resolved short code (§3.4). |
| T2 | **Malicious/phishing link creation** | An attacker uses the service to create a short link to a phishing/malware site, banking on the shortener's clean domain reputation. | Automated domain reputation check at creation (§4). Explicitly **not** fully preventable in v1 — see Exception in §4.3. |
| T3 | **SSRF-adjacent risk from arbitrary URL input** | If any server-side component ever *fetches* the submitted URL (e.g., a future link-preview/thumbnail feature), an attacker could target internal/private network addresses (`http://169.254.169.254/...`, `http://localhost:5432`, RFC1918 ranges) to probe or attack internal infrastructure. | v1 **never fetches or resolves the submitted URL server-side** — creation only validates syntax/scheme/length and stores the string (§3); redirect only returns an HTTP redirect to the browser, it does not proxy or fetch the destination itself. This design choice eliminates classic SSRF risk for v1 by construction. Flagged explicitly because link-preview generation is out of scope for v1 (`out-of-scope/01-summary.md` §D) but is a plausible future feature — if added, it **must** go through an SSRF-safe outbound fetcher (private-IP/link-local blocklist, no redirects followed blindly, timeout) behind the `Infrastructure` Adapter pattern already reserved for external integrations (`design-guidelines.md` §8). |
| T4 | **Short-code enumeration / scraping** | Sequential or predictable codes let an attacker walk the entire link space, harvesting private-by-obscurity links or scraping analytics. | ANFR-08: codes are not trivially sequential (generation strategy is covered in `nfr-create.md`, not repeated here); rate limiting (§5) bounds enumeration throughput regardless of code shape. |
| T5 | **Creation-endpoint abuse / resource exhaustion** | Scripted bulk creation floods the database, degrades redirect latency (ANFR-05), or is used to mass-produce phishing links faster than any check can react. | Rate limiting + creation-volume ceilings (§5). |
| T6 | **PII leakage via click logs** | Storing raw IP addresses or other identifying data in click logs creates a privacy liability with no corresponding product requirement. | PII-safe logging design (§6). |
| T7 | **Credential/secret leakage** | Hardcoded connection strings or keys committed to source control. | Secrets handling (§7). |
| T8 | **Transport interception / downgrade** | Traffic (including any future auth tokens) sent over plain HTTP. | HTTPS enforcement (§8). |
| T9 | **Standard web vulnerabilities** (XSS on the branded expired/removed-link page, injection, unhandled-exception information disclosure) | The branded message page (Q10) renders link-related data; any templating there is an XSS surface if not encoded. | Covered by the standard ASP.NET Core middleware stack (§9) plus coding-guideline output-encoding/parameterized-query rules (`coding-giudelines.md` §10), not re-derived here. |

**Out of scope, called out explicitly (not silently ignored):**

- Full authentication/authorization enforcement — see §10 (Exception).
- Manual/human content moderation — decided out of scope (Q17, Q18); v1 relies solely on the automated check in §4.
- Public abuse-reporting flow — decided out of scope (Q19); there is no user-facing "report this link" mitigation for v1.
- Formal regulatory compliance program (GDPR/CCPA certification) — decided out of scope (Q32); §6 is a privacy-conscious *design default*, not a compliance certification.

---

## 3. Input Validation

All validation happens at the `Application` layer boundary (a validator invoked from the create-link use case), consistent with `coding-giudelines.md` §10 ("validate all external input... at the boundary before acting on it") and `design-guidelines.md` §3 (thin controllers, DTOs at the boundary). Controllers never accept a URL and act on it without passing through this validator first.

### 3.1 Scheme allowlist

- **Only `http` and `https` are accepted.** (Q14)
- Rejected outright, with no special-casing: `javascript:`, `data:`, `file:`, `ftp:`, `vbscript:`, and any scheme not in the allowlist. This is a hard denylist-by-omission — the validator checks membership in `{ "http", "https" }`, it does not try to blocklist "known bad" schemes, because an allowlist is closed and a denylist is not.
- `javascript:`/`data:` URIs are the classic vector for turning a "URL shortener" into a stored-XSS gadget if a client ever renders the destination in an anchor tag without checking the scheme first — the allowlist removes that risk at the source.

```csharp
private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
{
    Uri.UriSchemeHttp,
    Uri.UriSchemeHttps,
};

public ValidationResult ValidateScheme(Uri candidate)
{
    if (!AllowedSchemes.Contains(candidate.Scheme))
    {
        return ValidationResult.Fail($"Scheme '{candidate.Scheme}' is not allowed. Only http/https URLs are accepted.");
    }

    return ValidationResult.Success();
}
```

### 3.2 Max length

- **Maximum submitted URL length: 2048 characters** (Q14, matching the practical limit widely used by browsers/servers).
- Enforced as a DTO-level `[MaxLength(2048)]` / `StringLength` annotation (caught by the standard MVC model-validation action filter, `design-guidelines.md` §5) **and** re-checked in the `Application`-layer validator, since the validator is the authoritative boundary and the DTO attribute is a fast-fail convenience, not a substitute.
- Rationale beyond "matches common practice": an unbounded string is itself a minor DoS vector (large request bodies, oversized index entries) and this project stores `OriginalUrl` as `TEXT` in SQLite with no native length constraint of its own.

### 3.3 Well-formed URI check

- The submitted string must parse as an absolute URI (`Uri.TryCreate(input, UriKind.Absolute, out var uri)`) before scheme/length checks run — malformed input is rejected per AF-03, independent of the security-specific checks in this document.

### 3.4 No server-side resolution of destination

- As noted in T3, the validator checks **syntax only** — it never issues an outbound HTTP request to the submitted URL to "check if it's reachable." Combined with the malicious-domain check in §4 (which is a **lookup against threat-intel data**, not a fetch of the URL itself), this keeps v1 free of SSRF surface without needing an SSRF-hardened HTTP client.

---

## 4. Malicious/Phishing Domain Check (Creation-Time)

Per Q17/Q18, v1 includes a **minimal automated check only** — no manual review team (explicitly out of scope, `out-of-scope/01-summary.md` §C).

### 4.1 Design

- Implemented as an `Infrastructure`-layer **Adapter** (`design-guidelines.md` §8 Design Pattern Catalog already reserves this pattern for "an analytics or link-safety-check API"), behind an `Application`/`Domain`-defined interface:

```csharp
// Domain or Application — the abstraction the use case depends on
public interface IMaliciousUrlChecker
{
    Task<bool> IsFlaggedAsync(Uri url, CancellationToken cancellationToken = default);
}
```

```csharp
// Infrastructure — the concrete implementation, swappable without touching Application
public class ExternalMaliciousUrlChecker : IMaliciousUrlChecker
{
    // Wraps a third-party reputation/safe-browsing API or a local denylist snapshot.
    // Isolates the third-party SDK/HTTP shape from the rest of the solution.
}
```

- Called synchronously as one step of the create-link use case, **before** the link is persisted. A flagged URL is rejected with a `400`/`422` `ProblemDetails` response explaining why (consistent with the "explicit error, no silent throttling" principle already established for rate limits, Q16 — applied here to rejection as well, for consistency of UX).
- The check runs **once, at creation time**, not on every redirect — redirect latency is the product's most latency-sensitive path (ANFR-05) and must not carry a synchronous third-party call on every hit.

### 4.2 What "minimal automated" means in practice

A domain-reputation lookup against one of: a maintained denylist (e.g., an open phishing-domain feed, refreshed periodically), or a third-party safe-browsing API. The exact provider is an `Infrastructure`-layer implementation detail and is intentionally not pinned down in this v1 document — the `IMaliciousUrlChecker` abstraction is what matters architecturally; the concrete provider is swappable (Strategy/Adapter, per `design-guidelines.md` §8) without changing the use case.

### 4.3 Exception — coverage is inherently partial

> **Exception:** An automated domain check only catches URLs that are *already* on a known-bad list or fail a heuristic; it cannot catch a freshly-registered phishing domain not yet flagged anywhere, and it cannot catch a legitimate domain that is compromised after the check runs. Combined with the decision to exclude manual review (Q17/Q18) and public abuse reporting (Q19), v1 has **no mechanism to catch novel or post-creation-compromised malicious links**. This is a deliberate, product-owner-confirmed scope trade-off for a v1 PoC, not an oversight — documented here so it isn't mistaken for a completeness guarantee.

---

## 5. Rate Limiting & Creation-Volume Limits

Traces to ANFR-09 and Q13/Q16.

### 5.1 Design

- Implemented using ASP.NET Core's built-in **rate-limiting middleware** (`Microsoft.AspNetCore.RateLimiting`), applied to the creation endpoint specifically — the redirect endpoint is intentionally excluded from aggressive limiting since it must stay low-latency and high-availability per ANFR-01/ANFR-05.
- Two independent controls, per Q13:

| Control | Applies to | Notes |
|---|---|---|
| **Per-caller rate limit** | Requests/time-window to the creation endpoint | Keyed by user identity when an authenticated context is present, falling back to a caller-identifying key (e.g., client IP, or API-consumer key if one exists) for anonymous callers. Exact key strategy depends on the auth mechanism eventually chosen (see §10) — this document specifies the *policy shape*, not the final key source. |
| **Creation-volume ceiling** | Total/rolling-window link count per identity | A distinct, coarser control from the rate limiter — bounds sustained bulk creation even if each individual request is spaced out enough to dodge the rate limiter. |

- **Anonymous vs. authenticated ceilings differ** — authenticated callers get a materially higher allowance than anonymous ones, consistent with Q13's "different for anonymous vs. authenticated sources."

```csharp
// Program.cs — placeholder policy shape; exact numbers are NOT final (see 5.2)
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("short-url-creation", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,          // PLACEHOLDER — see 5.2
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,             // no queueing: reject immediately, no silent throttling (Q16)
            }));
});
```

### 5.2 Exception — numeric limits are placeholders

> **Exception:** Per Q13, exact numeric thresholds (requests/minute, links/day per anonymous vs. authenticated caller) are **explicitly placeholders pending real usage data**. This document fixes the *policy shape* (per-caller rate limit + separate volume ceiling, anonymous < authenticated, explicit rejection over silent throttling) as the stable design decision; the numbers themselves are configuration (`IOptions<RateLimitSettings>`, per the Options Pattern already standardized in `design-guidelines.md` §8), not hardcoded, so they can be tuned without a code change once usage data exists.

### 5.3 Explicit-error requirement

- Per Q16: exceeding either limit returns a `429 Too Many Requests` with a `ProblemDetails` body stating which limit was hit and, where feasible, when the caller can retry (`Retry-After` header). No request is silently dropped or queued indefinitely (`QueueLimit = 0` above).

---

## 6. PII-Safe Click Logging

Traces to Q33.

- Click/access-event records (AF-08) capture only **non-identifying, aggregable data**: timestamp (UTC), coarse region (if derived, e.g., country/region from IP *without persisting the IP itself*), referrer, device type/category.
- **Raw IP addresses are never persisted.** If coarse geolocation is implemented, the IP is used transiently (in-memory, for the duration of that single request) to derive a coarse region and then discarded — it is never written to a column, log sink, or long-term store as part of the click record.
- This is a **privacy-conscious design default**, not a certified compliance program — formal GDPR/CCPA compliance is explicitly out of scope (Q32), and there is no published privacy policy/consent mechanism (Q34, out of scope). The design choice here reduces exposure without claiming regulatory certification.
- Schema-level consequence: the `ClickEvent`/`AccessEvent` entity (defined in `nfr-analytics.md` / the analytics design document, not duplicated here) has **no `IpAddress`, `UserAgent`-as-PII, or similar column** by design — this is a structural guarantee, not a runtime filter that could be bypassed by a future code change. If a future requirement needs IP-based abuse detection, that must be a new, explicitly-scoped decision (e.g., short-lived hashed IP for rate-limiting purposes only, never joined to click analytics), not a quiet addition to the click log.
- **Note (Pending, per `out-of-scope/01-summary.md`):** click/access-event data retention policy is still unconfirmed ("Recommended," not decided) — this document does not assume an auto-purge/TTL; if one is later confirmed, add it as a scheduled housekeeping job, not a change to what's captured per event.

---

## 7. Secrets & Connection-String Handling

Directly enforces `coding-giudelines.md` §10.

- **No hardcoded secrets, connection strings, or API keys in source** — this applies to the SQLite connection string, and to any credentials the `ExternalMaliciousUrlChecker` (§4) needs for a third-party reputation API.
- Configuration is layered per standard ASP.NET Core convention:
  - `appsettings.json` — non-sensitive defaults and structure only (e.g., connection string *shape*, not production values).
  - `appsettings.{Environment}.json` — environment-specific overrides, still no secrets committed.
  - **User Secrets** (`dotnet user-secrets`) for local development.
  - **Environment variables** or a secrets manager (e.g., Azure Key Vault via `Microsoft.Extensions.Configuration.AzureKeyVault`) for deployed environments — the specific target is an infrastructure/deployment decision outside this document's scope, but the *pattern* (never in source, always via `IConfiguration`) is fixed here.
- Accessed exclusively through `IConfiguration` / the **Options Pattern** (`IOptions<T>`, already standardized in `design-guidelines.md` §8), never `Environment.GetEnvironmentVariable` calls scattered through the codebase.

```csharp
// Good — matches coding-giudelines.md §10 exactly
services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(configuration.GetConnectionString("DefaultConnection")));
```

- `.gitignore` must exclude any local secrets file (`appsettings.Development.json` if it ever holds real secrets, `secrets.json`) — verified as part of source-control hygiene (`coding-giudelines.md` §12), not re-specified here.

---

## 8. HTTPS Enforcement

- `UseHttpsRedirection()` is registered early in the middleware pipeline (before routing), so any plain-HTTP request is redirected to HTTPS rather than served.
- `UseHsts()` is enabled for non-development environments, instructing browsers to prefer HTTPS for subsequent requests and reducing exposure to SSL-stripping-style downgrade attempts (T8).
- This applies uniformly to both the creation API and the redirect endpoint — the redirect endpoint is a bare HTTP redirect to the caller's browser, not a fetch, but it still must itself be served over HTTPS so the shortener's own domain isn't the weak link in an otherwise-HTTPS chain.
- Kestrel/hosting-level TLS certificate configuration is a deployment concern, not a design concern, and is intentionally not detailed here.

---

## 9. Standard ASP.NET Core Security Middleware (Placeholders)

Per `design-guidelines.md` §4, the following are fixed as **placeholders establishing pipeline shape and order** — full implementation matures alongside the rest of the API, consistent with how that document already treats this pipeline:

1. **Global exception-handling middleware** — converts unhandled exceptions to `ProblemDetails`; critically, this prevents stack traces/internal details leaking to callers (a T9-class information-disclosure risk), per `coding-giudelines.md` §6 ("exception messages... free of sensitive data").
2. **Correlation-ID / request logging middleware** — every request gets a correlation ID, enabling security incident investigation (e.g., tracing an abuse pattern across the rate-limit/creation/redirect path) without needing to log PII to do it (consistent with §6 above).
3. **HTTPS redirection / HSTS** — see §8; sits ahead of authentication in the pipeline so no credential-bearing request is ever processed over plain HTTP.
4. **Authentication middleware** (`UseAuthentication`) — see §10; registered as a placeholder now, wired to a concrete scheme later.
5. **Authorization middleware** (`UseAuthorization`) — enforces the "creation requires an authenticated user context" rule (§10) once authentication is real; also the eventual home for department-level ownership checks (out of scope for enforcement per Q3, but the pipeline slot is reserved so it isn't a structural retrofit later).
6. **Rate-limiting middleware** — see §5; sits after authentication so the per-caller partition key can use identity when available, before MVC endpoint execution.

Registered order in `Program.cs`: exception handling → correlation/logging → HTTPS redirection/HSTS → authentication → authorization → rate limiting → MVC endpoints. This extends (does not contradict) the order already fixed in `design-guidelines.md` §4, inserting the two security-specific stages (HTTPS, rate limiting) at the points where they take effect.

---

## 10. Authenticated User Context for Creation — Scope Boundary

This is the most important scope boundary in this document, stated explicitly per the task's own instruction not to hide it.

### 10.1 What is in scope (design decision)

- **Link creation requires an authenticated user context.** (Q1, Q2) The create-link use case accepts/expects a caller identity, and that identity is what:
  - keys the per-caller rate limit and creation-volume ceiling (§5) at the "authenticated" tier rather than the lower "anonymous" tier,
  - is recorded as `CreatedBy` on the `ShortUrl` entity per the standard audit-field convention (`data-design-guidelines.md` §3),
  - is the basis for the department-level ownership model (Q3) — the *model* (a link belongs to a creator/department) is in scope even though *enforcing* that ownership is not.
- Redirect/consumption of a link remains ungated — anyone with the short URL can use it (Q1). Only **creation** requires an authenticated context.

### 10.2 What is explicitly out of scope for v1 (Exception)

> **Exception:** The actual authentication/authorization **mechanism** — login flow, credential/token issuance and validation, session management, role/permission enforcement — is **out of scope and mocked for this v1 PoC** (`out-of-scope/01-summary.md` §A, Q2/Q3). Concretely:
> - There is no real login system. "An authenticated user context is assumed" — the design treats a caller identity as a given input to the create-link use case, not something this system proves.
> - `UseAuthentication`/`UseAuthorization` are wired into the pipeline as **placeholders** (§9, per `design-guidelines.md` §4's own placeholder framing) with **no concrete scheme selected** for v1. A mock/stub identity provider (e.g., a fixed test-user claim, or a trivially-trusted header) stands in for real auth so the rest of the design — audit fields, rate-limit tiers, ownership model — can be built and demonstrated without waiting on an auth system decision.
> - Department/super-admin role-based access control (Q4), team/multi-owner ownership (Q5), and admin moderation roles (Q6) are all **not built** — consistent with this same scope decision.
>
> **Why this is documented as an exception rather than silently designed as "real auth":** every other decision in this document that references "authenticated caller" (rate-limit tier in §5, `CreatedBy` audit field, ownership model) is written against the *concept* of an authenticated identity, not against any specific auth technology. When a real authentication mechanism is selected in a later version, it should be able to slot into the `UseAuthentication` placeholder and populate the same identity concept **without requiring redesign of §5, §6, or the audit-field convention** — those were deliberately built against the abstraction, not the mock. This is the practical benefit of stating the exception explicitly now instead of quietly hardcoding assumptions that a real auth system would later have to unwind.

### 10.3 Assumed vs. built — summary table

| | Assumed (mocked for v1) | Built in v1 |
|---|---|---|
| A caller identity exists for creation requests | Yes — no real login proves it | Use case *consumes* an identity value |
| That identity is trustworthy/verified | Yes — no credential validation performed | — |
| Identity is recorded for audit/ownership | — | `CreatedBy`, ownership model (Q3) |
| Identity drives rate-limit tier | — | Per-caller partitioning (§5) |
| Role/permission enforcement on that identity | Not built (Q4, Q6) | — |
| Redirect/consumption gating by identity | N/A — redirect is ungated by design (Q1) | — |

---

## 11. Summary of Exceptions

Consolidated for visibility (each is also documented in place above):

1. **§4.3** — the automated malicious/phishing check has inherent coverage gaps (no manual review, no abuse reporting in v1); this is a confirmed product-owner scope trade-off, not an oversight.
2. **§5.2** — rate-limit and creation-ceiling numeric values are placeholders pending real usage data; the policy *shape* is the stable v1 decision.
3. **§10.2** — the authentication/authorization mechanism is mocked for v1; only the *requirement* that creation have an authenticated context is real. Design elements that depend on identity (rate-limit tiering, audit fields, ownership model) are built against the identity abstraction so they don't require rework when real auth lands.
