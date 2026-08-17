using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UrlShortener.Application.Common;
using UrlShortener.Application.ShortUrls;
using UrlShortener.Domain.Entities;
using UrlShortener.Domain.Exceptions;
using UrlShortener.Domain.Repositories;
using UrlShortener.Domain.ShortUrls;
using Xunit;

namespace UrlShortener.Application.Tests.ShortUrls;

/// <summary>
/// Unit tests for <see cref="ShortUrlService"/> (AF-01, AF-03, AF-04) with every
/// collaborator mocked -- no real database. Naming/AAA per coding-guidelines.md §9;
/// scope per nfr-unit-testing.md §4.2.
/// </summary>
public class ShortUrlServiceTests
{
    private static ShortUrlService CreateService(
        Mock<IUnitOfWork> mockUnitOfWork,
        Mock<IShortUrlRepository> mockShortUrlRepository,
        Mock<IShortCodeGenerator> mockCodeGenerator,
        string baseUrl = "https://short.ly")
    {
        var options = Options.Create(new ShortUrlOptions { BaseUrl = baseUrl });

        // Item 4: NullShortUrlEventPublisher-equivalent stub -- CreateAsync's
        // fire-and-forget publish call must complete without a NullReferenceException
        // (Moq returns null, not a completed Task, for an unconfigured Task-returning
        // method) for every test that exercises the happy path.
        var mockEventPublisher = new Mock<IShortUrlEventPublisher>();
        mockEventPublisher
            .Setup(p => p.PublishUrlCreatedAsync(It.IsAny<ShortUrl>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new ShortUrlService(
            mockUnitOfWork.Object,
            mockShortUrlRepository.Object,
            mockCodeGenerator.Object,
            mockEventPublisher.Object,
            options,
            NullLogger<ShortUrlService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_WithValidUrl_ReturnsResponseWithGeneratedCode()
    {
        // Arrange
        var mockRepository = new Mock<IRepository<ShortUrl>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.Repository<ShortUrl>()).Returns(mockRepository.Object);

        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        mockShortUrlRepository.Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        mockCodeGenerator.Setup(g => g.GenerateCandidate()).Returns("abc1234");

        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = "https://example.com/very/long/path" };

        // Act
        var result = await service.CreateAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("abc1234", result.Code);
        Assert.Equal("https://short.ly/abc1234", result.ShortUrl);
        Assert.Equal(request.OriginalUrl, result.OriginalUrl);
        mockRepository.Verify(
            r => r.AddAsync(It.Is<ShortUrl>(s => s.OriginalUrl == request.OriginalUrl && s.Code == "abc1234"), It.IsAny<CancellationToken>()),
            Times.Once);
        mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_WithNullOrEmptyUrl_ThrowsMissingUrlException(string? originalUrl)
    {
        // Arrange -- edge case: null/empty submitted URL (AF-03).
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = originalUrl! };

        // Act & Assert
        await Assert.ThrowsAsync<MissingUrlException>(() => service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithMalformedUrl_ThrowsMalformedUrlException()
    {
        // Arrange
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = "not-a-valid-url" };

        // Act & Assert
        await Assert.ThrowsAsync<MalformedUrlException>(() => service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithNonHttpScheme_ThrowsUnsupportedUrlSchemeException()
    {
        // Arrange -- edge case: disallowed scheme (ftp:).
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = "ftp://example.com/file" };

        // Act & Assert
        await Assert.ThrowsAsync<UnsupportedUrlSchemeException>(() => service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithJavaScriptScheme_ThrowsUnsupportedUrlSchemeException()
    {
        // Arrange -- edge case: disallowed scheme (javascript:), the classic
        // stored-XSS-gadget vector called out in nfr-security.md §3.1.
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = "javascript:alert(1)" };

        // Act & Assert
        await Assert.ThrowsAsync<UnsupportedUrlSchemeException>(() => service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithUrlExceedingMaxLength_ThrowsUrlTooLongException()
    {
        // Arrange -- edge case: URL exceeding max length.
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var longUrl = "https://example.com/" + new string('a', UrlValidationConstants.MaxOriginalUrlLength);
        var request = new CreateShortUrlRequest { OriginalUrl = longUrl };

        // Act & Assert
        await Assert.ThrowsAsync<UrlTooLongException>(() => service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WhenFirstCandidateCollides_RetriesAndReturnsSecondCandidate()
    {
        // Arrange -- AF-04: collision on the first attempt must be retried, not surfaced as an error.
        var mockRepository = new Mock<IRepository<ShortUrl>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.Repository<ShortUrl>()).Returns(mockRepository.Object);

        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        mockShortUrlRepository.SetupSequence(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        mockCodeGenerator.SetupSequence(g => g.GenerateCandidate())
            .Returns("collide1")
            .Returns("unique12");

        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = "https://example.com/retry" };

        // Act
        var result = await service.CreateAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("unique12", result.Code);
        mockCodeGenerator.Verify(g => g.GenerateCandidate(), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateAsync_WhenEveryCandidateCollides_ThrowsShortCodeGenerationException()
    {
        // Arrange -- AF-04: the retry budget is finite; exhausting it is an exceptional condition.
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        mockShortUrlRepository.Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        mockCodeGenerator.Setup(g => g.GenerateCandidate()).Returns("alwayscollide");

        var service = CreateService(mockUnitOfWork, mockShortUrlRepository, mockCodeGenerator);
        var request = new CreateShortUrlRequest { OriginalUrl = "https://example.com/exhausted" };

        // Act & Assert
        await Assert.ThrowsAsync<ShortCodeGenerationException>(() => service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WhenEventPublisherFails_StillReturnsResponseAndLogsWarning()
    {
        // Arrange -- Item 4's fire-and-forget contract: a publish failure must never
        // surface to the caller of CreateAsync (the short URL is already durably
        // committed), it must only be logged. No existing test exercised this catch
        // block at all.
        var mockRepository = new Mock<IRepository<ShortUrl>>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.Repository<ShortUrl>()).Returns(mockRepository.Object);

        var mockShortUrlRepository = new Mock<IShortUrlRepository>();
        mockShortUrlRepository.Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var mockCodeGenerator = new Mock<IShortCodeGenerator>();
        mockCodeGenerator.Setup(g => g.GenerateCandidate()).Returns("failpub1");

        var mockEventPublisher = new Mock<IShortUrlEventPublisher>();
        mockEventPublisher
            .Setup(p => p.PublishUrlCreatedAsync(It.IsAny<ShortUrl>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated publish failure"));

        var warningTrackingLogger = new WarningTrackingLogger();
        var options = Options.Create(new ShortUrlOptions { BaseUrl = "https://short.ly" });

        var service = new ShortUrlService(
            mockUnitOfWork.Object,
            mockShortUrlRepository.Object,
            mockCodeGenerator.Object,
            mockEventPublisher.Object,
            options,
            warningTrackingLogger);

        var request = new CreateShortUrlRequest { OriginalUrl = "https://example.com/publish-failure" };

        // Act -- awaiting a Moq ThrowsAsync task resumes synchronously (it is already
        // completed), so PublishUrlCreatedEventSafelyAsync's catch block has already run
        // by the time CreateAsync returns control here -- no extra synchronization needed.
        var result = await service.CreateAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal("failpub1", result.Code);
        Assert.True(warningTrackingLogger.WarningLogged, "Expected a Warning-level log for the failed publish attempt.");
    }

    /// <summary>
    /// Minimal <see cref="ILogger{TCategoryName}"/> test double that records whether a
    /// Warning-level log was written, without Moq's finicky generic-method verification
    /// syntax for <see cref="ILogger.Log"/>.
    /// </summary>
    private sealed class WarningTrackingLogger : ILogger<ShortUrlService>
    {
        public bool WarningLogged { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningLogged = true;
            }
        }
    }
}
