namespace UrlShortener.Application.ShortUrls.Results;

/// <summary>
/// Outcome of resolving a short code (AF-02, AF-06). Kept as an explicit enum (not a
/// bare bool/nullable) so the redirect controller can map each case to the correct HTTP
/// status without re-deriving intent. See: documentation/02-design/v1/design/fn-fetch.md §4.
/// </summary>
public enum ShortUrlResolutionStatus
{
    Resolved,

    /// <summary>Covers both "never existed" and "deactivated/removed" -- see fn-fetch.md §7.3.</summary>
    NotFound,

    // "Expired" is intentionally NOT a member of this MVP's enum: expiration (Q8/Q9) is
    // out of scope for this MVP (see ShortUrl.cs / CreateShortUrlRequest.cs). When it is
    // implemented, add an Expired case here and branch on it in RedirectController exactly
    // as documentation/02-design/v1/design/fn-fetch.md §7.3/§8.2 already specifies
    // (410 Gone instead of 404).
}
