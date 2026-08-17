using UrlShortner.Domain.Entities;

namespace UrlShortner.Domain.Repositories;

/// <summary>
/// Entity-specific repository for <see cref="ShortUrl"/>, adding exactly the two
/// lookups the create and fetch use cases need beyond generic CRUD.
/// See: global/guidelines/design-guidelines.md §2, documentation/02-design/v1/design/fn-create.md §11,
/// documentation/02-design/v1/design/fn-fetch.md §5.
/// </summary>
public interface IShortUrlRepository : IRepository<ShortUrl>
{
    /// <summary>
    /// Used by both the system-generated collision-retry loop (AF-04) and would be reused
    /// by a future custom-alias uniqueness check (currently out of scope) -- a single
    /// uniqueness mechanism, not two.
    /// </summary>
    Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// AF-02 lookup for the redirect path. Respects the global soft-delete query filter,
    /// so a deactivated/removed code (out of scope for this MVP, see ShortUrl.cs) would
    /// already resolve to null once that feature exists.
    /// </summary>
    Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}
