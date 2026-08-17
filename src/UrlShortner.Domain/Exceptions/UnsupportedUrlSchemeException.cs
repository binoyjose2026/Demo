namespace UrlShortner.Domain.Exceptions;

/// <summary>
/// Thrown when the submitted original URL's scheme is not in the http/https allowlist
/// (Q14, ANFR-07 -- e.g. <c>javascript:</c>, <c>data:</c>, <c>ftp:</c>, <c>file:</c>). A
/// distinct type rather than a generic <see cref="ValidationAppException"/> so this
/// security-relevant failure reason (see nfr-security.md §3.1) can be told apart from a
/// missing/malformed URL or an over-length URL.
/// </summary>
public sealed class UnsupportedUrlSchemeException : ValidationAppException
{
    public UnsupportedUrlSchemeException(string scheme, string fieldName)
        : base($"URL scheme '{scheme}' is not allowed. Only http/https URLs are accepted.", fieldName)
    {
    }
}
