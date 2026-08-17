using System.Security.Cryptography;
using UrlShortener.Domain.ShortUrls.Abstractions;

namespace UrlShortener.Infrastructure.ShortUrls;

/// <summary>
/// v1 "random base62 + collision retry" short-code generator (AF-04, ANFR-08).
/// See: documentation/02-design/v1/design/fn-create.md §6 for the full rationale
/// (non-enumerability, collision-probability math at this project's scale).
///
/// MVP decision: this is the simple v1 approach, not the v2 pre-allocated ID-block /
/// extreme-scale approach in
/// documentation/02-design/v2/design/considerations/01-create-path-extreme-scalability.md
/// -- that would be over-engineering for this MVP's scope.
///
/// Registered Transient (design-guidelines.md §6's own example of a Transient service):
/// stateless and cheap to construct.
/// </summary>
public sealed class RandomBase62ShortCodeGenerator : IShortCodeGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int CodeLength = 7; // fn-create.md §6: 62^7 possible codes

    public string GenerateCandidate()
    {
        Span<char> buffer = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer);
    }
}
