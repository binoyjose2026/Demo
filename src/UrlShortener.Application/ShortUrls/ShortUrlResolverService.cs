using Microsoft.Extensions.Logging;
using UrlShortener.Domain.Repositories;
using UrlShortener.Domain.ShortUrls;

namespace UrlShortener.Application.ShortUrls;

/// <summary>
/// Fetch/redirect use case (AF-02, AF-06). The single place resolution business rules
/// live; the controller only maps this result to an HTTP response.
/// See: documentation/02-design/v1/design/fn-fetch.md §4.
/// </summary>
public sealed class ShortUrlResolverService : IShortUrlResolverService
{
    private readonly IShortUrlRepository _repository;

    // MVP cache seam: always a cache miss today (NullShortUrlCache). Reading here first,
    // and writing on a resolved hit below, keeps the seam wired end-to-end even though it
    // is currently a no-op -- swapping in Redis later requires no change to this class.
    // Full design: documentation/02-design/v2/design/considerations/07-redis-caching-and-invalidation.md
    private readonly IShortUrlCache _cache;

    private readonly ILogger<ShortUrlResolverService> _logger;

    public ShortUrlResolverService(IShortUrlRepository repository, IShortUrlCache cache, ILogger<ShortUrlResolverService> logger)
    {
        _repository = repository;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ShortUrlResolutionResult> ResolveAsync(string code, CancellationToken cancellationToken = default)
    {
        // Edge case: an empty/whitespace-only code segment can never map to a stored
        // short URL -- short-circuit as NotFound rather than spending a cache/DB round
        // trip on input that is malformed by construction.
        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("Short URL fetch attempted with an empty or malformed code segment.");
            return new ShortUrlResolutionResult(ShortUrlResolutionStatus.NotFound, null);
        }

        var shortUrl = await _cache.GetAsync(code, cancellationToken);

        if (shortUrl is null)
        {
            shortUrl = await _repository.GetByCodeAsync(code, cancellationToken);

            if (shortUrl is null)
            {
                // Covers both "never existed" and "deactivated/removed" once soft-delete
                // deactivation (AF-07, out of scope for this MVP) is wired up -- the global
                // soft-delete query filter would already exclude those rows here for free.
                _logger.LogInformation("Short URL fetch resulted in not found: {ShortCode}", code);
                return new ShortUrlResolutionResult(ShortUrlResolutionStatus.NotFound, null);
            }

            await _cache.SetAsync(shortUrl, cancellationToken);
        }

        // Expiration check (Q8/Q9, AF-06) would go here -- out of scope for this MVP,
        // see ShortUrlResolutionStatus.cs. Full design: fn-fetch.md §7.1. When added, an
        // expired/deactivated hit would log at this point (e.g. LogInformation with
        // {ShortCode}) and return a distinct ShortUrlResolutionStatus.Expired instead of
        // falling through to Resolved below.

        // AF-08 (record an access event) would fire here as a side effect of a resolved
        // hit -- analytics is out of scope for this MVP. Full design: fn-analytics.md.

        // PII-safe (nfr-security.md §6): logs only the short code, never the destination
        // URL or any caller-identifying data (IP, etc.).
        _logger.LogInformation("Short URL fetch resulted in redirect: {ShortCode}", code);

        return new ShortUrlResolutionResult(ShortUrlResolutionStatus.Resolved, shortUrl.OriginalUrl);
    }
}
