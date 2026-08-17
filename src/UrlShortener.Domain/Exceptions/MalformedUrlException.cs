namespace UrlShortener.Domain.Exceptions;

/// <summary>
/// Thrown when the submitted original URL does not parse as a well-formed absolute URI
/// (AF-03, <c>Uri.TryCreate(..., UriKind.Absolute, ...)</c> fails). A distinct type rather
/// than a generic <see cref="ValidationAppException"/> so this failure reason can be told
/// apart from a missing URL, a disallowed scheme, or an over-length URL.
/// </summary>
public sealed class MalformedUrlException : ValidationAppException
{
    public MalformedUrlException(string fieldName)
        : base("Original URL must be a well-formed absolute URI.", fieldName)
    {
    }
}
