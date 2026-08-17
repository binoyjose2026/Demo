using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Application.ShortUrls.Dtos;
using Xunit;

namespace UrlShortener.IntegrationTests.ShortUrls;

/// <summary>
/// End-to-end create -> fetch/redirect flow through the real ASP.NET Core pipeline
/// (routing, model binding, DI, EF Core/SQLite) -- proves the layers are wired together
/// correctly, not business-rule branches (those are covered by the unit tests).
/// See: documentation/02-design/v1/design/nfr-integration-testing.md §4, §7.
/// </summary>
public class ShortUrlsEndpointTests : IClassFixture<UrlShortenerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ShortUrlsEndpointTests(UrlShortenerWebApplicationFactory factory)
    {
        // No auto-redirect so the redirect response itself can be asserted rather than followed.
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task CreateShortUrl_WithValidUrl_ThenFetch_RedirectsToOriginalUrl()
    {
        // Arrange -- AF-01, AF-02: create then fetch/redirect through it.
        var request = new CreateShortUrlRequest
        {
            OriginalUrl = $"https://example.com/{Guid.NewGuid()}",
        };

        // Act -- create
        var createResponse = await _client.PostAsJsonAsync("/api/v1/short-urls", request);

        // Assert -- create
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ShortUrlResponse>();
        Assert.NotNull(created);
        Assert.False(string.IsNullOrWhiteSpace(created!.Code));
        Assert.Equal(request.OriginalUrl, created.OriginalUrl);

        // Act -- fetch/redirect
        var redirectResponse = await _client.GetAsync($"/{created.Code}");

        // Assert -- fetch/redirect
        Assert.Equal(HttpStatusCode.Found, redirectResponse.StatusCode);
        Assert.Equal(request.OriginalUrl, redirectResponse.Headers.Location?.ToString());
    }

    [Fact]
    public async Task CreateShortUrl_WithMalformedUrl_ReturnsValidationProblemDetails()
    {
        // Arrange -- AF-03.
        var request = new CreateShortUrlRequest { OriginalUrl = "not-a-url" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/short-urls", request);

        // Assert -- standard error response shape (design-guidelines.md §3).
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(nameof(CreateShortUrlRequest.OriginalUrl), problem!.Errors.Keys);
    }

    [Fact]
    public async Task Fetch_WithUnknownCode_ReturnsNotFound()
    {
        // Arrange -- AF-06. All base62 characters (item 16), so the request reaches the
        // repository lookup rather than being rejected by the format check.
        var unknownCode = $"missing{Guid.NewGuid():N}";

        // Act
        var response = await _client.GetAsync($"/{unknownCode}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateShortUrl_WithMissingUrl_ReturnsValidationProblemDetails()
    {
        // Arrange -- edge case: null/empty submitted URL (AF-03).
        var request = new CreateShortUrlRequest { OriginalUrl = string.Empty };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/short-urls", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(nameof(CreateShortUrlRequest.OriginalUrl), problem!.Errors.Keys);
    }

    [Fact]
    public async Task CreateShortUrl_WithDisallowedScheme_ReturnsValidationProblemDetails()
    {
        // Arrange -- edge case: disallowed scheme (javascript:), per nfr-security.md §3.1.
        var request = new CreateShortUrlRequest { OriginalUrl = "javascript:alert(1)" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/short-urls", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(nameof(CreateShortUrlRequest.OriginalUrl), problem!.Errors.Keys);
    }

    [Fact]
    public async Task CreateShortUrl_WithUrlExceedingMaxLength_ReturnsValidationProblemDetails()
    {
        // Arrange -- edge case: URL exceeding max length (Q14).
        var longUrl = "https://example.com/" + new string('a', 2048);
        var request = new CreateShortUrlRequest { OriginalUrl = longUrl };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/short-urls", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(nameof(CreateShortUrlRequest.OriginalUrl), problem!.Errors.Keys);
    }

    [Fact]
    public async Task Fetch_WithEmptyCodeSegment_ReturnsNotFound()
    {
        // Arrange -- edge case: an empty short-code segment. No route matches "/" itself
        // (RedirectController is mapped at "/{code}", requiring a non-empty segment), so
        // this proves the app-level result is still a clean 404, not a 500, even though
        // ShortUrlResolverService's own empty/whitespace guard is never reached here --
        // that guard is exercised directly by ShortUrlResolverServiceTests instead.
        // Act
        var response = await _client.GetAsync("/");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Fetch_WithMalformedCodeSegment_ReturnsBadRequest()
    {
        // Arrange -- item 16: a short-code segment containing characters outside the
        // generator's base62 alphabet (here, '-' and '!') can never match a stored
        // mapping, so it is rejected with a clean 400 before reaching the repository/DB,
        // rather than falling through to a 404 lookup miss.
        // Act
        var response = await _client.GetAsync("/not-a-real-code!");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Fetch_WithValidAlphabetButUnknownCode_ReturnsNotFound()
    {
        // Arrange -- item 16's counterpart case: a code drawn entirely from the base62
        // alphabet but not present in the store must still resolve as a normal 404, not
        // a 400 -- the format check only rejects impossible codes, never plausible ones.
        var unknownCode = $"za{Guid.NewGuid():N}".Substring(0, 12);

        // Act
        var response = await _client.GetAsync($"/{unknownCode}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
