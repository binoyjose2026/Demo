using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using UrlShortener.Application.ShortUrls;
using UrlShortener.Domain.Entities;
using UrlShortener.Domain.Repositories;
using UrlShortener.Domain.ShortUrls;
using Xunit;

namespace UrlShortener.Application.Tests.ShortUrls;

/// <summary>
/// Unit tests for <see cref="ShortUrlResolverService"/> (AF-02, AF-06) with the repository
/// and cache mocked -- no real database. Naming/AAA per coding-giudelines.md §9.
/// </summary>
public class ShortUrlResolverServiceTests
{
    [Fact]
    public async Task ResolveAsync_WithExistingCode_ReturnsResolvedWithOriginalUrl()
    {
        // Arrange
        var shortUrl = new ShortUrl { Code = "abc1234", OriginalUrl = "https://example.com/target" };

        var mockCache = new Mock<IShortUrlCache>();
        mockCache.Setup(c => c.GetAsync("abc1234", It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);

        var mockRepository = new Mock<IShortUrlRepository>();
        mockRepository.Setup(r => r.GetByCodeAsync("abc1234", It.IsAny<CancellationToken>())).ReturnsAsync(shortUrl);

        var service = new ShortUrlResolverService(mockRepository.Object, mockCache.Object, NullLogger<ShortUrlResolverService>.Instance);

        // Act
        var result = await service.ResolveAsync("abc1234", CancellationToken.None);

        // Assert
        Assert.Equal(ShortUrlResolutionStatus.Resolved, result.Status);
        Assert.Equal("https://example.com/target", result.OriginalUrl);
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownCode_ReturnsNotFound()
    {
        // Arrange
        var mockCache = new Mock<IShortUrlCache>();
        mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);

        var mockRepository = new Mock<IShortUrlRepository>();
        mockRepository.Setup(r => r.GetByCodeAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);

        var service = new ShortUrlResolverService(mockRepository.Object, mockCache.Object, NullLogger<ShortUrlResolverService>.Instance);

        // Act
        var result = await service.ResolveAsync("missing", CancellationToken.None);

        // Assert
        Assert.Equal(ShortUrlResolutionStatus.NotFound, result.Status);
        Assert.Null(result.OriginalUrl);
    }

    [Fact]
    public async Task ResolveAsync_WhenRepositoryReturnsResult_CachesItForNextLookup()
    {
        // Arrange -- verifies the cache seam is actually wired end-to-end even though the
        // MVP registers a no-op NullShortUrlCache (Infrastructure/Caching/NullShortUrlCache.cs).
        var shortUrl = new ShortUrl { Code = "cached01", OriginalUrl = "https://example.com/cache-me" };

        var mockCache = new Mock<IShortUrlCache>();
        mockCache.Setup(c => c.GetAsync("cached01", It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);

        var mockRepository = new Mock<IShortUrlRepository>();
        mockRepository.Setup(r => r.GetByCodeAsync("cached01", It.IsAny<CancellationToken>())).ReturnsAsync(shortUrl);

        var service = new ShortUrlResolverService(mockRepository.Object, mockCache.Object, NullLogger<ShortUrlResolverService>.Instance);

        // Act
        await service.ResolveAsync("cached01", CancellationToken.None);

        // Assert
        mockCache.Verify(c => c.SetAsync(shortUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_WithEmptyOrWhitespaceCode_ReturnsNotFoundWithoutQueryingCacheOrRepository(string code)
    {
        // Arrange -- edge case: an empty/malformed short-code segment can never map to a
        // stored short URL, so it should short-circuit as NotFound without a cache/DB round trip.
        var mockCache = new Mock<IShortUrlCache>();
        var mockRepository = new Mock<IShortUrlRepository>();

        var service = new ShortUrlResolverService(mockRepository.Object, mockCache.Object, NullLogger<ShortUrlResolverService>.Instance);

        // Act
        var result = await service.ResolveAsync(code, CancellationToken.None);

        // Assert
        Assert.Equal(ShortUrlResolutionStatus.NotFound, result.Status);
        mockCache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockRepository.Verify(r => r.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
