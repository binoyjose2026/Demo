namespace UrlShortner.Domain.ShortUrls;

/// <summary>
/// Domain-level invariant: the base62 alphabet every system-generated short code is drawn
/// from (see <c>RandomBase62ShortCodeGenerator</c>). A route-parameter short code
/// containing any character outside this alphabet can never match a real row, so
/// <c>RedirectController</c> rejects it as a clean <c>400</c> before the request reaches
/// the repository/DB, instead of spending a lookup on input that is malformed by
/// construction. Mirrors the "validate at the boundary" rule already applied to
/// <c>OriginalUrl</c> (nfr-security.md §3) and <c>UrlValidationConstants</c>'s placement
/// in this same namespace.
/// </summary>
public static class ShortCodeValidationConstants
{
    /// <summary>The full set of characters a valid (system-generated) short code may contain.</summary>
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Returns <c>true</c> only if every character in <paramref name="code"/> is drawn from
    /// <see cref="Alphabet"/>. An empty string is not considered valid by this check --
    /// callers that need to treat empty/whitespace specially (see
    /// <c>ShortUrlResolverService</c>) already guard that case separately.
    /// </summary>
    public static bool IsValidFormat(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        foreach (var c in code)
        {
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
