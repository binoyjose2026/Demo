using Microsoft.EntityFrameworkCore;
using UrlShortner.Domain.Entities;
using UrlShortner.Domain.Repositories;
using UrlShortner.Infrastructure.Persistence;
using UrlShortner.Infrastructure.Repositories;

namespace UrlShortner.Infrastructure.ShortUrls;

/// <summary>
/// EF Core implementation of <see cref="IShortUrlRepository"/>.
/// See: documentation/02-design/v1/design/fn-create.md §11, fn-fetch.md §5.
/// </summary>
public sealed class ShortUrlRepository : Repository<ShortUrl>, IShortUrlRepository
{
    public ShortUrlRepository(AppDbContext dbContext)
        : base(dbContext)
    {
    }

    public Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        DbSet.AnyAsync(s => s.Code == code, cancellationToken);

    public Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        DbSet.FirstOrDefaultAsync(s => s.Code == code, cancellationToken);
}
