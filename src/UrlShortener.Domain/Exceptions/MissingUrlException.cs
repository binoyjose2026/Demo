namespace UrlShortener.Domain.Exceptions;

/// <summary>
/// Thrown when the submitted original URL is null, empty, or whitespace-only (AF-03).
/// A distinct type rather than a generic <see cref="ValidationAppException"/> so callers
/// (logging, tests, future targeted handling) can tell this specific failure reason apart
/// from a malformed URI, a disallowed scheme, or an over-length URL.
/// </summary>
public sealed class MissingUrlException : ValidationAppException
{
    public MissingUrlException(string fieldName)
        : base("Original URL is required.", fieldName)
    {
    }
}
