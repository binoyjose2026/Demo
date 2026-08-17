namespace UrlShortener.Domain.Exceptions;

/// <summary>
/// Base type for a request that fails a Domain-owned validation rule (e.g. URL
/// scheme/length/well-formedness, per fn-create.md §5). Named distinctly from
/// System.ComponentModel.DataAnnotations.ValidationException to avoid ambiguity.
///
/// This is a Domain-level concern (a business invariant about what counts as a valid
/// original URL), not Application-layer orchestration -- it has zero dependency on
/// ASP.NET Core, EF Core, or any Application-layer type -- so it lives in
/// <c>UrlShortener.Domain</c> per global/guidelines/design-guidelines.md §1.
///
/// Left unsealed (rather than one generic exception doing double duty for every failure
/// reason) so each concrete validation failure gets its own distinct, specific type --
/// see <see cref="MissingUrlException"/>, <see cref="MalformedUrlException"/>,
/// <see cref="UnsupportedUrlSchemeException"/>, <see cref="UrlTooLongException"/>.
/// Mapped to 400 (ValidationProblemDetails) by the global exception-handling middleware
/// in UrlShortener.Api -- see global/guidelines/design-guidelines.md §4. Because the
/// mapping switches on this base type, every subclass is mapped automatically without
/// the handler needing to know about each one individually (Open/Closed).
/// </summary>
public class ValidationAppException : Exception
{
    /// <summary>The request field this failure relates to, if any (used to shape ValidationProblemDetails.Errors).</summary>
    public string? FieldName { get; }

    public ValidationAppException(string message, string? fieldName = null)
        : base(message)
    {
        FieldName = fieldName;
    }
}
