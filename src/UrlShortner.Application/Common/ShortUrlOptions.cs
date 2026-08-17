using System.ComponentModel.DataAnnotations;

namespace UrlShortner.Application.Common;

/// <summary>
/// Strongly-typed configuration for building the fully-qualified short URL returned
/// from create (AF-01). Options pattern per global/guidelines/design-guidelines.md §8,
/// bound from appsettings.json ("ShortUrl" section) instead of a hardcoded/magic string.
///
/// Item 7 (startup config validation): the data annotations below are enforced via
/// <c>.ValidateDataAnnotations().ValidateOnStart()</c> in
/// <see cref="ApplicationServiceCollectionExtensions"/>, so a missing/malformed
/// "ShortUrl:BaseUrl" setting fails fast at boot with a clear error instead of producing
/// a broken "https:///{code}"-shaped response the first time CreateAsync runs.
/// </summary>
public sealed class ShortUrlOptions
{
    public const string SectionName = "ShortUrl";

    /// <summary>Public base URL the short code is appended to, e.g. "https://short.ly".</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "ShortUrl:BaseUrl is required.")]
    [Url(ErrorMessage = "ShortUrl:BaseUrl must be a well-formed absolute URL.")]
    public string BaseUrl { get; set; } = string.Empty;
}
