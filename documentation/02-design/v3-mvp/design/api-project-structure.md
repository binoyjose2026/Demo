# API Project Structure — MVP (v3)

**Status:** As-built. Documents the actual scaffolded solution, not an aspirational structure.
**Consistent with:** `UrlShortener/engineering-standards/guidelines/design-guidelines.md` §1 (layered architecture, dependency direction).
**Solution root:** `src/UrlShortener.sln`, targeting **.NET 9** (`global.json` pins the SDK to `9.0.310`).
**Companion doc:** `exception-and-logging-strategy.md` (Serilog wiring, the `UrlShortener.Domain.Exceptions` hierarchy, exception -> HTTP status mapping).

---

## 1a. Layering fix — Domain-level types moved out of `Application`

An earlier pass had put pure Domain-level concerns (constants and exceptions with zero
dependency on ASP.NET Core, EF Core, or any Application-layer orchestration type) inside
`UrlShortener.Application`, violating the "Domain depends on nothing else in the solution"
rule in `UrlShortener/engineering-standards/guidelines/design-guidelines.md` §1. Audited and fixed:

| Type | Was | Now | Why it's a Domain concern |
|---|---|---|---|
| `UrlValidationConstants` | `Application/ShortUrls/UrlValidationConstants.cs` | `Domain/ShortUrls/UrlValidationConstants.cs` | A business invariant (max original-URL length) — not orchestration logic. |
| `ValidationAppException` + subclasses | `Application/Common/Exceptions/ValidationAppException.cs` | `Domain/Exceptions/ValidationAppException.cs` (+ 4 new subclasses, same folder) | Represents violation of a Domain validation rule; the type itself does no orchestration, it's pure data (message + field name) thrown by Application code and mapped by Api code — neither of which it depends on. |
| `ShortCodeGenerationException` | `Application/Common/Exceptions/ShortCodeGenerationException.cs` | `Domain/Exceptions/ShortCodeGenerationException.cs` | Same reasoning — a business invariant about the short-code generation process, zero framework dependency. |

`UrlShortener.Domain.csproj` still has **zero** `PackageReference`s after this move (only
`System.Exception` is used) — the move did not, and could not, introduce a new
dependency, which is exactly what confirms these types belonged there all along.
`UrlShortener.Application` and `UrlShortener.Api` reference the moved types via `using
UrlShortener.Domain.Exceptions;` / `using UrlShortener.Domain.ShortUrls;` — no other
dependency-direction change was needed since `Application` already referenced `Domain`.

## 1. Solution layout

```
src/
├── UrlShortener.sln
├── db/
│   └── urlshortener.db                    <- real SQLite file, created by `dotnet ef database update`
├── UrlShortener.Api/                       <- ASP.NET Core Web API host (webapi template, controllers)
│   ├── Controllers/
│   │   ├── ShortUrlsController.cs         <- POST /api/short-urls (AF-01)
│   │   └── RedirectController.cs          <- GET /{code}          (AF-02, AF-06)
│   ├── Middleware/
│   │   └── GlobalExceptionHandler.cs      <- IExceptionHandler -> ProblemDetails; logs every exception (Serilog)
│   ├── Program.cs                         <- composition root; UseSerilog()/UseSerilogRequestLogging() wired here
│   ├── appsettings.json                   <- "Serilog" section (console sink, MinimumLevel, Overrides)
│   └── appsettings.Development.json
├── UrlShortener.Application/               <- use-case orchestration, DTOs, validation
│   ├── ShortUrls/
│   │   ├── CreateShortUrlRequest.cs / ShortUrlResponse.cs   (DTOs)
│   │   ├── IShortUrlService.cs / ShortUrlService.cs         (create, AF-01/03/04; ILogger<ShortUrlService> injected)
│   │   ├── IShortUrlResolverService.cs / ShortUrlResolverService.cs (fetch, AF-02/06; ILogger<ShortUrlResolverService> injected)
│   │   └── ShortUrlResolutionStatus.cs / ShortUrlResolutionResult.cs
│   ├── Common/
│   │   └── ShortUrlOptions.cs             <- Options pattern (base URL for responses)
│   └── ApplicationServiceCollectionExtensions.cs
├── UrlShortener.Domain/                    <- entities, repository/strategy interfaces, Domain-level
│   │                                          constants & exceptions (zero deps on ASP.NET Core/EF
│   │                                          Core/Application orchestration -- design-guidelines.md §1)
│   ├── Entities/
│   │   ├── AuditableEntity.cs             <- Id/audit/RowVersion/soft-delete base
│   │   └── ShortUrl.cs
│   ├── Exceptions/                        <- moved here from Application/Common/Exceptions (see §1a below):
│   │   ├── ValidationAppException.cs      <-   unsealed base type
│   │   ├── MissingUrlException.cs         <-   : ValidationAppException (empty/null URL)
│   │   ├── MalformedUrlException.cs       <-   : ValidationAppException (not a well-formed URI)
│   │   ├── UnsupportedUrlSchemeException.cs <- : ValidationAppException (not http/https)
│   │   ├── UrlTooLongException.cs         <-   : ValidationAppException (exceeds max length)
│   │   └── ShortCodeGenerationException.cs <-  sealed (AF-04 retry-budget exhausted)
│   ├── Repositories/
│   │   ├── IRepository.cs / IUnitOfWork.cs
│   │   └── IShortUrlRepository.cs
│   └── ShortUrls/
│       ├── IShortCodeGenerator.cs         <- Strategy seam (AF-04)
│       ├── IShortUrlCache.cs              <- NULL-cache seam (the Redis placeholder)
│       └── UrlValidationConstants.cs      <- moved here from Application/ShortUrls (see §1a below)
├── UrlShortener.Infrastructure/            <- EF Core, repositories, generator/cache impls
│   ├── Persistence/
│   │   ├── AppDbContext.cs
│   │   └── Migrations/20260817180753_InitialCreate.cs
│   ├── Repositories/Repository.cs, UnitOfWork.cs
│   ├── ShortUrls/ShortUrlRepository.cs, RandomBase62ShortCodeGenerator.cs
│   ├── Caching/NullShortUrlCache.cs       <- the literal NULL Redis placeholder
│   └── InfrastructureServiceCollectionExtensions.cs
├── UrlShortener.Common/                    <- zero-dependency shared helpers
│   └── Guards/Guard.cs
├── UrlShortener.Application.Tests/         <- unit tests (Moq, xUnit)
│   └── ShortUrls/ShortUrlServiceTests.cs, ShortUrlResolverServiceTests.cs
└── UrlShortener.IntegrationTests/          <- integration tests (WebApplicationFactory)
    ├── UrlShortenerWebApplicationFactory.cs
    └── ShortUrls/ShortUrlsEndpointTests.cs
```

## 2. Dependency direction (as built, matches design-guidelines.md §1)

```
Api  ──────────────►  Application  ──────────────►  Domain
 │                          │                            ▲
 │                          ▼                            │
 └───────────────►  Infrastructure ──────────────────────┘
                          │
                          ▼
                        Common
```

Verified via actual `dotnet add reference` calls:
- `Application` → `Domain`, `Common`
- `Infrastructure` → `Domain`, `Common`
- `Api` → `Application`, `Infrastructure`
- `Application.Tests` → `Application`, `Domain` (mocks `Domain`-defined interfaces; never references `Infrastructure`)
- `IntegrationTests` → `Api`, `Application` (boots the real app via `WebApplicationFactory<Program>`)

## 3. Key NuGet packages per project

| Project | Key packages |
|---|---|
| `UrlShortener.Api` | `Microsoft.AspNetCore.OpenApi`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Sqlite`, `Serilog.AspNetCore` 9.0.0, `Serilog.Sinks.Console` 6.0.0, `Serilog.Settings.Configuration` 9.0.0 (pinned to the `9.x` line to match this project's `net9.0`/ASP.NET Core 9 target rather than the newer `10.x` release) |
| `UrlShortener.Application` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` (for `ILogger<T>`, injected into `ShortUrlService`/`ShortUrlResolverService`), `Microsoft.Extensions.Options.ConfigurationExtensions` |
| `UrlShortener.Domain` | *(none — zero `PackageReference`s, by design; see §1a)* |
| `UrlShortener.Infrastructure` | `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Sqlite` |
| `UrlShortener.Application.Tests` | `xunit`, `Moq`, `Microsoft.NET.Test.Sdk` |
| `UrlShortener.IntegrationTests` | `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.EntityFrameworkCore.Sqlite` (for the in-memory test connection), `xunit` |
| `UrlShortener.LoadTests` (§6.7, new) | `NBomber` |

**Post-hardening additions to `UrlShortener.Api` (§6):** `Swashbuckle.AspNetCore` (pinned to 6.9.0 — the 7.x+/10.x lines relocate `OpenApiInfo` to a `Microsoft.OpenApi` namespace shape that isn't worth chasing for this MVP), `Asp.Versioning.Mvc` + `.Mvc.ApiExplorer` (8.1.0 — the current `10.x` line targets `net10.0` only), `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`, `OpenTelemetry.Extensions.Hosting`, `.Instrumentation.AspNetCore`, `.Instrumentation.EntityFrameworkCore` (prerelease — this package has not shipped a stable release compatible with current EF Core versions), `.Exporter.Console`. **Post-hardening addition to `UrlShortener.Application`:** `Microsoft.Extensions.Options.DataAnnotations` (for `ValidateDataAnnotations()`, §6.6).

## 4. Composition root (`Program.cs`)

- `builder.Host.UseSerilog(...)`, reading from the `Serilog` section of `appsettings.json`/`appsettings.{Environment}.json` — replaces the default `Microsoft.Extensions.Logging` provider entirely, wired **before** `AddApplicationServices`/`AddInfrastructureServices` so every service's `ILogger<T>` is Serilog-backed from the start. `app.UseSerilogRequestLogging()` is registered right after `app.UseExceptionHandler()`. See `exception-and-logging-strategy.md` §1.
- `AddApplicationServices` / `AddInfrastructureServices` — one DI extension call per layer, per design-guidelines.md §6.
- `AddProblemDetails()` + `AddExceptionHandler<GlobalExceptionHandler>()` + `app.UseExceptionHandler()` — the standard error response shape (design-guidelines.md §3), implemented via the current idiomatic ASP.NET Core `IExceptionHandler` mechanism rather than hand-rolled middleware.
- The SQLite connection string is **resolved programmatically** (walks up from the running assembly's location to find `UrlShortener.sln`, then targets `<that folder>/db/urlshortener.db`) so the database file lands at the exact required path (`src/db/urlshortener.db`) regardless of whether the app is launched via `dotnet run`, a built `.exe`, or a test host.
- `context.Database.Migrate()` runs on startup (skipped under the `IntegrationTest` environment, where `UrlShortenerWebApplicationFactory` migrates its own private in-memory connection instead).
- `public partial class Program { }` is appended so `WebApplicationFactory<Program>` in the separate `IntegrationTests` project can reference the top-level-statement-generated `Program` class.

## 5. What was deliberately not scaffolded (see `documentation/02-design/v3-mvp/agents/agent-prompt.md`)

No `UrlShortener.Domain.Tests` project (nfr-unit-testing.md §2 lists it, but `UrlShortener.Domain` still has no *behavioral* logic to unit-test in isolation — the `ShortUrl` entity is a plain data holder, `UrlValidationConstants` is a literal, and the `Exceptions` types (§1a) are simple data-carrying classes with no branching logic of their own; every one of them is already exercised indirectly through `ShortUrlServiceTests`/`ShortUrlResolverServiceTests`. Trivial to add a dedicated project once an actual domain-level *method* with branching logic exists to test). No analytics module — see `fn-analytics.md` and the per-feature "deliberately deferred" comments in `ShortUrlService.cs`/`ShortUrlResolverService.cs`. Auth and rate-limiting are no longer un-wired placeholders — see §6.

## 6. Post-MVP hardening additions

A batch of production-hardening items layered onto the MVP structure above without changing the core create/fetch project boundaries. Full behavioral description: `api-design.md` §6.

### 6.1 New files in `UrlShortener.Api`

```
UrlShortener.Api/
├── Authentication/
│   └── MvpPlaceholderAuthenticationHandler.cs   <- always-succeeds placeholder auth scheme (item 3)
├── Filters/
│   └── IdempotencyKeyFilter.cs                  <- reads Idempotency-Key, does not dedupe (item 5)
├── Middleware/
│   ├── CorrelationIdMiddleware.cs               <- X-Correlation-Id in/out + Serilog LogContext (item 6)
│   └── SecurityResponseHeadersMiddleware.cs     <- X-Content-Type-Options, Referrer-Policy (item 10)
├── Dockerfile                                   <- multi-stage SDK/runtime build (item 13)
└── (Program.cs, Controllers/, Middleware/GlobalExceptionHandler.cs -- extended, not replaced)
```

`GlobalExceptionHandler` and the two new middleware classes are declared `internal` (not `public`) — they are Api-layer plumbing, not part of the public API surface `GenerateDocumentationFile`/CS1591 cares about (see `UrlShortener.Api.csproj`'s own comment).

### 6.2 New files in `UrlShortener.Domain`

```
UrlShortener.Domain/ShortUrls/
├── IShortUrlEventPublisher.cs        <- NullKafka seam, mirrors IShortUrlCache (item 4)
└── ShortCodeValidationConstants.cs   <- shared base62 alphabet + IsValidFormat() (item 16)
```

### 6.3 New files in `UrlShortener.Infrastructure`

```
UrlShortener.Infrastructure/Messaging/
└── NullShortUrlEventPublisher.cs     <- the literal NULL Kafka placeholder (item 4), mirrors
                                          Caching/NullShortUrlCache.cs exactly
```

### 6.4 New files in `UrlShortener.IntegrationTests`

```
UrlShortener.IntegrationTests/Persistence/
└── ShortUrlConcurrencyTests.cs       <- RowVersion optimistic-concurrency proof (item 17),
                                          against AppDbContext directly (no update endpoint exists)
```

### 6.5 New solution-root files

```
src/
├── .editorconfig      <- item 12: formatting/naming rules matching coding-guidelines.md
├── requests.http       <- item 14: example create/fetch requests, valid and invalid
└── README.md           <- item 15: how to run locally, test, load-test, and containerize
```

### 6.6 `UrlShortener.Application` change

`ApplicationServiceCollectionExtensions.AddApplicationServices` now binds `ShortUrlOptions` via `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` instead of the older `services.Configure<T>(...)` — see item 7 / `api-design.md` §6.11. `ShortUrlOptions.BaseUrl` gained `[Required]`/`[Url]` attributes.

### 6.7 New project: `UrlShortener.LoadTests`

```
UrlShortener.LoadTests/
├── UrlShortener.LoadTests.csproj   <- console app (OutputType=Exe), NBomber package only,
│                                      no ProjectReference to any other project in the
│                                      solution -- it drives the app over plain HTTP,
│                                      the same way an external load-test client would
└── Program.cs                     <- two scenarios: create_short_url (POST), fetch_redirect (GET)
```

Added to `UrlShortener.sln` via `dotnet sln add`. Deliberately **not** an xUnit project (no `Microsoft.NET.Test.Sdk`/`xunit` package references, no `<IsPackable>false</IsPackable>` test-project shape) so `dotnet test` on the solution skips it automatically — there is nothing for VSTest to discover. Build-only for this task: not run/executed, not wired into CI. See the project's own `Program.cs` header comment and `api-design.md` §6.12 for why measuring against the v2 design's extreme-scale numbers would require the actual v2 infrastructure (Redis, distributed rate limiting, database partitioning/sharding, horizontally-scaled instances) to be a meaningful comparison — this is a single-instance/SQLite MVP, so this harness is a smoke/harness check, not a scalability benchmark.

### 6.8 DI-lifetime audit (item 8)

Reviewed every registration in `ApplicationServiceCollectionExtensions`/`InfrastructureServiceCollectionExtensions`; all were already correct (no `AppDbContext`/repository/`IUnitOfWork` registered as anything other than Scoped). Added a one-line comment at each registration confirming the intentional lifetime choice, including the two new item-4 registrations (`IShortUrlEventPublisher` → Singleton, same reasoning as `IShortUrlCache`).

### 6.9 CancellationToken propagation audit (item 9)

Reviewed every async method in `UrlShortener.Application` and `UrlShortener.Infrastructure`; all already accepted and threaded a `CancellationToken` through to every downstream async call. No gaps found — the one method that intentionally does *not* take one (`ShortUrlService.PublishUrlCreatedEventSafelyAsync`) is documented inline as deliberate: it's a fire-and-forget side effect that must outlive the (already-cancelled-by-then) request token.
