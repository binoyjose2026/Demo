using System.Net;
using Xunit;

namespace UrlShortener.IntegrationTests.HealthChecks;

/// <summary>
/// Item 2 (health checks): end-to-end coverage for the liveness/readiness split -- no
/// test anywhere else in the solution exercised either endpoint through a real HTTP
/// round trip. See: documentation/02-design/v3-mvp/design/api-design.md §6.2,
/// documentation/02-design/v1/design/nfr-reliability-and-availability.md §4.
/// </summary>
public class HealthCheckTests : IClassFixture<UrlShortenerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthCheckTests(UrlShortenerWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Live_ReturnsHealthy()
    {
        // Act -- liveness has no dependency checks (Predicate = _ => false), so this must
        // succeed even without touching the database.
        var response = await _client.GetAsync("/health/live");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsHealthy()
    {
        // Act -- readiness includes the "sqlite-db" AddDbContextCheck, so this also proves
        // the test host's private in-memory SQLite connection is reachable.
        var response = await _client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
