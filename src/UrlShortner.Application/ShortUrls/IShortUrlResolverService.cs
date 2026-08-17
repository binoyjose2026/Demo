namespace UrlShortner.Application.ShortUrls;

/// <summary>Application-layer use case: resolve a short code for redirect (AF-02).</summary>
public interface IShortUrlResolverService
{
    Task<ShortUrlResolutionResult> ResolveAsync(string code, CancellationToken cancellationToken = default);
}
