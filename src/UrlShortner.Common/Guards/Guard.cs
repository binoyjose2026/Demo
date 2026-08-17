namespace UrlShortner.Common.Guards;

/// <summary>
/// Small, stateless guard-clause helpers shared across layers. Contains no business
/// logic and depends on nothing else in the solution.
/// See: global/guidelines/design-guidelines.md §1 (Common project responsibilities).
/// </summary>
public static class Guard
{
    public static string AgainstNullOrWhiteSpace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
        }

        return value;
    }
}
