using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Application.ShortUrls;

/// <summary>
/// Request payload to create a new short URL (AF-01). Bound directly from the HTTP request body.
/// DTO at the boundary -- never the Domain entity. See: global/guidelines/design-guidelines.md §3.
/// </summary>
public sealed class CreateShortUrlRequest
{
    /// <summary>The long/original URL to shorten. Required. AF-01, AF-03.</summary>
    [Required]
    public string OriginalUrl { get; set; } = string.Empty;

    // Deliberately NOT included in this MVP request (documented, out-of-scope seams,
    // not silent omissions):
    //   - CustomAlias (vanity alias)   -> documentation/02-design/v1/design/fn-create.md §7 (AF-01, Q20/Q21)
    //   - ExpiresAtUtc (opt-in expiry) -> documentation/02-design/v1/design/fn-create.md §8 (Q8/Q9)
}
