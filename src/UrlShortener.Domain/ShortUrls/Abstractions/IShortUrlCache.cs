using UrlShortener.Domain.Entities;

namespace UrlShortener.Domain.ShortUrls.Abstractions;

/// <summary>
/// Read-through cache seam for the hot redirect lookup (ANFR-01/ANFR-05/ANFR-06 --
/// the redirect path is the most frequently exercised, lowest-latency-required operation).
///
/// MVP decision: real caching (Redis) is explicitly OUT of scope for this MVP. This
/// interface exists purely as the swap-in seam the prompt asked for; the only registered
/// implementation today is <c>NullShortUrlCache</c> (Infrastructure), which always misses
/// and no-ops on Set/Remove. Swapping in a real distributed cache later is a DI
/// registration change only -- no caller of this interface needs to change.
/// Full design for the real implementation (TTL, invalidation-on-deactivation, cache
/// stampede protection) already exists at:
/// documentation/02-design/v2/design/considerations/07-redis-caching-and-invalidation.md
/// </summary>
public interface IShortUrlCache
{
    /// <summary>Returns the cached mapping, or null on a cache miss (always null for the MVP null cache).</summary>
    Task<ShortUrl?> GetAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Caches a resolved mapping. No-op for the MVP null cache.</summary>
    Task SetAsync(ShortUrl shortUrl, CancellationToken cancellationToken = default);
}
