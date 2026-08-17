# Exception & Logging Strategy — MVP (v3)

**Status:** As-built. Documents the Serilog wiring, the exception-type hierarchy, the
exception → HTTP status mapping, and the edge cases now explicitly handled and tested.
**Companion docs:** `api-design.md` (endpoints), `api-project-structure.md` (project
layout/layering), `documentation/02-design/v1/design/nfr-security.md` §6 (PII-safe
logging rule this design follows).

---

## 1. Serilog setup

- **Packages** (`UrlShortener.Api.csproj`): `Serilog.AspNetCore` 9.0.0, `Serilog.Sinks.Console`
  6.0.0, `Serilog.Settings.Configuration` 9.0.0 (pinned to the `9.x` line, not the newest
  `10.x`, so the transitive `Microsoft.Extensions.*` versions stay aligned with this
  project's `net9.0`/ASP.NET Core 9 target instead of pulling in .NET 10 abstractions).
- **Wiring** (`Program.cs`): `builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration.ReadFrom.Configuration(context.Configuration).ReadFrom.Services(services).Enrich.FromLogContext())`
  replaces the default `Microsoft.Extensions.Logging` provider entirely -- every
  `ILogger<T>` in the app (framework or application code) is now backed by Serilog.
  `app.UseSerilogRequestLogging()` is registered right after `app.UseExceptionHandler()`
  so every request gets a structured one-line summary (method, path, status, elapsed ms)
  even when `GlobalExceptionHandler` has translated an exception into a non-2xx response.
- **Configuration** (`appsettings.json`, `Serilog` section, read via `Serilog.Settings.Configuration`):
  - Sink: Console, with a structured `outputTemplate` that renders each log's named
    properties (`{Properties:j}`) alongside the message -- e.g. `{ShortCode}`,
    `{ExceptionType}` -- so log lines are `grep`/`jq`-able, not free-form text.
  - `MinimumLevel.Default`: `Information` (`Debug` in `appsettings.Development.json`).
  - `MinimumLevel.Override`: `Microsoft`, `Microsoft.AspNetCore`, and `System` all at
    `Warning`, so framework-internal chatter (routing, DI, Kestrel connection noise)
    doesn't drown out the application's own events.
  - `Enrich.FromLogContext` + a static `Application: UrlShortener.Api` property on every
    log line.
  - Extension point (documented, not built -- consistent with this project's "deferred
    with a pointer" convention): swapping the Console sink's plain-text `outputTemplate`
    for `Serilog.Formatting.Compact`'s `CompactJsonFormatter` is a one-line, zero-code
    change if a log-aggregation pipeline later needs literal JSON lines instead of a
    human-readable template; not added now since the minimum required sink set didn't
    call for the extra package.

## 2. What gets logged, and where

| Event | Where | Level | Structured properties |
|---|---|---|---|
| Short URL created | `ShortUrlService.CreateAsync` | Information | `{ShortCode}` -- **never** the original URL or any caller-identifying data (IP, etc.), per nfr-security.md §6 |
| Create failed validation (missing/malformed/disallowed-scheme/too-long URL) | `ShortUrlService.ValidateAndNormalizeUrl` | Warning | `{Reason}`, or `{Scheme}` / `{Length}`/`{MaxLength}` depending on which rule failed |
| Short-code collision, retrying | `ShortUrlService.ResolveSystemGeneratedCodeAsync` | Warning | `{Attempt}`, `{MaxAttempts}` -- never the colliding code's original URL |
| Short-code retry budget exhausted (terminal) | `GlobalExceptionHandler` (as the thrown `ShortCodeGenerationException` propagates) | Error | full exception + `{ExceptionType}`, `{Path}` |
| Fetch resulted in redirect | `ShortUrlResolverService.ResolveAsync` | Information | `{ShortCode}` only -- never the destination URL |
| Fetch resulted in not-found | `ShortUrlResolverService.ResolveAsync` | Information | `{ShortCode}` |
| Fetch with an empty/malformed code segment | `ShortUrlResolverService.ResolveAsync` (short-circuit guard) | Warning | (no code logged -- there is nothing meaningful to log) |
| Fetch resulted in expired/deactivated | **Not implemented** -- `ShortUrlResolutionStatus` has no `Expired` member in this MVP (see api-design.md §4, "Optional expiration" / "Deactivation"). A code comment at the exact point marks where the log call and status branch would go once that feature exists. | -- | -- |
| Any unhandled/unexpected exception | `GlobalExceptionHandler.TryHandleAsync` | Error (500s) / Warning (400s) | full exception + `{ExceptionType}`, `{Path}` |
| Every HTTP request (method, path, status, elapsed ms) | `Serilog.AspNetCore.RequestLoggingMiddleware` (`UseSerilogRequestLogging()`) | Information | `{RequestPath}`, `{StatusCode}`, `{Elapsed}` |

**PII-safe by construction (nfr-security.md §6):** no log statement anywhere in the
application takes a raw client IP, the full original/destination URL, or any other
caller-identifying value as a logged property -- only the short code (a public,
non-identifying token) and validation-failure metadata are logged. `RequestLoggingMiddleware`'s
default enrichers do not log the remote IP either.

## 3. Exception types

`ValidationAppException` (`UrlShortener.Domain.Exceptions`) is now an unsealed base type
with one distinct subclass per real failure reason, instead of one generic exception
doing double duty for every validation rule:

| Type | Failure reason | Base |
|---|---|---|
| `MissingUrlException` | Original URL is null/empty/whitespace-only | `ValidationAppException` |
| `MalformedUrlException` | Original URL does not parse as an absolute URI | `ValidationAppException` |
| `UnsupportedUrlSchemeException` | Scheme is not `http`/`https` (e.g. `javascript:`, `ftp:`, `data:`) | `ValidationAppException` |
| `UrlTooLongException` | Original URL exceeds `UrlValidationConstants.MaxOriginalUrlLength` (2048) | `ValidationAppException` |
| `ShortCodeGenerationException` | Collision-retry budget (5 attempts) exhausted (AF-04) | `Exception` (sealed, single-purpose already) |

All four validation subclasses carry the same `FieldName` (always `OriginalUrl` in this
MVP) that `ValidationAppException` already exposed, so `GlobalExceptionHandler`'s existing
`ValidationAppException { FieldName: { } fieldName }` pattern match keeps mapping every
one of them to a `ValidationProblemDetails` response without a case per subtype
(Open/Closed) -- see §4.

**Not-found is deliberately NOT an exception.** Per `coding-guidelines.md` §6 ("prefer a
return code/Result pattern... for expected failure paths that are part of normal control
flow"), an unknown/removed short code is an expected outcome of a redirect lookup, not an
exceptional one -- `ShortUrlResolverService.ResolveAsync` returns
`ShortUrlResolutionResult(ShortUrlResolutionStatus.NotFound, null)` and
`RedirectController` maps that directly to `404 Not Found`, with no exception thrown or
caught anywhere on that path.

## 4. Exception → HTTP status mapping (`GlobalExceptionHandler`)

| Exception / outcome | HTTP status | Response body |
|---|---|---|
| `ValidationAppException` (any subclass: `MissingUrlException`, `MalformedUrlException`, `UnsupportedUrlSchemeException`, `UrlTooLongException`) | `400 Bad Request` | `ValidationProblemDetails` with the field name and the exception's own (client-safe) message |
| `ShortUrlResolutionStatus.NotFound` (not an exception -- see §3) | `404 Not Found` | Empty body / default `ProblemDetails` from `[ApiController]` + `AddProblemDetails()` |
| `ShortCodeGenerationException` | `500 Internal Server Error` | `ProblemDetails` with a generic, client-safe `Detail` ("The server could not generate a unique short code at this time...") -- never the internal exception message |
| Any other/unexpected exception | `500 Internal Server Error` | `ProblemDetails` with a generic, client-safe `Detail` ("An unexpected error occurred...") -- never the internal exception message or stack trace |
| `409 Conflict` | **Not reachable in this MVP** -- see §5 | -- |

Every 500-class response's `ProblemDetails.Detail` is now a fixed, safe string rather
than `exception.Message` (a change from the pre-hardening version of this handler, which
echoed `exception.Message` verbatim for every status including unexpected 500s). The full
exception -- message and stack trace -- is still logged server-side (§2), just never
placed in the response body, per `coding-guidelines.md` §6 and this handler's own
long-standing contract for the two exception types it always knew about.

## 5. Edge cases explicitly handled (with tests)

| Edge case | Handling | Test(s) |
|---|---|---|
| Null/empty submitted URL | `[Required]` on `CreateShortUrlRequest.OriginalUrl` short-circuits via MVC model validation for the empty-string/whitespace-at-the-wire case; `ShortUrlService.ValidateAndNormalizeUrl` throws `MissingUrlException` as the Application-layer's own defense-in-depth check (matches nfr-security.md §3 "the validator is the authoritative boundary, not a substitute") | `ShortUrlServiceTests.CreateAsync_WithNullOrEmptyUrl_ThrowsMissingUrlException` (`[Theory]`: `null`, `""`, `"   "`); `ShortUrlsEndpointTests.CreateShortUrl_WithMissingUrl_ReturnsValidationProblemDetails` |
| URL with a disallowed scheme (`javascript:`, `ftp:`, etc.) | `UnsupportedUrlSchemeException` | `ShortUrlServiceTests.CreateAsync_WithNonHttpScheme_ThrowsUnsupportedUrlSchemeException` (`ftp:`), `CreateAsync_WithJavaScriptScheme_ThrowsUnsupportedUrlSchemeException`; `ShortUrlsEndpointTests.CreateShortUrl_WithDisallowedScheme_ReturnsValidationProblemDetails` |
| URL exceeding max length | `UrlTooLongException` | `ShortUrlServiceTests.CreateAsync_WithUrlExceedingMaxLength_ThrowsUrlTooLongException`; `ShortUrlsEndpointTests.CreateShortUrl_WithUrlExceedingMaxLength_ReturnsValidationProblemDetails` |
| Short-code generation exhausting its retry budget | `ShortCodeGenerationException` -> `500` `ProblemDetails` with a safe, generic `Detail` -- a clean 500-class error, not an unhandled exception/crash | `ShortUrlServiceTests.CreateAsync_WhenEveryCandidateCollides_ThrowsShortCodeGenerationException` |
| Fetching a short code that doesn't exist | `ShortUrlResolutionStatus.NotFound` -> `404` | `ShortUrlResolverServiceTests.ResolveAsync_WithUnknownCode_ReturnsNotFound`; `ShortUrlsEndpointTests.Fetch_WithUnknownCode_ReturnsNotFound` |
| Fetching with an empty/malformed short-code segment | `ShortUrlResolverService.ResolveAsync` short-circuits null/whitespace codes to `NotFound` without a cache/DB round trip; a non-empty-but-nonsensical code (outside the generator's alphabet) resolves as a normal not-found lookup | `ShortUrlResolverServiceTests.ResolveAsync_WithEmptyOrWhitespaceCode_ReturnsNotFoundWithoutQueryingCacheOrRepository` (`[Theory]`: `""`, `"   "`); `ShortUrlsEndpointTests.Fetch_WithEmptyCodeSegment_ReturnsNotFound`, `Fetch_WithMalformedCodeSegment_ReturnsNotFound` |
| Concurrent requests creating the same **custom** short code | **Not reachable in this MVP** -- there is no custom-alias feature (see api-design.md §4); every code is system-generated and collision-checked via the retry loop above. Skipped per the task's own "if not reachable, skip it" instruction. | -- |

## 6. A note on the concurrent-insert race for system-generated codes

`ResolveSystemGeneratedCodeAsync`'s collision check (`ExistsByCodeAsync`) and the
subsequent `SaveChangesAsync` insert are two separate round trips, so two concurrent
requests can in theory both pass the existence check for the same randomly-generated
candidate before either has inserted (a classic check-then-act race). If that ever
happens, `IX_ShortUrl_Code`'s unique constraint (`db-design.md` §3) rejects the second
insert as a `DbUpdateException`, which this MVP does not special-case -- it falls through
to the generic `500` fallback in §4, a clean (if generic) error rather than an unhandled
crash. Given the collision-probability math already cited in `db-design.md` §5 (62^7
possible codes), the probability of two concurrent requests generating the *same* random
candidate within the same narrow race window is negligible at this MVP's scale; retrying
the whole `CreateAsync` call with a fresh candidate on `DbUpdateException` would close
this gap completely and is the documented upgrade path if/when real concurrent write
volume makes it worth the extra complexity.
