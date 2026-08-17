namespace UrlShortener.Application.ShortUrls.Results;

/// <summary>Result of a short-code resolution attempt (AF-02). See: fn-fetch.md §4.</summary>
public sealed record ShortUrlResolutionResult(ShortUrlResolutionStatus Status, string? OriginalUrl);
