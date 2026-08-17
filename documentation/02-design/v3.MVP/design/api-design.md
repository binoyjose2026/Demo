# API Design — MVP (v3)

**Status:** As-built. Documents the two actual endpoints implemented in `UrlShortner.Api`, not an aspirational design.
**Scope:** Create + fetch/redirect only — see `documentation/02-design/v3.MVP/agents/mvp-design@agent.md`. No custom alias, no expiration, no analytics/click-tracking, no metadata endpoint, no auth, no rate limiting, no moderation check, no real caching. Each omission is a documented, deliberate deferral (see §4) with a matching code comment at the point it would plug in — not a silent gap.
**Companion docs:** `db-design.md` (schema), `api-project-structure.md` (project layout), `exception-and-logging-strategy.md` (Serilog setup, the exception-type hierarchy, the full exception -> HTTP status mapping, and the edge cases now explicitly handled with tests). Full pre-MVP designs this is trimmed from: `documentation/02-design/v1/design/fn-create.md`, `fn-fetch.md`.

---

## 1. `POST /api/short-urls` — Create (AF-01, AF-03, AF-04)

Implemented in `UrlShortner.Api.Controllers.ShortUrlsController.CreateAsync`.

### Request

```http
POST /api/short-urls
Content-Type: application/json

{
  "originalUrl": "https://example.com/some/very/long/path?query=1"
}
```

`CreateShortUrlRequest` — one required field, `OriginalUrl` (`string`).

### Validation (in order; first failure wins)

| # | Rule | Requirement |
|---|---|---|
| 1 | Non-empty, well-formed absolute URI (`Uri.TryCreate(..., UriKind.Absolute, ...)`) | AF-03 |
| 2 | Scheme is `http` or `https` (case-insensitive) | Q14, ANFR-07 |
| 3 | Length ≤ 2048 characters (`UrlValidationConstants.MaxOriginalUrlLength`) | Q14 |

A failure at any step throws a distinct `UrlShortner.Domain.Exceptions.ValidationAppException` subclass -- `MissingUrlException`, `MalformedUrlException`, or `UrlTooLongException` for rules 1-3 respectively (each one a specific, single-purpose type rather than one generic exception doing double duty) -- translated by `GlobalExceptionHandler` into a `400` `ValidationProblemDetails` response. See `exception-and-logging-strategy.md` §3-4 for the full exception hierarchy and status mapping, including `UnsupportedUrlSchemeException` (rule 2's disallowed-scheme case, e.g. `javascript:`/`ftp:`).

### Short-code generation (AF-04, ANFR-08)

Random 7-character base62 candidate (`RandomBase62ShortCodeGenerator`), checked against `IShortUrlRepository.ExistsByCodeAsync` (backed by the unique `IX_ShortUrl_Code` index), retried up to 5 attempts; each collision is logged (`ShortUrlService`, Warning level). Exhausting the retry budget throws `ShortCodeGenerationException` → `500` with a generic, client-safe `ProblemDetails.Detail` (never the internal exception message). See `db-design.md` §5 for why this is the v1 approach, not the v2 extreme-scale one, and `exception-and-logging-strategy.md` §6 for the documented (currently out-of-scope) concurrent-insert race.

### Response — `201 Created`

```json
{
  "shortUrl": "https://short.ly/3m7HuFm",
  "code": "3m7HuFm",
  "originalUrl": "https://www.anthropic.com/some/very/long/path?x=1",
  "createdAtUtc": "2026-08-17T18:15:54.5063697Z"
}
```

`Location` header is set to `/{code}` (the redirect endpoint — see §5 for why there is no metadata endpoint to point at instead).

### Status codes

| Status | When |
|---|---|
| `201 Created` | Short URL created. |
| `400 Bad Request` (`ValidationProblemDetails`) | Missing/malformed JSON, or any validation rule in the table above fails. |
| `500 Internal Server Error` (`ProblemDetails`) | Short-code retry budget exhausted (expected to be effectively unreachable — see `fn-create.md` §6). |

## 2. `GET /{code}` — Fetch / Redirect (AF-02, AF-06)

Implemented in `UrlShortner.Api.Controllers.RedirectController.RedirectAsync`.

**Design exception (per `fn-fetch.md` §3, carried into this MVP unchanged):** this route lives at the application root, not under `/api/...`, because the whole point of a short link is a short path.

### Request

```http
GET /{code}
```

### Behavior

0. An empty/whitespace-only `code` segment short-circuits to not-found immediately (logged at Warning), without a cache/DB round trip.
1. `IShortUrlResolverService.ResolveAsync(code)` checks the (no-op) cache, then `IShortUrlRepository.GetByCodeAsync(code)`.
2. Found → `302 Found` with `Location: <OriginalUrl>`, logged at Information (short code only, never the destination URL, per `exception-and-logging-strategy.md` §2). **302, not 301** — deliberately, so every request re-hits the server rather than being cached client-side; see `fn-fetch.md` §10 for the full rationale (matters most once expiry/deactivation/analytics exist, all out of scope for this MVP, but the 302 choice is made now so it doesn't need revisiting later).
3. Not found → `404 Not Found`, logged at Information (short code only). `[ApiController]` + `AddProblemDetails()` shape this as `application/problem+json` automatically.

### Status codes

| Status | When |
|---|---|
| `302 Found` | Code resolved; `Location` header holds the original URL. |
| `404 Not Found` | Code does not exist (no distinction is made from "existed and was later removed" — that lifecycle state doesn't exist in this MVP; see §4). |

### Verified manually against the running app + real SQLite file

```
POST /api/short-urls {"originalUrl":"https://www.anthropic.com/some/very/long/path?x=1"}
  -> 201, code "3m7HuFm"
GET /3m7HuFm
  -> 302, Location: https://www.anthropic.com/some/very/long/path?x=1
GET /doesnotexist999
  -> 404
```

## 3. Standard error shape

Every non-2xx response is `ProblemDetails`/`ValidationProblemDetails` (`application/problem+json`), per `UrlShortner/global/guidelines/design-guidelines.md` §3 — produced by `AddProblemDetails()` for framework-level failures (bad routing, model binding) and by `GlobalExceptionHandler` (an `IExceptionHandler`) for `ValidationAppException` (and its subclasses)/`ShortCodeGenerationException`/any other unhandled exception. See `exception-and-logging-strategy.md` for the full exception-type hierarchy, the exception → HTTP status mapping, what gets logged where (Serilog), and the edge cases now explicitly handled with tests.

## 4. Deliberately out of scope for this MVP (documented, with a pointer to the full design)

| Feature | Where the comment lives in code | Full design |
|---|---|---|
| Authenticated user context (`CreatedBy` is the literal placeholder `"system"`) | `ShortUrlService.CreateAsync`, guard-clause comment block | `fn-create.md` §4 (Q1/Q2) |
| Usage ceiling / per-caller quota | `ShortUrlService.CreateAsync`, guard-clause comment block | `fn-create.md` §10, `nfr-security.md` |
| Malicious/phishing domain check | `ShortUrlService.CreateAsync`, guard-clause comment block | `fn-create.md` §9, `nfr-security.md` |
| Custom/vanity alias | `CreateShortUrlRequest.cs`, `ShortUrlService.CreateAsync` | `fn-create.md` §7 |
| Optional expiration | `CreateShortUrlRequest.cs`, `ShortUrlResolutionStatus.cs`, `ShortUrlResolverService.cs` | `fn-create.md` §8, `fn-fetch.md` §7.1 |
| Metadata retrieval endpoint (`GET /api/short-urls/{code}`) | `ShortUrlsController.CreateAsync` (Location-header comment) | `fn-fetch.md` §9 (AF-05) |
| Analytics / click tracking | `ShortUrlResolverService.ResolveAsync` | `fn-analytics.md` (AF-08/09/10) |
| Deactivation / removal | `ShortUrl.cs`, `ShortUrlResolverService.cs` | `fn-fetch.md` §7.2 (AF-07) |
| Rate limiting on create | `Program.cs`, before `AddInfrastructureServices` | `fn-create.md` §10, `nfr-security.md` (ANFR-09) |
| Real caching (Redis) | `IShortUrlCache` / `NullShortUrlCache.cs` — the literal NULL placeholder this MVP asked for | `documentation/02-design/v2/design/considerations/07-redis-caching-and-invalidation.md` |
| Extreme-scale short-code generation (pre-allocated ID blocks) | `IShortCodeGenerator.cs` | `documentation/02-design/v2/design/considerations/01-create-path-extreme-scalability.md`, `.../28-short-key-generation-approaches.md` |
| `409 Conflict` on concurrent same-code creation | N/A — unreachable without a custom-alias feature; the concurrent-insert race for system-generated codes falls to the generic `500` fallback instead (documented, not silently ignored) | `exception-and-logging-strategy.md` §5-6 |

## 5. Testing

- **Unit** (`UrlShortner.Application.Tests`, Moq + xUnit): `ShortUrlServiceTests` (valid create; missing/malformed/disallowed-scheme (`ftp:`, `javascript:`)/over-length URL, each asserting its specific exception type; collision retry succeeds; retry budget exhausted) and `ShortUrlResolverServiceTests` (resolved, not-found, empty/whitespace code short-circuits without querying cache/repository, cache-write-on-miss). 15 tests, all passing.
- **Integration** (`UrlShortner.IntegrationTests`, `WebApplicationFactory<Program>` + private in-memory SQLite): create → fetch/redirect happy path; missing/malformed/disallowed-scheme/over-length URL → `ValidationProblemDetails`; unknown code, empty code segment, and malformed code segment → `404`. 8 tests, all passing.
- See `exception-and-logging-strategy.md` §5 for the full edge-case-to-test mapping.
