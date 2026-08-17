# API Design — MVP (v3)

**Status:** As-built. Documents the two actual endpoints implemented in `UrlShortener.Api`, not an aspirational design.
**Scope:** Create + fetch/redirect only — see `documentation/02-design/v3-mvp/agents/agent-prompt.md`. No custom alias, no expiration, no analytics/click-tracking, no metadata endpoint, no moderation check, no real caching/event broker. Each omission is a documented, deliberate deferral (see §4) with a matching code comment at the point it would plug in — not a silent gap. **Post-hardening update (§6):** auth and rate limiting are no longer un-wired placeholders in code — real ASP.NET Core primitives (`[Authorize]`, `[EnableRateLimiting]`) are now on the create action, backed by an intentionally permissive/no-op policy each; they remain "not really enforcing anything" in spirit (an effectively-unlimited rate limit, an always-succeeding auth policy), just no longer decorative.
**Companion docs:** `db-design.md` (schema), `api-project-structure.md` (project layout), `exception-and-logging-strategy.md` (Serilog setup, the exception-type hierarchy, the full exception -> HTTP status mapping, and the edge cases now explicitly handled with tests). Full pre-MVP designs this is trimmed from: `documentation/02-design/v1/design/fn-create.md`, `fn-fetch.md`.

---

## 1. `POST /api/v1/short-urls` — Create (AF-01, AF-03, AF-04)

Implemented in `UrlShortener.Api.Controllers.ShortUrlsController.CreateAsync`.

**Post-hardening update:** the route is now versioned (`/api/v1/short-urls`, was `/api/short-urls`) — see §6.7. The action also now carries `[EnableRateLimiting("CreatePolicy")]`, `[Authorize(Policy = "Mvp-Bypass")]`, and an `Idempotency-Key`-reading `IdempotencyKeyFilter` — see §6.3-6.5 for what each actually does (all three are intentionally permissive/no-op placeholders, not enforcement).

### Request

```http
POST /api/v1/short-urls
Content-Type: application/json
Idempotency-Key: 5f8c1e2a-9b3d-4e7f-9a1c-2d6b7e4f0a11

{
  "originalUrl": "https://example.com/some/very/long/path?query=1"
}
```

`Idempotency-Key` is optional and, for this MVP, observed/logged only — see §6.5.

`CreateShortUrlRequest` — one required field, `OriginalUrl` (`string`).

### Validation (in order; first failure wins)

| # | Rule | Requirement |
|---|---|---|
| 1 | Non-empty, well-formed absolute URI (`Uri.TryCreate(..., UriKind.Absolute, ...)`) | AF-03 |
| 2 | Scheme is `http` or `https` (case-insensitive) | Q14, ANFR-07 |
| 3 | Length ≤ 2048 characters (`UrlValidationConstants.MaxOriginalUrlLength`) | Q14 |

A failure at any step throws a distinct `UrlShortener.Domain.Exceptions.ValidationAppException` subclass -- `MissingUrlException`, `MalformedUrlException`, or `UrlTooLongException` for rules 1-3 respectively (each one a specific, single-purpose type rather than one generic exception doing double duty) -- translated by `GlobalExceptionHandler` into a `400` `ValidationProblemDetails` response. See `exception-and-logging-strategy.md` §3-4 for the full exception hierarchy and status mapping, including `UnsupportedUrlSchemeException` (rule 2's disallowed-scheme case, e.g. `javascript:`/`ftp:`).

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

Implemented in `UrlShortener.Api.Controllers.RedirectController.RedirectAsync`.

**Design exception (per `fn-fetch.md` §3, carried into this MVP unchanged):** this route lives at the application root, not under `/api/...`, because the whole point of a short link is a short path.

### Request

```http
GET /{code}
```

### Behavior

0. **Post-hardening addition:** a `code` segment containing any character outside the base62 alphabet (`RandomBase62ShortCodeGenerator`'s own alphabet, now also exposed as `UrlShortener.Domain.ShortUrls.ShortCodeValidationConstants`) is rejected with `400 Bad Request` immediately, before any cache/DB round trip — see §6.6. This runs first, in `RedirectController` itself.
0a. An empty/whitespace-only `code` segment short-circuits to not-found immediately (logged at Warning), without a cache/DB round trip. (Unreachable via this route today — `[HttpGet("/{code}")]` requires a non-empty segment — but still exercised directly by `ShortUrlResolverServiceTests`.)
1. `IShortUrlResolverService.ResolveAsync(code)` checks the (no-op) cache, then `IShortUrlRepository.GetByCodeAsync(code)`.
2. Found → `302 Found` with `Location: <OriginalUrl>`, logged at Information (short code only, never the destination URL, per `exception-and-logging-strategy.md` §2). **302, not 301** — deliberately, so every request re-hits the server rather than being cached client-side; see `fn-fetch.md` §10 for the full rationale (matters most once expiry/deactivation/analytics exist, all out of scope for this MVP, but the 302 choice is made now so it doesn't need revisiting later).
3. Not found → `404 Not Found`, logged at Information (short code only). `[ApiController]` + `AddProblemDetails()` shape this as `application/problem+json` automatically.

### Status codes

| Status | When |
|---|---|
| `302 Found` | Code resolved; `Location` header holds the original URL. |
| `400 Bad Request` | **Post-hardening addition (§6.6):** the code segment contains characters outside the base62 alphabet — rejected before any repository/DB lookup. |
| `404 Not Found` | Code does not exist (no distinction is made from "existed and was later removed" — that lifecycle state doesn't exist in this MVP; see §4). |

### Verified manually against the running app + real SQLite file

```
POST /api/v1/short-urls {"originalUrl":"https://www.anthropic.com/some/very/long/path?x=1"}
  -> 201, code "3m7HuFm"
GET /3m7HuFm
  -> 302, Location: https://www.anthropic.com/some/very/long/path?x=1
GET /doesnotexist999
  -> 404
GET /not-a-real-code!
  -> 400 (post-hardening addition, §6.6 -- rejected before any DB lookup)
```

## 3. Standard error shape

Every non-2xx response is `ProblemDetails`/`ValidationProblemDetails` (`application/problem+json`), per `UrlShortener/engineering-standards/guidelines/design-guidelines.md` §3 — produced by `AddProblemDetails()` for framework-level failures (bad routing, model binding) and by `GlobalExceptionHandler` (an `IExceptionHandler`) for `ValidationAppException` (and its subclasses)/`ShortCodeGenerationException`/any other unhandled exception. See `exception-and-logging-strategy.md` for the full exception-type hierarchy, the exception → HTTP status mapping, what gets logged where (Serilog), and the edge cases now explicitly handled with tests.

## 4. Deliberately out of scope for this MVP (documented, with a pointer to the full design)

| Feature | Where the comment lives in code | Full design |
|---|---|---|
| Authenticated user context (`CreatedBy` is the literal placeholder `"system"`) | `ShortUrlService.CreateAsync`, guard-clause comment block | `fn-create.md` §4 (Q1/Q2) |
| Usage ceiling / per-caller quota | `ShortUrlService.CreateAsync`, guard-clause comment block | `fn-create.md` §10, `nfr-security.md` |
| Malicious/phishing domain check | `ShortUrlService.CreateAsync`, guard-clause comment block | `fn-create.md` §9, `nfr-security.md` |
| Custom/vanity alias | `CreateShortUrlRequest.cs`, `ShortUrlService.CreateAsync` | `fn-create.md` §7 |
| Optional expiration | `CreateShortUrlRequest.cs`, `ShortUrlResolutionStatus.cs`, `ShortUrlResolverService.cs` | `fn-create.md` §8, `fn-fetch.md` §7.1 |
| Metadata retrieval endpoint (`GET /api/v1/short-urls/{code}`) | `ShortUrlsController.CreateAsync` (Location-header comment) | `fn-fetch.md` §9 (AF-05) |
| Analytics / click tracking | `ShortUrlResolverService.ResolveAsync` | `fn-analytics.md` (AF-08/09/10) |
| Deactivation / removal | `ShortUrl.cs`, `ShortUrlResolverService.cs` | `fn-fetch.md` §7.2 (AF-07) |
| Real caching (Redis) | `IShortUrlCache` / `NullShortUrlCache.cs` — the literal NULL placeholder this MVP asked for | `documentation/02-design/v2/design/considerations/07-redis-caching-and-invalidation.md` |
| Extreme-scale short-code generation (pre-allocated ID blocks) | `IShortCodeGenerator.cs` | `documentation/02-design/v2/design/considerations/01-create-path-extreme-scalability.md`, `.../28-short-key-generation-approaches.md` |
| `409 Conflict` on concurrent same-code creation | N/A — unreachable without a custom-alias feature; the concurrent-insert race for system-generated codes falls to the generic `500` fallback instead (documented, not silently ignored) | `exception-and-logging-strategy.md` §5-6 |

**No longer in this table (moved to §6 — now wired, still permissive placeholders, not full implementations):** rate limiting on create, authenticated user context, idempotency-key handling, Kafka event publishing. Still fully out of scope, unchanged: the malicious/phishing domain check and the metadata retrieval endpoint above remain undeferred-to-code — no seam exists for either yet.

## 5. Testing

- **Unit** (`UrlShortener.Application.Tests`, Moq + xUnit): `ShortUrlServiceTests` (valid create; missing/malformed/disallowed-scheme (`ftp:`, `javascript:`)/over-length URL, each asserting its specific exception type; collision retry succeeds; retry budget exhausted) and `ShortUrlResolverServiceTests` (resolved, not-found, empty/whitespace code short-circuits without querying cache/repository, cache-write-on-miss). 15 tests, all passing.
- **Integration** (`UrlShortener.IntegrationTests`, `WebApplicationFactory<Program>` + private in-memory SQLite): create → fetch/redirect happy path; missing/malformed/disallowed-scheme/over-length URL → `ValidationProblemDetails`; unknown/valid-format code, empty code segment, and malformed (invalid-alphabet) code segment → `404`/`400`; plus a dedicated `RowVersion` optimistic-concurrency test against `AppDbContext` directly (item 17, no update endpoint exists in this MVP to exercise it through the API). 10 tests, all passing.
- See `exception-and-logging-strategy.md` §5 for the full edge-case-to-test mapping.

## 6. Post-MVP hardening additions

The sections above describe the original create/fetch-only MVP surface. This section documents a batch of production-hardening additions layered on top, without changing that core surface's business behavior (validation rules, short-code generation, the exception hierarchy). See `api-project-structure.md` §6 for where each one lives in the solution.

### 6.1 Swagger/OpenAPI

`Swashbuckle.AspNetCore` (pinned to the 6.x line — the 7.x+/10.x lines moved `OpenApiInfo` to a different `Microsoft.OpenApi` namespace shape not worth chasing for this MVP). XML doc comments (`UrlShortener.Api.csproj`'s `GenerateDocumentationFile`) feed Swagger's operation/schema descriptions from the controllers' real `<summary>`/`<param>`/`<response>` comments. UI mapped only in `Development`, at `/swagger`.

### 6.2 Health checks, request logging, OpenTelemetry

- `GET /health/live` / `GET /health/ready` — exactly the design already specified in `documentation/02-design/v1/design/nfr-reliability-and-availability.md` §4 (liveness has no dependency checks; readiness runs `AddDbContextCheck<AppDbContext>()`). Unversioned, per that document's own placement rule.
- `app.UseSerilogRequestLogging()` was already wired pre-hardening (see `exception-and-logging-strategy.md`); unchanged here except that it now runs after `CorrelationIdMiddleware` (§6.8), so its summary line also carries `{CorrelationId}`.
- Basic OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `.Instrumentation.AspNetCore`, `.Instrumentation.EntityFrameworkCore`, `.Exporter.Console`) — traces + metrics, console-exported. A v1/MVP-appropriate seam, not the v2 observability stack: `documentation/02-design/v2/design/considerations/13-observability-at-scale.md` covers the real OTEL-collector/Grafana/Loki/Tempo target this would export to in production.

### 6.3 Rate limiting placeholder

`[EnableRateLimiting("CreatePolicy")]` on `ShortUrlsController.CreateAsync`, backed by a `Microsoft.AspNetCore.RateLimiting` fixed-window policy configured at `PermitLimit = int.MaxValue` in `Program.cs` — a real primitive, wired end-to-end, but effectively unlimited. Real policy (sliding-window, Redis-backed counters shared across horizontally-scaled instances): `documentation/02-design/v2/design/considerations/12-distributed-rate-limiting.md`.

### 6.4 Authorization placeholder

`[Authorize(Policy = "Mvp-Bypass")]` on the same action. The policy (`RequireAssertion(_ => true)`) always succeeds; a minimal `MvpPlaceholderAuthenticationHandler` authentication scheme always authenticates the caller as one fixed placeholder identity so `[Authorize]` doesn't 401 outright. See `documentation/02-design/v1/design/nfr-security.md` §10's "Assumed vs. built" table — this is the code-level realization of that already-documented scope boundary, not a new decision.

### 6.5 Idempotency-Key placeholder

`IdempotencyKeyFilter` (an `IAsyncActionFilter`) reads the `Idempotency-Key` header if present and logs it — no deduplication happens. Real design (Redis-backed dedup store, cached-response replay): `documentation/02-design/v2/design/considerations/11-idempotency-keys.md`.

### 6.6 Short-code format validation on redirect

`RedirectController.RedirectAsync` now rejects a `code` segment containing any character outside the base62 alphabet (`UrlShortener.Domain.ShortUrls.ShortCodeValidationConstants`) with `400 Bad Request`, before any cache/DB round trip — see §2 above.

### 6.7 API versioning

`Asp.Versioning.Mvc`/`.Mvc.ApiExplorer`. `ShortUrlsController` carries `[ApiVersion("1.0")]` and routes under `api/v{version:apiVersion}/short-urls` (today: `api/v1/short-urls`). `RedirectController`'s `GET /{code}` deliberately stays unversioned — it's a public short-link surface, not a versioned API contract a client negotiates against.

### 6.8 Correlation ID

`CorrelationIdMiddleware` reads an inbound `X-Correlation-Id` header or generates one, pushes it into the Serilog `LogContext` for the request, echoes it back as a response header, and stashes it on `HttpContext.Items` so `GlobalExceptionHandler` can add it to every `ProblemDetails`/`ValidationProblemDetails` response as a `traceId` extension field.

### 6.9 Security response headers

`SecurityResponseHeadersMiddleware` adds `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`. `app.UseHsts()` (previously not called at all) is now wired for non-Development environments, per `nfr-security.md` §8.

### 6.10 NullKafka event publisher seam

`IShortUrlEventPublisher` (`UrlShortener.Domain.ShortUrls`) / `NullShortUrlEventPublisher` (`UrlShortener.Infrastructure.Messaging`) mirror the `IShortUrlCache`/`NullShortUrlCache` pattern exactly. `ShortUrlService.CreateAsync` calls `PublishUrlCreatedAsync` fire-and-forget after the entity commits — a publish failure is caught and logged, never surfaced to the caller. The Null implementation contains a commented-out block showing the real `Confluent.Kafka` `IProducer<string,string>.ProduceAsync(...)` call it would make. Full design: `documentation/02-design/v2/design/considerations/05-kafka-comparison.md`.

### 6.11 Startup config validation

`ShortUrlOptions` (`ShortUrl:BaseUrl`) is now bound via `AddOptions<ShortUrlOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` with `[Required]`/`[Url]` attributes — a missing/malformed config value now fails fast at boot instead of producing a malformed short URL the first time `CreateAsync` runs.

### 6.12 `UrlShortener.LoadTests`

A new NBomber-based console project (not an xUnit project — excluded from `dotnet test` automatically) with two scenarios: hammering `POST /api/v1/short-urls` and `GET /{code}`, against a configurable base URL (`LOADTEST_BASE_URL`, default `http://localhost:5236`). Build-only for this task — not run/executed, not wired into CI. See `api-project-structure.md` §6 and the project's own `Program.cs` header comment for why measuring against the v2 extreme-scale numbers would require the actual v2 infrastructure to be meaningful.
