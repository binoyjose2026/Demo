using UrlShortener.Application.ShortUrls.Dtos;

namespace UrlShortener.Application.ShortUrls.Services;

/// <summary>Application-layer use case: create a new short URL (AF-01).</summary>
public interface IShortUrlService
{
    Task<ShortUrlResponse> CreateAsync(CreateShortUrlRequest request, CancellationToken cancellationToken = default);
}
