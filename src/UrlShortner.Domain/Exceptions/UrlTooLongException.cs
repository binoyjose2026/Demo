using UrlShortner.Domain.ShortUrls;

namespace UrlShortner.Domain.Exceptions;

/// <summary>
/// Thrown when the submitted original URL exceeds <see cref="UrlValidationConstants.MaxOriginalUrlLength"/>
/// (Q14). A distinct type rather than a generic <see cref="ValidationAppException"/> so
/// this failure reason can be told apart from a missing/malformed URL or a disallowed scheme.
/// </summary>
public sealed class UrlTooLongException : ValidationAppException
{
    public UrlTooLongException(int maxLength, string fieldName)
        : base($"Original URL must not exceed {maxLength} characters.", fieldName)
    {
    }
}
