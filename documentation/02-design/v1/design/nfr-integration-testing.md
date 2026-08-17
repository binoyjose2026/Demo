# Integration Testing Design

**Non-functional concern:** Testability / Quality Assurance
**Applies to:** `UrlShortner.Api` (host), exercising `Application`, `Infrastructure`, and `Domain` together through the real ASP.NET Core pipeline.
**Consistent with:** [Coding Guidelines §9 Testing Conventions](../../../../global/guidelines/coding-giudelines.md), [Data Design Guidelines §1, §8](../../../../global/guidelines/data-design-guidelines.md), [Design Guidelines §3–§5](../../../../global/guidelines/design-guidelines.md).

---

## 1. Purpose and Scope

Unit tests (see the companion unit testing document, `nfr-unit-testing.md`) verify a single class's logic in isolation with its dependencies faked. Integration tests exist for a different, non-overlapping purpose: **prove that the pieces are wired together correctly** — DI registrations resolve, middleware runs in the right order, MVC filters fire, routing matches, EF Core mappings/migrations are valid against a real SQLite schema, and a real HTTP request produces the response contract a client actually receives.

This document does not re-test business-rule branches already covered by unit tests; it tests the *seams* between layers described in the [Design Guidelines](../../../../global/guidelines/design-guidelines.md) — Api → Application → Infrastructure → Domain — end to end.

---

## 2. Test Host: `WebApplicationFactory<TEntryPoint>` / `TestServer`

- Every integration test suite boots the application in-process using ASP.NET Core's `WebApplicationFactory<Program>` (from `Microsoft.AspNetCore.Mvc.Testing`), which hosts the app on an in-memory `TestServer`.
- This runs the **full real pipeline** as configured in `Program.cs`: the global exception-handling middleware, correlation-ID/logging middleware, authentication/authorization middleware, MVC action/exception/authorization/result filters (Design Guidelines §4–§5), routing, model binding, controllers, DI container, and EF Core — nothing here is mocked or bypassed. This is the entire point of an integration test versus a unit test: no layer is replaced except the one described in §3 below (the database).
- A single custom factory, `UrlShortnerWebApplicationFactory : WebApplicationFactory<Program>`, is shared across a test class via `IClassFixture<T>` (xUnit) so the host starts once per test class, not once per test — keeping suite runtime reasonable while each individual test still gets an isolated database (§3).
- `factory.CreateClient()` returns a real `HttpClient` making real HTTP requests against the in-memory server — tests assert on actual status codes, headers, and JSON bodies, exactly as an external caller would observe them.

---

## 3. Test Database Strategy for SQLite

### 3.1 The strategy

**Each test class gets its own private, ephemeral SQLite database, created fresh and migrated at factory start-up, and disposed at the end of the test class.**

Implementation: inside `UrlShortnerWebApplicationFactory.ConfigureWebHost`, remove the app's registered `AppDbContext`/`DbContextOptions<AppDbContext>` service descriptors and re-register `AppDbContext` against a private connection:

- **Default / preferred:** an **in-memory SQLite connection** (`Microsoft.Data.Sqlite`, `DataSource=:memory:`) opened once and kept open for the lifetime of the factory (SQLite's in-memory database is destroyed the moment its one connection closes, so the `SqliteConnection` instance itself — not a connection string — is what's passed to `UseSqlite(connection)`). This is the fastest option and requires no disk cleanup.
- **Alternative, when a test specifically needs real file-backed SQLite behavior** (e.g., verifying file-locking/concurrency edge cases called out in Data Design Guidelines §1's SQLite trade-offs, or testing `Database.Migrate()` against an actual file): a **temp-file SQLite database** created under the OS temp directory with a unique GUID-based file name per test class, deleted in the factory's `Dispose`/teardown.
- In both cases, `context.Database.Migrate()` is invoked during factory start-up (not `EnsureCreated()`), so integration tests also validate that the project's real EF Core migrations (Data Design Guidelines §8) apply cleanly — the same migrations that will run against the shipped `App.db` file.

```csharp
protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    builder.ConfigureServices(services =>
    {
        services.RemoveAll<DbContextOptions<AppDbContext>>();

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

        using var scope = services.BuildServiceProvider().CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    });
}
```

### 3.2 Exception — deliberate deviation from production, documented

> **Exception:** Data Design Guidelines §1 standardizes on a single shipped SQLite file (`App.db`) committed alongside the app. Integration tests intentionally **do not** use that file. Rationale:
> - **Isolation and repeatability** (Coding Guidelines §9: "tests should be independent and repeatable — no shared mutable state or ordering dependencies between tests"). Sharing the shipped file across test runs — or across parallel test classes — would let one test's data leak into another's assertions and would corrupt the file developers actually run the app against locally.
> - **No pollution of the shipped artifact.** The `App.db` committed to the repo must stay whatever state the team intends to ship; tests must never write to it.
> - **Speed.** An in-memory connection avoids disk I/O per test class.
>
> This is a **test-only** substitution of the database *connection*, not of the database *engine* or *schema path* — tests still run against real SQLite via the real `Microsoft.Data.Sqlite` provider and real EF Core migrations, so schema drift and provider-specific behavior (Data Design Guidelines §1's "avoid raw SQL that assumes SQL Server/PostgreSQL behavior") are still caught. Only the connection target changes. This is called out explicitly here rather than left as a silent inconsistency between test and production configuration.

### 3.3 Isolation between tests within a class

- Tests within a class share the class-level in-memory connection/schema but must not assume ordering or leak state between tests (Coding Guidelines §9). Achieve this by either:
  - having each test create its own short URL(s) with unique input (e.g., a GUID-suffixed long URL) so assertions never collide with another test's rows, or
  - wrapping each test in a transaction that is rolled back afterward, if test volume in a class grows large enough that transaction overhead is worth it.
- The default approach for this project is the first (unique-per-test data), since it's simpler and matches the "minimal, readable test data" guidance in Coding Guidelines §9.

---

## 4. Flows Covered End-to-End

Integration tests target complete request/response round-trips that cross Api → Application → Infrastructure → Domain, tied to the functional requirements they validate:

| Flow | Requirements | What's asserted |
|---|---|---|
| **Create → Fetch/Redirect → Analytics** (the primary happy-path flow) | AF-01 (create), AF-02 (redirect), AF-08 (record access event), AF-09 (click count increments) | `POST` create returns `201 Created` with a short code; a subsequent `GET` to that code returns a redirect to the original long URL; a subsequent analytics `GET` reflects an incremented access count. |
| **Create with invalid input** | AF-03 | `POST` with a malformed URL returns a validation error shaped as `ValidationProblemDetails` (see §5). |
| **Fetch/redirect for unknown or deactivated code** | AF-06, AF-07 | `GET` for a nonexistent or removed code returns the defined not-found/expired response, not a 500. |
| **Retrieve metadata** | AF-05 | `GET` metadata for a known short code returns original URL, creation date, status. |
| **Retrieve analytics** | AF-10 | `GET` analytics for a known short code returns click count and last-accessed timestamp, matching what the redirect flow actually recorded. |

Deliberately **not** re-covered here: exhaustive validation-rule permutations, short-code collision-handling branches, or repository query edge cases — those belong in the unit testing document, since they're pure logic that doesn't require a live HTTP pipeline or database to verify (see §6).

---

## 5. Asserting the Standard Error Response Shape

Design Guidelines §3 and §4 standardize on `ProblemDetails` (RFC 7807) — produced either by the global exception-handling middleware for unhandled exceptions, or as `ValidationProblemDetails` for model-state validation failures — as the uniform error response shape for all non-2xx responses.

Integration tests are the layer responsible for proving this contract holds for a real HTTP response, since it depends on middleware/filter wiring that unit tests don't exercise:

- Assert the HTTP status code matches the failure (`400` for validation, `404` for not-found, `500`→translated for unhandled exceptions once the middleware is implemented).
- Assert `Content-Type: application/problem+json`.
- Deserialize the body into `ProblemDetails`/`ValidationProblemDetails` and assert on `title`, `status`, and (for validation) the `errors` dictionary — not just that *some* JSON came back.
- As the correlation-ID middleware placeholder (Design Guidelines §4) is implemented, extend these assertions to check the correlation header is present on both success and error responses; until then, this is a forward-looking note rather than an enforced assertion.

---

## 6. Relationship to Unit Testing

Integration tests complement, not duplicate, `nfr-unit-testing.md`: unit tests isolate and exhaustively cover business/validation logic per class with fakes, while this document's tests prove those classes are correctly wired into the real pipeline and produce the correct end-to-end HTTP/database behavior for a representative set of flows.

---

## 7. Example Integration Test Skeleton

Follows the same naming (`MethodName_Scenario_ExpectedBehavior`) and AAA structure standardized in Coding Guidelines §9, applied here to an end-to-end flow rather than a single method.

```csharp
public class ShortUrlsEndpointTests : IClassFixture<UrlShortnerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ShortUrlsEndpointTests(UrlShortnerWebApplicationFactory factory)
    {
        // Arrange (shared): real in-process pipeline, no auto-redirect so the
        // redirect response itself can be asserted rather than followed.
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task CreateShortUrl_WithValidUrl_RedirectsAndRecordsAnalytics()
    {
        // Arrange
        var request = new CreateShortUrlRequest
        {
            OriginalUrl = $"https://example.com/{Guid.NewGuid()}"
        };

        // Act — create (AF-01)
        var createResponse = await _client.PostAsJsonAsync("/api/short-urls", request);

        // Assert — create
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ShortUrlResponse>();
        Assert.NotNull(created);
        Assert.False(string.IsNullOrWhiteSpace(created!.Code));

        // Act — redirect (AF-02)
        var redirectResponse = await _client.GetAsync($"/{created.Code}");

        // Assert — redirect
        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        Assert.Equal(request.OriginalUrl, redirectResponse.Headers.Location?.ToString());

        // Act — analytics (AF-08, AF-09, AF-10)
        var analyticsResponse = await _client.GetAsync($"/api/short-urls/{created.Code}/analytics");
        var analytics = await analyticsResponse.Content.ReadFromJsonAsync<ShortUrlAnalyticsResponse>();

        // Assert — analytics reflects the redirect that just happened
        Assert.Equal(HttpStatusCode.OK, analyticsResponse.StatusCode);
        Assert.Equal(1, analytics!.AccessCount);
    }

    [Fact]
    public async Task CreateShortUrl_WithMalformedUrl_ReturnsValidationProblemDetails()
    {
        // Arrange
        var request = new CreateShortUrlRequest { OriginalUrl = "not-a-url" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/short-urls", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(nameof(CreateShortUrlRequest.OriginalUrl), problem!.Errors.Keys);
    }
}
```

---

## 8. Summary of Key Decisions

- Use `WebApplicationFactory<Program>` / `TestServer` to run the real ASP.NET Core pipeline (middleware, filters, DI, EF Core) in-process — no layer faked except the database connection.
- Test database: a private SQLite connection (in-memory by default, temp-file when file-specific behavior must be tested) per test class, migrated via real EF Core migrations — an explicitly documented **Exception** to the single shipped-`App.db` convention in the Data Design Guidelines, not a silent inconsistency.
- Isolate tests within a class via unique per-test data rather than shared mutable rows.
- Cover the create → redirect → analytics round-trip (AF-01, AF-02, AF-08–AF-10) plus validation and not-found paths (AF-03, AF-05–AF-07) end-to-end.
- Assert the `ProblemDetails`/`ValidationProblemDetails` contract on every non-2xx response, since only an integration test can prove the middleware/filter wiring actually produces it.
- Complements, does not duplicate, `nfr-unit-testing.md`: unit tests exhaustively cover per-class logic in isolation; integration tests prove the wiring and a representative set of end-to-end flows.
