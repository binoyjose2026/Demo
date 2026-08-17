# Software Architecture & Design Guidelines

These guidelines define the standard application architecture for this project (a URL shortener built on **ASP.NET Core MVC / Web API, .NET 9**). They describe a conventional, widely-recognized **layered architecture** with a **Repository pattern** over EF Core, standard ASP.NET Core middleware/filter pipelines, and constructor-based **Dependency Injection** — nothing here is a bespoke or exotic pattern. They are written to be consistent with the [C# Coding Guidelines](./coding-guidelines.md) (naming, SOLID, async conventions) and the [Data Design Guidelines](./data-design-guidelines.md) (EF Core + SQLite, `Id`/audit/`RowVersion`/soft-delete conventions on every entity).

---

## 1. Solution & Project Layout

The solution is split into separate class library projects along standard layered-architecture lines, so each layer can be compiled, tested, and reasoned about independently.

| Project | Responsibility |
|---|---|
| **`UrlShortener.Api`** | ASP.NET Core Web API/MVC host: controllers, middleware, MVC filters, the DI composition root (`Program.cs`), `appsettings.json`. The only executable/startup project. |
| **`UrlShortener.Application`** | Application/business logic layer: application services, use-case orchestration, request/response DTOs, validation. Depends only on `Domain` (and `Common`). |
| **`UrlShortener.Domain`** | The **common domain project**: entities (including the `AuditableEntity` base from the data design guidelines), value objects, domain-level interfaces (e.g., `IRepository<T>`), enums, and domain constants. **Depends on nothing else in the solution.** |
| **`UrlShortener.Infrastructure`** | Data access and external integrations: the EF Core `DbContext`, entity configurations, repository implementations, `IUnitOfWork`, third-party/external service adapters. Depends on `Domain` (and `Common`). |
| **`UrlShortener.Common`** (a.k.a. `Utilities`) | The **utilities project**: stateless, cross-cutting helper functions and extension methods (string/date/short-code helpers, guard-clause helpers, constants shared across layers). Contains **no business logic** and **no dependency on any other project** — everything else may depend on it. |

### Allowed dependency direction

```
Api  ──────────────►  Application  ──────────────►  Domain
 │                          │                            ▲
 │                          ▼                            │
 └───────────────►  Infrastructure ──────────────────────┘
                          │
                          ▼
                        Common  (referenced by every project; depends on none)
```

- `Domain` references nothing else in the solution (it may reference `Common` only if strictly needed — prefer zero dependencies).
- `Application` references `Domain` and `Common` only. It **must not** reference `Infrastructure` or `Api` — it depends on abstractions (`IRepository<T>`, `IUnitOfWork`) defined in `Domain`, not on their EF Core implementations.
- `Infrastructure` references `Domain` and `Common`, and implements the interfaces `Domain`/`Application` define. It **must not** be referenced by `Application`.
- `Api` references `Application` and `Infrastructure` — `Infrastructure` **only** for DI registration/composition in `Program.cs` (`AddInfrastructureServices()`), never for controllers to call `Infrastructure` types directly.
- **Never** reference "downward-to-upward": `Domain` must never reference `Application`, `Infrastructure`, or `Api`.
- Controllers depend on `Application` service interfaces only — never on `Infrastructure` (repositories, `DbContext`) or `Domain` entities directly (see Section 3).

This is the standard "Clean Architecture" / "Onion Architecture" dependency rule as commonly applied to ASP.NET Core solutions: dependencies point inward, toward `Domain`.

---

## 2. Repository Pattern

The Repository pattern is the standard data-access abstraction between the `Application` layer and EF Core. Repository **interfaces** live in `Domain` (or `Application`, if preferred, since they are the abstraction consumers depend on); **implementations** live in `Infrastructure` and wrap the `AppDbContext`.

- A generic `IRepository<T>` provides basic CRUD for any `AuditableEntity`-derived type, so most entities don't need a bespoke repository.
- An entity-specific repository (e.g., `IShortUrlRepository : IRepository<ShortUrl>`) is added only when an entity needs queries beyond generic CRUD (e.g., "find by short code").
- An `IUnitOfWork` coordinates multiple repositories under a single `SaveChangesAsync` call, so a use case that touches more than one aggregate commits as one transaction.
- Repositories work **with**, not around, the data-design conventions: `GetByIdAsync` and list queries rely on the global soft-delete query filter (`IsDeleted`) already configured on `AppDbContext`, so callers never see soft-deleted rows without asking explicitly; `UpdateAsync` relies on EF Core's `RowVersion` concurrency token to throw `DbUpdateConcurrencyException` on a conflicting write; `DeleteAsync` performs a **soft delete** (sets `IsDeleted`/`DeletedAtUtc`) rather than issuing a hard `DELETE`, consistent with Section 5 of the data design guidelines.

```csharp
public interface IRepository<T> where T : AuditableEntity
{
    Task<T?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    void Delete(T entity); // soft delete — sets IsDeleted/DeletedAtUtc, does not physically remove the row
}

public interface IUnitOfWork
{
    IRepository<T> Repository<T>() where T : AuditableEntity;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
```

- Repositories return `Domain` entities, never `IQueryable<T>` leaking EF Core-specific behavior across the `Application`/`Infrastructure` boundary, keeping `Infrastructure` swappable (consistent with the "SQLite today, server RDBMS later" note in the data design guidelines).

---

## 3. Layered API Design (ASP.NET Core MVC, .NET 9)

- **Thin controllers**: controllers only (a) bind/validate the incoming request, (b) call a single `Application` service method, and (c) map the result to an HTTP response. No business logic, no direct EF Core/repository calls in a controller.
- **DTOs at the boundary, never domain entities**: controllers accept and return request/response DTOs defined in `Application` (e.g., `CreateShortUrlRequest`, `ShortUrlResponse`), never `Domain` entities directly. This keeps the wire contract decoupled from persistence concerns (audit fields, `RowVersion`, navigation properties should not leak to API consumers).
- **Mapping** between entities and DTOs happens in the `Application` layer (hand-written mapping methods or a mapping library), not in controllers.
- **Standard error shape**: use `ProblemDetails` (RFC 7807, built into ASP.NET Core via `AddProblemDetails()` / `UseExceptionHandler`) as the uniform error response shape for all non-2xx responses, including validation failures (`ValidationProblemDetails`).
- **Versioning**: reserved as a placeholder for now — the project will adopt URL-segment versioning (e.g., `/api/v1/...`) via the standard `Asp.Versioning.Mvc` package when a breaking API change is first required. No versioning package is wired in yet.

```csharp
[ApiController]
[Route("api/short-urls")]
public class ShortUrlsController : ControllerBase
{
    private readonly IShortUrlService _shortUrlService;

    public ShortUrlsController(IShortUrlService shortUrlService) => _shortUrlService = shortUrlService;

    [HttpPost]
    public async Task<ActionResult<ShortUrlResponse>> CreateAsync(CreateShortUrlRequest request, CancellationToken cancellationToken)
    {
        var result = await _shortUrlService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetAsync), new { code = result.Code }, result);
    }
}
```

---

## 4. Middleware Pipeline

The following ASP.NET Core middleware components are the standard pipeline for this project. They are **placeholders** to be fully implemented as the project matures — listed here to fix the expected pipeline shape and order:

1. **Global exception-handling middleware** — catches unhandled exceptions and converts them to a `ProblemDetails` response; the top-level handler referenced in the coding guidelines' error-handling section.
2. **Correlation-ID / request logging middleware** — assigns/propagates a correlation ID per request and logs request/response summaries for traceability.
3. **Authentication middleware** (`UseAuthentication`) — standard ASP.NET Core identity/token validation, placeholder until an auth scheme is chosen.
4. **Authorization middleware** (`UseAuthorization`) — standard ASP.NET Core policy/role enforcement.

```csharp
/// <summary>
/// Placeholder middleware. Converts unhandled exceptions into a ProblemDetails response.
/// </summary>
public class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public GlobalExceptionHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // TODO: log ex, write a ProblemDetails response, set status code.
        }
    }
}
```

Registered in `Program.cs` in this order: exception handling → correlation/logging → authentication → authorization → MVC endpoints.

---

## 5. MVC Filters

Standard ASP.NET Core MVC filter types this project will use, added as **placeholders** and applied globally or per-controller/action as needed:

| Filter type | Purpose |
|---|---|
| **Action filter** | Model-state/validation check before an action executes, so controllers don't repeat `if (!ModelState.IsValid)` checks. |
| **Exception filter** | MVC-level exception-to-response translation for cases not caught by the global middleware (e.g., MVC-pipeline-specific concerns). |
| **Authorization filter** | Custom authorization checks beyond the standard `[Authorize]` attribute, where needed. |
| **Result filter** | Post-processing of the action result before it's written to the response (e.g., shaping/enveloping responses). |

```csharp
/// <summary>
/// Placeholder action filter. Short-circuits with a ValidationProblemDetails response
/// when ModelState is invalid, so controllers don't repeat this check.
/// </summary>
public class ValidateModelStateFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState));
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
```

---

## 6. Dependency Injection

- **Constructor injection** is the standard way every class receives its dependencies — no service locator, no `new`-ing up dependencies inside a class (consistent with Section 8 of the coding guidelines).
- **Service lifetimes**:
  - **Transient** — stateless, cheap-to-create services with no shared state (e.g., a short-code generation strategy, a mapper). Default choice when unsure.
  - **Scoped** — services holding per-request state or wrapping something scoped, most importantly `AppDbContext` and repository/`IUnitOfWork` implementations. One instance per HTTP request avoids sharing a `DbContext`'s change tracker across unrelated requests.
  - **Singleton** — stateless services that are expensive to create or must be shared app-wide (e.g., configuration-bound `IOptions<T>` snapshots, an in-memory cache instance). Never register `AppDbContext` or a repository as Singleton — EF Core's `DbContext` is not thread-safe for concurrent use.
- **Registration convention**: each layer exposes its own `IServiceCollection` extension method, called from `Program.cs`, so `Program.cs` stays a short composition root rather than a long flat list of registrations.

```csharp
// Infrastructure/DependencyInjection.cs
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("DefaultConnection")));

        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
```

```csharp
// Program.cs
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);
```

---

## 7. SOLID Principles

All five SOLID principles apply, as they do throughout the coding guidelines — restated here specifically as they shape *this architecture's* structural decisions:

- **S — Single Responsibility**: each layer (and each class within it — controller, application service, repository) has exactly one reason to change; e.g., a controller changes only for HTTP-shape reasons, an application service only for business-rule reasons.
- **O — Open/Closed**: new behavior (e.g., a new short-code generation strategy) is added as a new implementation registered via DI, without modifying existing, tested classes.
- **L — Liskov Substitution**: any `IRepository<T>` implementation (the default EF Core one, a future caching decorator, a test in-memory fake) must be substitutable wherever `IRepository<T>` is used, without breaking callers.
- **I — Interface Segregation**: repository and service interfaces stay small and role-specific (e.g., a separate `IShortUrlRepository` for short-code lookups) rather than one bloated interface every consumer is forced to depend on in full.
- **D — Dependency Inversion**: controllers and application services depend on abstractions — `IRepository<T>`, `IUnitOfWork`, `IShortUrlService` — never on `AppDbContext` or concrete EF Core types directly; this is exactly what makes `Application` independent of `Infrastructure` in the dependency diagram in Section 1.

---

## 8. Design Pattern Catalog

Design patterns this architecture will potentially engage, kept to patterns that plausibly fit a layered ASP.NET Core Web API with a repository-backed data layer:

| Pattern | What it's for / where it shows up here |
|---|---|
| **Repository** | Abstracts EF Core data access behind `IRepository<T>` / entity-specific repositories (Section 2). |
| **Unit of Work** | Coordinates multiple repositories under one `SaveChangesAsync` transaction via `IUnitOfWork`. |
| **Dependency Injection** | Constructor-injected abstractions wired through `Microsoft.Extensions.DependencyInjection`, composed per-layer (Section 6). |
| **Options Pattern** | Strongly-typed configuration binding (`IOptions<T>` / `IOptionsSnapshot<T>`) for settings such as connection strings or short-code generation settings, instead of raw `IConfiguration` lookups scattered through the code. |
| **Strategy** | Pluggable short-code generation algorithm (e.g., random vs. sequential vs. hash-based) behind a common `IShortCodeGenerator` interface, swappable via DI. |
| **Decorator** | Optional caching layer over `IRepository<T>`/`IShortUrlRepository` (e.g., a `CachingShortUrlRepository` wrapping the EF Core one) added without changing consumers. |
| **Adapter** | Wraps external service integrations (e.g., an analytics or link-safety-check API) behind a `Domain`/`Application`-defined interface, isolating third-party SDK shapes to `Infrastructure`. |
| **Factory** | Where object construction needs to vary at runtime (e.g., choosing an `IShortCodeGenerator` implementation by configuration), a simple factory encapsulates the selection logic rather than scattering `if`/`switch` construction logic. |

Patterns intentionally **not** included: Specification (query composition needs here are simple enough that repository methods and LINQ suffice; would be revisited only if query complexity grows significantly), CQRS/MediatR, and other heavier patterns not warranted by this project's scope.
