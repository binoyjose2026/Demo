using UrlShortener.Domain.Entities;
using UrlShortener.Domain.ShortUrls.Abstractions;

namespace UrlShortener.Infrastructure.Caching;

/// <summary>
/// The literal "NULL placeholder for Redis" this MVP was asked for: a no-op
/// <see cref="IShortUrlCache"/> that always misses on read and does nothing on write.
/// Every redirect request falls straight through to the repository/SQLite -- correct
/// but with none of the latency/throughput benefit a real cache would provide
/// (ANFR-01, ANFR-05, ANFR-06).
///
/// This is the one line that needs to change to introduce real caching later: swap this
/// registration in Infrastructure/DependencyInjectionExtensions.cs for a Redis-backed
/// implementation of IShortUrlCache. No caller (ShortUrlResolverService) needs to change,
/// by construction (Dependency Inversion). Full design for the real implementation:
/// documentation/02-design/v2/design/considerations/07-redis-caching-and-invalidation.md
/// </summary>
public sealed class NullShortUrlCache : IShortUrlCache
{
    public Task<ShortUrl?> GetAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult<ShortUrl?>(null);

    public Task SetAsync(ShortUrl shortUrl, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
