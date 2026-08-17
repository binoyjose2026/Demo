# Unit Testing Design — URL Shortener

**Version:** v1
**Status:** Draft
**Consistent with:** [`coding-guidelines.md`](../../../../engineering-standards/guidelines/coding-guidelines.md) §9 (Testing Conventions), [`data-design-guidelines.md`](../../../../engineering-standards/guidelines/data-design-guidelines.md), [`design-guidelines.md`](../../../../engineering-standards/guidelines/design-guidelines.md) §1 (Solution & Project Layout)
**Companion document:** `nfr-integration-testing.md` (EF Core/SQLite behavior, HTTP pipeline, end-to-end redirect flow — not duplicated here)

---

## 1. Purpose & Scope

This document defines how **unit tests** are organized and written for the URL Shortener solution: which layers get unit-tested, the standard mocking/assertion tooling, and an example test skeleton. It covers *unit* tests only — tests that exercise a single class in isolation, with all collaborators replaced by test doubles. Anything that needs a real database, a real HTTP pipeline, or more than one layer wired together belongs to the integration testing design (`nfr-integration-testing.md`) and is out of scope here (see §5).

---

## 2. Test Project Structure

Test projects mirror the layered solution structure from `design-guidelines.md` §1, one test project per testable production project, using the standard `<ProjectUnderTest>.Tests` naming convention:

| Test project | Tests | References |
|---|---|---|
| `UrlShortener.Domain.Tests` | `UrlShortener.Domain` — entities, value objects, domain rules | `UrlShortener.Domain` |
| `UrlShortener.Application.Tests` | `UrlShortener.Application` — application services, DTO validation/mapping | `UrlShortener.Application` (→ transitively `Domain`, `Common`), `Moq`, `xunit` |

`UrlShortener.Infrastructure` and `UrlShortener.Api` have **no unit test project** — see §5 for rationale; they are covered by the integration testing design instead.

- **Folder structure inside each test project mirrors the source project's folder/namespace structure**, per the "folder structure mirrors namespace" rule in `coding-guidelines.md` §2 — e.g. `UrlShortener.Application/Services/ShortUrlService.cs` is tested by `UrlShortener.Application.Tests/Services/ShortUrlServiceTests.cs`.
- **One test class per production class** (`ShortUrlServiceTests` for `ShortUrlService`), consistent with the "one type per file" convention.
- Test projects follow the same dependency-direction rule as production code (`design-guidelines.md` §1): `UrlShortener.Application.Tests` never references `Infrastructure` or `Api` — it mocks `Domain`-defined abstractions (`IRepository<T>`, `IUnitOfWork`) instead, so these tests stay fast and layer-isolated by construction, not just by convention.

---

## 3. Framework, Mocking Library, and Assertion Library

| Concern | Standard | Rationale |
|---|---|---|
| **Test framework** | **xUnit** (`[Fact]`, `[Theory]`) | Already the framework used in the AAA example in `coding-guidelines.md` §9 — adopting it here keeps the two documents consistent rather than introducing a second framework. |
| **Mocking library** | **Moq** | The de facto standard mocking library for .NET, with the broadest documentation/community support and a `Setup`/`Verify` API that maps directly onto this project's constructor-injected abstractions (`IRepository<T>`, `IUnitOfWork`, `IShortCodeGenerator`, etc. — see `design-guidelines.md` §2, §6). No source-generator or additional build-time tooling required, keeping the test project simple. |
| **Assertion library** | **xUnit's built-in `Assert`** | `coding-guidelines.md` §9 already illustrates `Assert.Equal(...)` in its canonical AAA example; standardizing on it avoids adding a second assertion DSL (e.g., FluentAssertions) for marginal fluency gains. This is a deliberate choice, not an oversight — FluentAssertions was considered and rejected to keep the test-project dependency surface minimal, consistent with the "avoid unnecessary allocations/dependencies" spirit of the coding guidelines. |
| **Coverage tooling** | `coverlet.collector` via `dotnet test --collect:"XPlat Code Coverage"` | Standard .NET SDK-integrated coverage collector; no extra CI-specific tooling needed. |

NuGet packages per test project: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, plus `Moq` for `UrlShortener.Application.Tests`. `UrlShortener.Domain.Tests` generally does not need `Moq` (see §4.1) unless a domain service takes an injected collaborator.

---

## 4. What Gets Unit Tested, and How

### 4.1 Domain layer (`UrlShortener.Domain.Tests`)

Tests entity and value-object **behavior in isolation**, with no mocks, no DI container, and no `AuditableEntity` plumbing:

- Business rules and invariants on `ShortUrl` (e.g., a short code cannot be reassigned after creation, expiration/deactivation state transitions per **AF-06/AF-07**), constructed directly with `new`.
- Value-object validation logic (e.g., a `Url`/`OriginalUrl` value object enforcing the well-formed-URL rule behind **AF-03**), including edge cases (empty, malformed, non-http(s) scheme).
- Pure domain calculations (e.g., click-count increment logic behind **AF-09**, if modeled as a domain method rather than a raw property setter).

**Exception:** Domain unit tests deliberately do **not** assert on `AuditableEntity` base-class fields (`CreatedAtUtc`, `RowVersion`, `IsDeleted`, etc., per `data-design-guidelines.md` §3–§5). Those are populated by the `SaveChangesAsync` override/interceptor in `Infrastructure`, not by domain logic — asserting them here would be testing another layer's behavior through the wrong door. That plumbing is verified in the integration testing design instead.

### 4.2 Application layer (`UrlShortener.Application.Tests`)

Tests application services (e.g., `IShortUrlService`) **with every collaborator mocked** — no real database, no real `AppDbContext`, no EF Core provider of any kind:

- `IRepository<T>` and `IUnitOfWork` (per `design-guidelines.md` §2) are mocked with Moq so a service test verifies *orchestration* (which repository calls happen, in what order, with what arguments, whether `SaveChangesAsync` is called) without caring how persistence is implemented.
- Collaborators like `IShortCodeGenerator` (the Strategy-pattern short-code algorithm from `design-guidelines.md` §8) are mocked so the service's collision-retry/orchestration logic is tested deterministically, independent of the actual generation algorithm.
- Request/response DTO validation and entity↔DTO mapping performed in the `Application` layer (per `design-guidelines.md` §3) is tested directly against the mapping methods.

**Exception:** Unit tests never use EF Core's `InMemory` provider as a "lightweight database" stand-in, even though it is tempting for Application-layer tests. `InMemory` does not enforce relational constraints and does not raise `DbUpdateConcurrencyException` the way SQLite does for the `RowVersion` concurrency token (`data-design-guidelines.md` §4), so it would produce false confidence. Mocking `IRepository<T>`/`IUnitOfWork` directly is the standard for these tests; real EF Core/SQLite behavior is verified only in the integration testing design.

**Exception:** The quality/randomness of short-code generation itself (collision probability, non-enumerability per **ANFR-08**) is not verified by asserting exact generator output in a unit test — that is a property of the concrete `IShortCodeGenerator` implementation, not of the service under test. Application-layer tests inject a deterministic fake/mocked generator and assert the service's *retry-on-collision* orchestration instead.

---

## 5. Explicitly Out of Scope (see `nfr-integration-testing.md`)

The following are **not** covered by unit tests in this project, by design — they require real infrastructure that a unit test (by definition, one class + mocks) cannot meaningfully exercise:

- **EF Core / SQLite query behavior** — `AppDbContext` configuration, migrations, the `RowVersion` concurrency token's actual `DbUpdateConcurrencyException` behavior, the global soft-delete query filter, generated SQL — all belong to `Infrastructure` and are verified against a real (or test-instance) SQLite database in the integration testing design.
- **`UrlShortener.Infrastructure` repository implementations** — `Repository<T>`, `IUnitOfWork` implementation, and any entity-specific repository (e.g., `IShortUrlRepository`) are exercised end-to-end against SQLite in integration tests, not unit-tested against mocks of `DbContext` (mocking `DbContext`/`DbSet<T>` directly is fragile and low-value; testing the real provider is more representative).
- **HTTP pipeline and controllers (`UrlShortener.Api`)** — model binding, routing, middleware (global exception handling, correlation/logging, auth), MVC filters (`design-guidelines.md` §4–§5), and `ProblemDetails` response shaping are verified via integration/functional tests (e.g., `WebApplicationFactory`), not controller unit tests. Per `design-guidelines.md` §3, controllers are intentionally thin (bind → call one `Application` service → map result), so a controller "unit" test with a mocked service would mostly re-verify ASP.NET Core's own wiring rather than any logic owned by this codebase — better value comes from an integration test that exercises the real pipeline.
- **Cross-layer/end-to-end flows** — e.g., "create a short URL then redirect through it" spans `Api` → `Application` → `Infrastructure` → SQLite and is an integration/functional test scenario, not a unit test.

---

## 6. Naming Convention & AAA Structure

Test naming and structure follow `coding-guidelines.md` §9 exactly — not restated here beyond a pointer:

- Naming pattern: `MethodName_Scenario_ExpectedBehavior`.
- Structure: Arrange / Act / Assert, with a blank line (or `// Arrange`, `// Act`, `// Assert` comments) separating each phase.
- One logical behavior per test method; multiple `Assert` calls are fine if they verify the same behavior.
- Tests are independent and repeatable — no shared mutable state, no ordering dependencies (this also means: no shared `Mock<T>` instances across test methods — each test creates its own).

---

## 7. Example: Application-Layer Unit Test

Skeleton for the create-short-URL use case (**AF-01**), showing the naming convention, AAA structure, and mocked `IRepository<T>`/`IUnitOfWork`:

```csharp
using Moq;
using UrlShortener.Application.Contracts;
using UrlShortener.Application.Services;
using UrlShortener.Domain.Entities;
using UrlShortener.Domain.Repositories;
using Xunit;

namespace UrlShortener.Application.Tests.Services;

public class ShortUrlServiceTests
{
    [Fact]
    public async Task CreateAsync_WithValidUrl_ReturnsResponseWithGeneratedCode()
    {
        // Arrange
        var mockRepository = new Mock<IRepository<ShortUrl>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.Repository<ShortUrl>()).Returns(mockRepository.Object);

        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        mockCodeGenerator.Setup(g => g.Generate()).Returns("abc123");

        var service = new ShortUrlService(mockUnitOfWork.Object, mockCodeGenerator.Object);
        var request = new CreateShortUrlRequest { OriginalUrl = "https://example.com/very/long/path" };

        // Act
        var result = await service.CreateAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("abc123", result.Code);
        mockRepository.Verify(
            r => r.AddAsync(It.Is<ShortUrl>(s => s.OriginalUrl == request.OriginalUrl), It.IsAny<CancellationToken>()),
            Times.Once);
        mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidUrl_ThrowsValidationException()
    {
        // Arrange
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        var service = new ShortUrlService(mockUnitOfWork.Object, mockCodeGenerator.Object);
        var request = new CreateShortUrlRequest { OriginalUrl = "not-a-valid-url" };

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(request, CancellationToken.None));
    }
}
```

Note: `mockUnitOfWork.Verify(...)` confirms the service commits through `IUnitOfWork.SaveChangesAsync`, not by calling `SaveChanges` on a repository directly — this keeps the test coupled to the abstraction defined in `design-guidelines.md` §2, not to any particular `Infrastructure` implementation detail.

---

## 8. Summary

| Layer | Unit tested? | Mechanism |
|---|---|---|
| `Domain` | Yes | Direct instantiation, no mocks |
| `Application` | Yes | Moq-mocked `IRepository<T>` / `IUnitOfWork` / other collaborators |
| `Infrastructure` | No (integration only) | See `nfr-integration-testing.md` |
| `Api` | No (integration/functional only) | See `nfr-integration-testing.md` |
| `Common` | Yes, where it holds non-trivial logic | Direct calls to static/pure helper methods; no mocking needed since `Common` has zero dependencies (`design-guidelines.md` §1) |
