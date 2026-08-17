namespace UrlShortner.Domain.ShortUrls;

/// <summary>
/// Named constants instead of magic numbers, per coding-giudelines.md §4. These are
/// Domain-level invariants (the rules that define what a valid original URL looks like
/// for this system), not Application-layer orchestration -- they have zero dependency on
/// ASP.NET Core, EF Core, or any Application-layer type, so they live in
/// <c>UrlShortner.Domain</c> per global/guidelines/design-guidelines.md §1.
/// </summary>
public static class UrlValidationConstants
{
    public const int MaxOriginalUrlLength = 2048; // Q14
}
