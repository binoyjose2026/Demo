# API Project Structure — MVP (v3)

**Status:** As-built. Documents the actual scaffolded solution, not an aspirational structure.
**Consistent with:** `UrlShortner/global/guidelines/design-guidelines.md` §1 (layered architecture, dependency direction).
**Solution root:** `src/UrlShortner.sln`, targeting **.NET 9** (`global.json` pins the SDK to `9.0.310`).
**Companion doc:** `exception-and-logging-strategy.md` (Serilog wiring, the `UrlShortner.Domain.Exceptions` hierarchy, exception -> HTTP status mapping).

---

## 1a. Layering fix — Domain-level types moved out of `Application`

An earlier pass had put pure Domain-level concerns (constants and exceptions with zero
dependency on ASP.NET Core, EF Core, or any Application-layer orchestration type) inside
`UrlShortner.Application`, violating the "Domain depends on nothing else in the solution"
rule in `UrlShortner/global/guidelines/design-guidelines.md` §1. Audited and fixed:

| Type | Was | Now | Why it's a Domain concern |
|---|---|---|---|
| `UrlValidationConstants` | `Application/ShortUrls/UrlValidationConstants.cs` | `Domain/ShortUrls/UrlValidationConstants.cs` | A business invariant (max original-URL length) — not orchestration logic. |
| `ValidationAppException` + subclasses | `Application/Common/Exceptions/ValidationAppException.cs` | `Domain/Exceptions/ValidationAppException.cs` (+ 4 new subclasses, same folder) | Represents violation of a Domain validation rule; the type itself does no orchestration, it's pure data (message + field name) thrown by Application code and mapped by Api code — neither of which it depends on. |
| `ShortCodeGenerationException` | `Application/Common/Exceptions/ShortCodeGenerationException.cs` | `Domain/Exceptions/ShortCodeGenerationException.cs` | Same reasoning — a business invariant about the short-code generation process, zero framework dependency. |

`UrlShortner.Domain.csproj` still has **zero** `PackageReference`s after this move (only
`System.Exception` is used) — the move did not, and could not, introduce a new
dependency, which is exactly what confirms these types belonged there all along.
`UrlShortner.Application` and `UrlShortner.Api` reference the moved types via `using
UrlShortner.Domain.Exceptions;` / `using UrlShortner.Domain.ShortUrls;` — no other
dependency-direction change was needed since `Application` already referenced `Domain`.

## 1. Solution layout

```
src/
├── UrlShortner.sln
├── db/
│   └── urlshortner.db                     <- real SQLite file, created by `dotnet ef database update`
├── UrlShortner.Api/                       <- ASP.NET Core Web API host (webapi template, controllers)
│   ├── Controllers/
│   │   ├── ShortUrlsController.cs         <- POST /api/short-urls (AF-01)
│   │   └── RedirectController.cs          <- GET /{code}          (AF-02, AF-06)
│   ├── Middleware/
│   │   └── GlobalExceptionHandler.cs      <- IExceptionHandler -> ProblemDetails; logs every exception (Serilog)
│   ├── Program.cs                         <- composition root; UseSerilog()/UseSerilogRequestLogging() wired here
│   ├── appsettings.json                   <- "Serilog" section (console sink, MinimumLevel, Overrides)
│   └── appsettings.Development.json
├── UrlShortner.Application/               <- use-case orchestration, DTOs, validation
│   ├── ShortUrls/
│   │   ├── CreateShortUrlRequest.cs / ShortUrlResponse.cs   (DTOs)
│   │   ├── IShortUrlService.cs / ShortUrlService.cs         (create, AF-01/03/04; ILogger<ShortUrlService> injected)
│   │   ├── IShortUrlResolverService.cs / ShortUrlResolverService.cs (fetch, AF-02/06; ILogger<ShortUrlResolverService> injected)
│   │   └── ShortUrlResolutionStatus.cs / ShortUrlResolutionResult.cs
│   ├── Common/
│   │   └── ShortUrlOptions.cs             <- Options pattern (base URL for responses)
│   └── ApplicationServiceCollectionExtensions.cs
├── UrlShortner.Domain/                    <- entities, repository/strategy interfaces, Domain-level
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
├── UrlShortner.Infrastructure/            <- EF Core, repositories, generator/cache impls
│   ├── Persistence/
│   │   ├── AppDbContext.cs
│   │   └── Migrations/20260817180753_InitialCreate.cs
│   ├── Repositories/Repository.cs, UnitOfWork.cs
│   ├── ShortUrls/ShortUrlRepository.cs, RandomBase62ShortCodeGenerator.cs
│   ├── Caching/NullShortUrlCache.cs       <- the literal NULL Redis placeholder
│   └── InfrastructureServiceCollectionExtensions.cs
├── UrlShortner.Common/                    <- zero-dependency shared helpers
│   └── Guards/Guard.cs
├── UrlShortner.Application.Tests/         <- unit tests (Moq, xUnit)
│   └── ShortUrls/ShortUrlServiceTests.cs, ShortUrlResolverServiceTests.cs
└── UrlShortner.IntegrationTests/          <- integration tests (WebApplicationFactory)
    ├── UrlShortnerWebApplicationFactory.cs
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
| `UrlShortner.Api` | `Microsoft.AspNetCore.OpenApi`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Sqlite`, `Serilog.AspNetCore` 9.0.0, `Serilog.Sinks.Console` 6.0.0, `Serilog.Settings.Configuration` 9.0.0 (pinned to the `9.x` line to match this project's `net9.0`/ASP.NET Core 9 target rather than the newer `10.x` release) |
| `UrlShortner.Application` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` (for `ILogger<T>`, injected into `ShortUrlService`/`ShortUrlResolverService`), `Microsoft.Extensions.Options.ConfigurationExtensions` |
| `UrlShortner.Domain` | *(none — zero `PackageReference`s, by design; see §1a)* |
| `UrlShortner.Infrastructure` | `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Sqlite` |
| `UrlShortner.Application.Tests` | `xunit`, `Moq`, `Microsoft.NET.Test.Sdk` |
| `UrlShortner.IntegrationTests` | `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.EntityFrameworkCore.Sqlite` (for the in-memory test connection), `xunit` |

## 4. Composition root (`Program.cs`)

- `builder.Host.UseSerilog(...)`, reading from the `Serilog` section of `appsettings.json`/`appsettings.{Environment}.json` — replaces the default `Microsoft.Extensions.Logging` provider entirely, wired **before** `AddApplicationServices`/`AddInfrastructureServices` so every service's `ILogger<T>` is Serilog-backed from the start. `app.UseSerilogRequestLogging()` is registered right after `app.UseExceptionHandler()`. See `exception-and-logging-strategy.md` §1.
- `AddApplicationServices` / `AddInfrastructureServices` — one DI extension call per layer, per design-guidelines.md §6.
- `AddProblemDetails()` + `AddExceptionHandler<GlobalExceptionHandler>()` + `app.UseExceptionHandler()` — the standard error response shape (design-guidelines.md §3), implemented via the current idiomatic ASP.NET Core `IExceptionHandler` mechanism rather than hand-rolled middleware.
- The SQLite connection string is **resolved programmatically** (walks up from the running assembly's location to find `UrlShortner.sln`, then targets `<that folder>/db/urlshortner.db`) so the database file lands at the exact required path (`src/db/urlshortner.db`) regardless of whether the app is launched via `dotnet run`, a built `.exe`, or a test host.
- `context.Database.Migrate()` runs on startup (skipped under the `IntegrationTest` environment, where `UrlShortnerWebApplicationFactory` migrates its own private in-memory connection instead).
- `public partial class Program { }` is appended so `WebApplicationFactory<Program>` in the separate `IntegrationTests` project can reference the top-level-statement-generated `Program` class.

## 5. What was deliberately not scaffolded (see `documentation/02-design/v3.MVP/agents/mvp-design@agent.md`)

No `UrlShortner.Domain.Tests` project (nfr-unit-testing.md §2 lists it, but `UrlShortner.Domain` still has no *behavioral* logic to unit-test in isolation — the `ShortUrl` entity is a plain data holder, `UrlValidationConstants` is a literal, and the `Exceptions` types (§1a) are simple data-carrying classes with no branching logic of their own; every one of them is already exercised indirectly through `ShortUrlServiceTests`/`ShortUrlResolverServiceTests`. Trivial to add a dedicated project once an actual domain-level *method* with branching logic exists to test). No analytics, auth, or rate-limiting projects/modules — see the per-feature "deliberately deferred" comments in `ShortUrlService.cs`, `Program.cs`, and `api-design.md`.
