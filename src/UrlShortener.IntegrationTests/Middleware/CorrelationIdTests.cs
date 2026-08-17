using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Application.ShortUrls;
using Xunit;

namespace UrlShortener.IntegrationTests.Middleware;

/// <summary>
/// Item 6 (CorrelationIdMiddleware): end-to-end coverage for the correlation-ID seam --
/// no test anywhere else in the solution exercised this through a real HTTP round trip
/// (echoed response header, and the "traceId" extension GlobalExceptionHandler adds to
/// every ProblemDetails/ValidationProblemDetails response). Uses the literal header name
/// a real client would send rather than referencing the internal
/// <c>CorrelationIdMiddleware</c> type, keeping this a black-box HTTP-contract test.
/// See: documentation/02-design/v3-mvp/design/api-design.md §6.8.
/// </summary>
public class CorrelationIdTests : IClassFixture<UrlShortenerWebApplicationFactory>
{
    private const string CorrelationIdHeaderName = "X-Correlation-Id";

    private readonly HttpClient _client;

    public CorrelationIdTests(UrlShortenerWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Request_WithInboundCorrelationId_EchoesSameIdBackAsResponseHeader()
    {
        // Arrange
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/doesnotexist999");
        request.Headers.Add(CorrelationIdHeaderName, correlationId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert -- the exact inbound value is echoed back unchanged, not regenerated.
        Assert.True(response.Headers.TryGetValues(CorrelationIdHeaderName, out var values));
        Assert.Equal(correlationId, Assert.Single(values!));
    }

    [Fact]
    public async Task Request_WithoutInboundCorrelationId_GeneratesOne()
    {
        // Act
        var response = await _client.GetAsync("/doesnotexist999");

        // Assert -- a caller that sends no X-Correlation-Id still gets one back.
        Assert.True(response.Headers.TryGetValues(CorrelationIdHeaderName, out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    [Fact]
    public async Task ValidationError_WithInboundCorrelationId_SurfacesSameIdAsTraceIdExtension()
    {
        // Arrange -- GlobalExceptionHandler is expected to surface the same correlation ID
        // pushed by CorrelationIdMiddleware as a ProblemDetails.traceId extension, so a
        // caller reporting an error and an operator grepping server-side logs are looking
        // at the same identifier.
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/short-urls")
        {
            Content = JsonContent.Create(new CreateShortUrlRequest { OriginalUrl = "not-a-url" }),
        };
        request.Headers.Add(CorrelationIdHeaderName, correlationId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem!.Extensions.TryGetValue("traceId", out var traceId));
        Assert.Equal(correlationId, traceId?.ToString());
    }
}
