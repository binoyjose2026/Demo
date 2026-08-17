using System.Net;
using System.Net.Http.Json;
using NBomber.CSharp;

// -----------------------------------------------------------------------------------
// Item 18: UrlShortener.LoadTests -- an NBomber harness with two scenarios, hammering
// the create endpoint and the fetch/redirect endpoint respectively. This project is
// build-only for this task -- it is NOT run/executed as part of this work, and is not
// wired into CI; `dotnet build`/`dotnet test` on the solution must simply succeed with
// this project present (it is a console app, not an xUnit project, so `dotnet test`
// skips it automatically -- there is nothing for the test runner to discover here).
//
// Measuring this MVP against the v2 design's extreme-scale numbers
// (documentation/02-design/v2/design/considerations/01-create-path-extreme-scalability.md,
// .../12-distributed-rate-limiting.md, .../10-database-partitioning-sharding.md, etc.)
// would require the actual v2 infrastructure (Redis caching, distributed rate limiting,
// database partitioning/sharding, horizontally-scaled API instances behind a load
// balancer) to be a meaningful comparison. This MVP is currently a single instance
// backed by SQLite (data-design-guidelines.md §1) -- running this harness against it is
// a smoke/harness check ("does the app survive concurrent load and keep responding
// correctly"), not a scalability benchmark, and should not be read as one.
// -----------------------------------------------------------------------------------

var baseUrl = Environment.GetEnvironmentVariable("LOADTEST_BASE_URL") ?? "http://localhost:5236";

// AllowAutoRedirect = false so the fetch/redirect scenario below can observe the actual
// 302 response this endpoint returns, rather than HttpClient transparently following it
// to whatever the (arbitrary, external) OriginalUrl happens to return.
using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

// Seed one short URL up front so the fetch/redirect scenario has a real, stable code to
// hit repeatedly instead of guessing at one. If the target app isn't reachable yet, fall
// back to a code that will 404 -- the fetch scenario still runs (and reports failures
// honestly) rather than crashing the whole harness at startup.
var seedCode = await TrySeedShortUrlAsync(httpClient, baseUrl);

// --- Scenario 1: hammer the create endpoint (POST /api/v1/short-urls) --------------
var createScenario = Scenario.Create("create_short_url", async scenarioContext =>
{
    var payload = new { originalUrl = $"https://example.com/loadtest/{Guid.NewGuid()}" };
    using var response = await httpClient.PostAsJsonAsync($"{baseUrl}/api/v1/short-urls", payload);

    return response.StatusCode == HttpStatusCode.Created
        ? Response.Ok(statusCode: ((int)response.StatusCode).ToString())
        : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
})
.WithoutWarmUp()
.WithLoadSimulations(
    Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)));

// --- Scenario 2: hammer the fetch/redirect endpoint (GET /{code}) ------------------
var fetchScenario = Scenario.Create("fetch_redirect", async scenarioContext =>
{
    using var response = await httpClient.GetAsync($"{baseUrl}/{seedCode}");

    return response.StatusCode == HttpStatusCode.Found
        ? Response.Ok(statusCode: ((int)response.StatusCode).ToString())
        : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
})
.WithoutWarmUp()
.WithLoadSimulations(
    Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)));

NBomberRunner
    .RegisterScenarios(createScenario, fetchScenario)
    .Run();

static async Task<string> TrySeedShortUrlAsync(HttpClient httpClient, string baseUrl)
{
    try
    {
        using var seedResponse = await httpClient.PostAsJsonAsync(
            $"{baseUrl}/api/v1/short-urls",
            new { originalUrl = "https://example.com/loadtest-seed" });
        seedResponse.EnsureSuccessStatusCode();

        var seedBody = await seedResponse.Content.ReadFromJsonAsync<SeedShortUrlResponse>();
        return seedBody?.Code ?? "seedmissing";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[UrlShortener.LoadTests] Could not seed a short URL against {baseUrl} ({ex.Message}); the fetch_redirect scenario will exercise the 404 path instead.");
        return "seedmissing";
    }
}

/// <summary>Minimal shape matching just the field this harness needs from ShortUrlResponse.</summary>
internal sealed record SeedShortUrlResponse(string Code);
