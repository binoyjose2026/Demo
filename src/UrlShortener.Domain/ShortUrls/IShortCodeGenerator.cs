namespace UrlShortener.Domain.ShortUrls;

/// <summary>
/// Strategy for producing a candidate short code (AF-04). Implementations are swappable
/// via DI without changing the calling application service (Open/Closed).
/// See: engineering-standards/guidelines/design-guidelines.md §8 (Strategy pattern),
/// documentation/02-design/v1/design/fn-create.md §6.
///
/// MVP decision: this project uses the v1 "random base62 + collision retry" approach
/// (see RandomBase62ShortCodeGenerator in Infrastructure), NOT the v2 pre-allocated
/// ID-block / extreme-scale approach in
/// documentation/02-design/v2/design/considerations/01-create-path-extreme-scalability.md
/// and documentation/02-design/v2/design/considerations/28-short-key-generation-approaches.md
/// -- that approach would be over-engineering for this MVP's scope but is the documented
/// upgrade path if this ever needs to scale.
/// </summary>
public interface IShortCodeGenerator
{
    /// <summary>Produces one random candidate code. Does not guarantee uniqueness -- callers must check.</summary>
    string GenerateCandidate();
}
