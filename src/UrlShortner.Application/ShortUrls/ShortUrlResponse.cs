namespace UrlShortner.Application.ShortUrls;

/// <summary>
/// Response returned after a short URL is successfully created.
/// Deliberately excludes persistence concerns (Id, RowVersion, IsDeleted/DeletedAtUtc) --
/// see: global/guidelines/design-guidelines.md §3.
/// </summary>
public sealed class ShortUrlResponse
{
    /// <summary>The fully-qualified short URL a caller can use (e.g. https://short.ly/abc1234).</summary>
    public string ShortUrl { get; set; } = string.Empty;

    /// <summary>The short code alone (e.g. abc1234). AF-04.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Echo of the original long URL.</summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>Creation timestamp, UTC.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
