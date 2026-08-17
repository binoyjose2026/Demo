namespace UrlShortener.Domain.Exceptions;

/// <summary>
/// Thrown when the collision-retry budget for system-generated short codes is exhausted
/// (AF-04). Expected to be effectively unreachable given the collision-probability math
/// in documentation/02-design/v1/design/fn-create.md §6; treated as an exceptional
/// condition (500), not a normal validation failure.
///
/// This is a Domain-level concern (a business invariant about the short-code generation
/// process, independent of ASP.NET Core/EF Core/any Application-layer orchestration), so
/// it lives in <c>UrlShortener.Domain</c> per global/guidelines/design-guidelines.md §1.
/// </summary>
public sealed class ShortCodeGenerationException : Exception
{
    public ShortCodeGenerationException(string message)
        : base(message)
    {
    }
}
