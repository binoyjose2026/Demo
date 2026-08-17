# Resilience Design

**Layer:** Cross-cutting (Api, Application, Infrastructure)
**Status:** v1 — initial design
**Traces to:** ANFR-01 (redirect availability), ANFR-03 (durable persistence, no lost mappings), **ANFR-04** (graceful degradation on backend failure — primary driver of this document), ANFR-10 (observability of errors/latency)

---

## 1. Purpose & Scope

ANFR-04 states: *"The service shall degrade gracefully on backend failure rather than corrupting or losing data."* This document defines how the application behaves when something downstream of the controller fails — the SQLite database, the automated malicious-domain check, or an unhandled programming error — so that failure is always visible, consistent, and non-corrupting to the caller, never silent or partial.

Resilience here means three things, in order of priority:

1. **Never corrupt or partially commit data** (ANFR-03, ANFR-04).
2. **Never leak an unhandled exception, stack trace, or internal detail** to a caller.
3. **Return a consistent, actionable error shape** so API consumers can distinguish "retry me" from "don't retry me."

This document does **not** cover input validation errors (see the per-function design docs) or authentication/authorization (out of scope for v1 per the out-of-scope summary) — it covers what happens when a dependency the request needs is unavailable or slow.

---

## 2. Graceful Degradation on Backend/Database Failure (ANFR-04)

### 2.1 Failure surface

The only "backend" this service has in v1 is the SQLite database reached through EF Core (`AppDbContext`), plus the outbound HTTP call made by the malicious-domain check (Section 4). There is no cache tier, no message queue, and no second data store in v1 — so "backend failure" concretely means:

- The SQLite file is locked (a concurrent writer holds the lock — see the data design guidelines' concurrency trade-off note) and the write times out.
- The SQLite file is unreachable/corrupted/missing (disk/deployment issue).
- A `DbUpdateConcurrencyException` is thrown because `RowVersion` no longer matches (a genuine concurrent-edit conflict, not an infrastructure failure, but handled through the same pipeline).
- The outbound malicious-domain check times out or the remote endpoint is down (Section 4).

### 2.2 The rule: fail atomically, fail loud, never fail silently

- **Every write to `AppDbContext` goes through a single `SaveChangesAsync` call per use case**, via `IUnitOfWork` (per the design guidelines' Unit-of-Work pattern). EF Core wraps that call in an implicit transaction, so a failure partway through a multi-entity write (e.g., create `ShortUrl` + record an audit/analytics row) rolls back completely — SQLite never ends up with half a mapping. This is what makes "no corruption" true by construction rather than by convention.
- **The application layer never catches a database exception to retry the business operation silently and never returns a fabricated success.** If `SaveChangesAsync` throws, the exception propagates unmodified to the global exception-handling middleware (Section 3). Swallowing it and returning `200 OK` without a persisted row would itself be data loss from the caller's point of view — exactly what ANFR-04 forbids.
- **Reads degrade the same way**: if `GetByIdAsync`/`ListAsync` cannot reach the database, the repository lets the exception propagate — there is no fallback cache to serve stale data from in v1 (see Section 6, deferred scope), so "graceful" here means "a clean, typed 503, not a hang or a garbled response," not "silently serve possibly-wrong data."
- **Optimistic concurrency conflicts (`DbUpdateConcurrencyException`)** are treated as an expected, distinguishable failure, not a generic 500: the global exception middleware maps this specific exception type to `409 Conflict` (Section 3.3), consistent with the coding guidelines' preference for specific exception types and the data design guidelines' `RowVersion` concurrency token.

### 2.3 Why no automatic retry against the database in v1

A tempting reflex is "wrap `SaveChangesAsync` in a retry loop." This is deliberately **not** done for the primary write path in v1:

> **Exception (documented trade-off):** Automatic retry-on-write against SQLite is not implemented in v1. SQLite serializes concurrent writers at the file/database level (data design guidelines, Section 1); a transient "database is locked" condition is realistically caused by the *same* single-writer contention EF Core's default busy-timeout already handles at the ADO.NET provider level (`Microsoft.Data.Sqlite` command timeout / `PRAGMA busy_timeout`). Layering an application-level retry on top risks retrying a write whose first attempt actually succeeded just as the timeout fired, which is an idempotency hazard (see Section 5) worse than the transient error it's meant to fix. Configuring `Microsoft.Data.Sqlite`'s busy timeout appropriately (e.g., a few seconds) is the accepted v1 mitigation; a resilience-library retry policy is reserved for genuinely idempotent, network-bound calls (Section 4), not for the local embedded write path.

---

## 3. Global Exception-Handling Middleware

Section 4 of the design guidelines defines `GlobalExceptionHandlingMiddleware` as a **placeholder** with a `// TODO: log ex, write a ProblemDetails response, set status code.` body. This section fills that placeholder in with a concrete design so every unhandled failure — database, moderation-check, or programmer error — produces the same predictable shape.

### 3.1 Contract

- Every non-2xx response the API returns — whether raised by a controller-level `ModelState` failure, an application-layer exception, or an infrastructure exception — is a `ProblemDetails` (or `ValidationProblemDetails`) JSON body, per RFC 7807 and the design guidelines' "standard error shape" rule. No response ever contains a raw stack trace, exception message concatenated from internals, or an empty body with just a status code.
- The middleware sits **first** in the pipeline (per the design guidelines' documented order: exception handling → correlation/logging → authentication → authorization → MVC endpoints), so it can catch anything thrown by every stage after it, including MVC action execution.

### 3.2 Implementation shape

```csharp
/// <summary>
/// Converts unhandled exceptions into a ProblemDetails response and ensures
/// no exception detail is leaked to the caller. Logs the full exception,
/// including correlation ID, before responding.
/// </summary>
public class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;

    public GlobalExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var (statusCode, title) = MapException(ex);

            _logger.LogError(
                ex,
                "Unhandled exception. CorrelationId={CorrelationId} StatusCode={StatusCode}",
                context.TraceIdentifier,
                statusCode);

            var problemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Type = $"https://httpstatuses.io/{statusCode}",
                Instance = context.Request.Path,
            };
            problemDetails.Extensions["correlationId"] = context.TraceIdentifier;

            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(problemDetails);
        }
    }

    private static (int StatusCode, string Title) MapException(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => (StatusCode: 409, "The resource was modified by another request. Reload and retry."),
        DbUpdateException => (StatusCode: 503, "The data store is temporarily unavailable. Please retry."),
        TimeoutException => (StatusCode: 503, "A dependent operation timed out. Please retry."),
        OperationCanceledException => (StatusCode: 499, "The request was cancelled."),
        ArgumentException or InvalidOperationException => (StatusCode: 400, "The request could not be processed."),
        _ => (StatusCode: 500, "An unexpected error occurred."),
    };
}
```

### 3.3 Status-code mapping table

| Exception / condition | HTTP status | Caller guidance |
|---|---|---|
| `DbUpdateConcurrencyException` (RowVersion mismatch) | `409 Conflict` | Re-fetch the resource, reapply the change, retry. |
| `DbUpdateException` / any SQLite connectivity failure | `503 Service Unavailable` | Transient; safe to retry with backoff. |
| `TimeoutException` (DB or moderation-check timeout, Section 4) | `503 Service Unavailable` | Transient; safe to retry with backoff. |
| Malicious-domain check reports the URL is unsafe | `422 Unprocessable Entity` | Not transient — the submission itself is rejected; do not retry unchanged. |
| `ArgumentException` / `InvalidOperationException` (guard-clause failures reaching the middleware) | `400 Bad Request` | Caller error; fix the request. |
| Anything else (unexpected/programmer error) | `500 Internal Server Error` | Not the caller's fault; logged for investigation, no retry guidance implied. |

- This table is the single source of truth for exception-to-status mapping — it lives in one place (`MapException`) rather than being re-implemented per controller, consistent with the coding guidelines' DRY/single-responsibility spirit and the design guidelines' note that only *unexpected* exceptions should reach this top-level handler (expected failure paths like "not found" should already be modelled as a typed result, not an exception, per Section 6 of the coding guidelines).
- `Extensions["correlationId"]` ties every error response back to the correlation-ID/request-logging middleware (design guidelines, Section 4, item 2), so ANFR-10 (observability) and this document reinforce each other: a caller reporting an error can hand support the `correlationId`, and the corresponding server-side log line has the full exception.
- **MVC-level exception filters** (design guidelines, Section 5) are not used to duplicate this mapping — the global middleware is the single authority for exception-to-response translation in v1. The exception-filter placeholder remains reserved for genuinely MVC-pipeline-specific concerns only (e.g., model-binding edge cases), not general error mapping, to avoid two competing places that decide the response shape.

---

## 4. Retry/Timeout Policy for the Automated Moderation (Malicious-Domain) Check

### 4.1 Context

Per the in-scope summary (Section C) and out-of-scope summary (Section C), v1 includes a **minimal automated malicious/phishing-domain check** at link-creation time (Q17/Q18) — no manual review team. That check is expected to call an external service (a domain-reputation/safe-browsing API) from `Infrastructure`, behind a `Domain`/`Application`-defined interface, per the design guidelines' Adapter pattern entry (Section 8: *"external service integrations... isolating third-party SDK shapes to Infrastructure"*).

Any outbound network call is the one place in the create-link flow where "slow" and "down" are real, everyday conditions — unlike the local SQLite file — so it is the one place a resilience *policy* (not just a middleware catch-all) is warranted.

### 4.2 Recommended approach: `Microsoft.Extensions.Resilience` (Polly)

**Recommendation:** use the standard .NET resilience library — `Microsoft.Extensions.Resilience` / `Microsoft.Extensions.Http.Resilience`, which wraps **Polly** — rather than hand-rolling retry loops. This is consistent with the project's general stance of using standard, well-known .NET building blocks (EF Core over hand-written ADO.NET, `IOptions<T>` over raw config lookups) rather than bespoke infrastructure.

Conceptual shape (registered once in `Infrastructure`'s DI extension method, alongside the moderation-check `HttpClient`):

```csharp
// Infrastructure/DependencyInjection.cs
services.AddHttpClient<IMaliciousUrlChecker, ExternalMaliciousUrlChecker>(client =>
    {
        client.BaseAddress = new Uri(configuration["ModerationCheck:BaseUrl"]!);
    })
    .AddResilienceHandler("moderation-check", builder =>
    {
        builder.AddTimeout(TimeSpan.FromSeconds(2));           // per-attempt timeout

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(200),
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is not null ||
                args.Outcome.Result?.StatusCode is >= HttpStatusCode.InternalServerError),
        });

        builder.AddTimeout(TimeSpan.FromSeconds(5));            // overall budget for the pipeline
    });
```

```csharp
public interface IMaliciousUrlChecker
{
    /// <summary>
    /// Returns true if the URL's domain is flagged as malicious/phishing. Throws
    /// TimeoutException (mapped to 503 by the global middleware) if the
    /// check cannot complete within the resilience pipeline's budget.
    /// </summary>
    Task<bool> IsFlaggedAsync(Uri url, CancellationToken cancellationToken = default);
}
```

Same interface `nfr-security.md` §4.1 already defines — this section only adds the resilience wrapper (timeout/retry) around the `HttpClient` backing its `Infrastructure` implementation, it does not redefine the contract.

- **Per-attempt timeout** (2s) bounds a single network round-trip; **overall pipeline timeout** (5s) bounds the whole create-link request's tolerance for the moderation dependency, so a flaky external service cannot make link creation hang indefinitely — this is what keeps `ANFR-04`'s "degrade gracefully" true even when a *third-party* dependency, not just the local database, is unhealthy.
- **Retry only idempotent GET-style reputation lookups**, and only on transient conditions (timeout, 5xx, connection failure) — never retry on a definitive "this domain is flagged" response, which is a real result, not a transient failure.
- **On exhaustion** (all retries and the timeout budget spent), the adapter throws a `TimeoutException`, which the global middleware already maps to `503 Service Unavailable` (Section 3.3) — the caller is told to retry the whole create-link request later, not silently given a link that skipped its safety check.

### 4.3 Explicit v1 placeholder note

> **Exception (documented placeholder):** As of this document, the malicious-domain check itself (`IMaliciousUrlChecker` and its concrete adapter) is **not yet implemented** — only its interface contract and this resilience wrapper are designed. The retry/timeout configuration above (attempt counts, delays, thresholds) is a reasonable v1 starting point, not a tuned value; it should be revisited once a real moderation provider is selected and its actual latency/error characteristics are known. Until the moderation check is built, `IsFlaggedAsync` may be backed by a no-op/allow-all stub in `Infrastructure` so the create-link flow is not blocked on this dependency — that stub must be clearly named (e.g., `NoOpMaliciousUrlChecker`) and swapped out, not left silently permanent.

---

## 5. Idempotency of the Create-Link Operation

### 5.1 The problem

`POST /api/short-urls` is not naturally idempotent — a second identical `POST` normally creates a second resource. Combined with the global timeout/retry story above (Section 4) and standard client behavior (browsers/HTTP clients often retry a `POST` automatically after a network timeout, unaware of whether the server actually committed it), a client that times out waiting for a create-link response faces a real ambiguity: *did the link get created or not?*

This matters directly for ANFR-04 and ANFR-03: a naive client retry after a timeout must not result in **duplicate short codes for the same submission**, and the client must have a safe way to find out what actually happened instead of guessing.

### 5.2 v1 approach: safe-by-construction, not fully idempotent

v1 does not implement a formal idempotency-key mechanism (e.g., a client-supplied `Idempotency-Key` header with server-side request deduplication). Instead, it relies on three properties that are already true given this project's decisions, which together make a client-side retry-after-timeout **safe** (no corruption, no silent duplicate) even without that machinery:

1. **The write is atomic** (Section 2.2): a timed-out request either committed a fully-formed `ShortUrl` row (with its code, target URL, and audit fields) or committed nothing. There is no possibility of a half-written row for a client to collide with.
2. **Short-code generation is server-owned and collision-checked** (per AF-04 and the create-link design). If a client retries and the server happens to generate a *different* code for the same long URL, that is a duplicate mapping, not corruption — the original URL is still shortenable more than once by design (there's no uniqueness constraint on the target URL, only on the short code), so a duplicate row is a wasted code, not a data-integrity violation.
3. **If the caller supplied a custom alias** (Section D of the in-scope summary), the alias uniqueness constraint on `ShortUrl.Code` makes a retried request with the *same* alias fail fast and audibly: the second attempt gets a `409 Conflict` (unique constraint violation, mapped alongside `DbUpdateConcurrencyException` in Section 3.3) rather than a silent duplicate or a silent success. This is the one path in v1 where a retry after timeout is naturally, automatically idempotent — which is a deliberate reason to encourage custom aliases for clients that need retry-safety, not an incidental side effect.

### 5.3 What this means concretely for a client

| Scenario | v1 behavior |
|---|---|
| Client retries a timed-out `POST` with a **system-generated** code, no alias | May result in two distinct short codes pointing at the same long URL. Not corrupt, not lost data — just a harmless duplicate mapping the client should reconcile by checking existing links (`GET`) before retrying, if duplicates matter to them. |
| Client retries a timed-out `POST` with a **custom alias** | Second attempt fails with `409 Conflict` if the first attempt actually succeeded; client calls `GET` for that alias to confirm the outcome. Effectively idempotent. |
| Server never received the first request (true network failure before the server) | Retry behaves as a normal first create — no ambiguity. |

### 5.4 Explicit deferred scope

> **Exception (documented deferred scope):** A formal idempotency-key pattern (client-supplied key, server-side dedup table keyed on that value with a short TTL, returning the original response on a replayed key) is **not implemented in v1**. It is the standard, well-known fix for the system-generated-code duplicate-mapping case in Section 5.3 and should be the first resilience enhancement considered post-v1 if duplicate mappings from client retries prove to be a real operational problem — it was deferred because v1 has no committed SLA (per the in-scope summary's Service Level Expectations section) and duplicate, harmless mappings are judged an acceptable trade-off against the added complexity of a dedup table for a PoC-stage service.

---

## 6. What Is Explicitly NOT Built in v1

Consistent with this project's practice of naming trade-offs rather than leaving them implicit, and in the spirit of the out-of-scope summary (which does not itemize resilience patterns because none were in scope for the requirements review, not because they were considered and rejected):

> **Exception (documented deferred scope — resilience patterns):** The following standard resilience patterns are **not implemented in v1**, in every case because this is a single-instance PoC-stage service with one embedded database and no committed SLA (in-scope summary, Section H), so the operational conditions that justify them do not yet exist:
>
> - **Circuit breakers** — not added around the SQLite data access path (there is nothing to "trip away from"; a single embedded database has no healthy replica to fail over to) or around the moderation-check call (v1's retry+timeout budget in Section 4 is judged sufficient at current call volume; a circuit breaker becomes worth adding if/when the moderation provider's outages are frequent enough that repeated timeout-then-fail cycles measurably hurt create-link latency for other requests).
> - **Bulkheads / connection-pool isolation** — not configured to isolate the moderation-check `HttpClient` or the SQLite connection pool from each other or from other outbound calls, because v1 has no other outbound calls to isolate against and a single `AppDbContext` pool is already scoped per-request (design guidelines, Section 6).
> - **Fallback/cached-read degradation** (e.g., serving a stale cached redirect target when the database is unreachable) — not implemented; a redirect request during a database outage returns `503` (Section 2.2) rather than a possibly-stale cached value, which is the safer default for ANFR-02 ("a short code shall consistently resolve to the same original URL") until a caching layer is deliberately introduced (the design guidelines already reserve a `CachingShortUrlRepository` decorator slot in the Design Pattern Catalog for exactly this future use).
> - **Distributed idempotency-key store for create-link** — see Section 5.4.
>
> None of these are ruled out permanently — each is deferred because the current single-instance, single-datastore, no-SLA deployment shape (per the in-scope/out-of-scope summaries) doesn't yet create the failure modes these patterns exist to solve. Revisit this list whenever the deployment model changes (e.g., multiple API instances, a server-based RDBMS per the data design guidelines' migration note, or a formal SLA is adopted).

**Note — health-check endpoints are not on this deferred list.** An earlier draft of this section listed `/health/live`/`/health/ready` as not wired up in v1; that contradicted `nfr-reliability-and-availability.md` §4, which designs them as a buildable v1 feature (liveness is process-only; readiness runs `AddDbContextCheck<AppDbContext>`) precisely because they are a low-cost addition whose value doesn't depend on an orchestrator being present yet — manual/ops polling is itself a valid consumer. See that document's §4 for the full design; this document defers to it rather than repeating a since-corrected claim.

---

## 7. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| DB/backend failure | Propagate exceptions untouched from `SaveChangesAsync`/repositories to the global middleware; atomic single-transaction writes via `IUnitOfWork` | ANFR-03, ANFR-04 |
| No app-level DB write retry | Rely on `Microsoft.Data.Sqlite` busy-timeout; documented exception in Section 2.3 | ANFR-04 |
| Global exception handling | Fill the existing middleware placeholder with a typed exception → `ProblemDetails` map (Section 3.3), correlation ID attached | Design guidelines Section 4; ANFR-10 |
| Optimistic concurrency conflicts | `DbUpdateConcurrencyException` → `409 Conflict`, distinguishable from generic failure | Data design guidelines Section 4 |
| Moderation-check resilience | `Microsoft.Extensions.Resilience` (Polly) timeout + limited retry on the outbound HTTP adapter; documented as a v1 placeholder alongside the not-yet-built checker | ANFR-04; in-scope summary Section C |
| Create-link idempotency | No formal idempotency-key store in v1; safety achieved via atomic writes + alias uniqueness; documented deferred scope for the system-generated-code duplicate case | ANFR-03, ANFR-04 |
| Circuit breakers / bulkheads / health probes / fallback caching | Explicitly deferred, with rationale, not silently omitted | ANFR-04 (documented exception) |
