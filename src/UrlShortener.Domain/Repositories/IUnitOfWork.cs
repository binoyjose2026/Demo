using UrlShortener.Domain.Entities;

namespace UrlShortener.Domain.Repositories;

/// <summary>
/// Coordinates one or more repositories under a single SaveChangesAsync call.
/// See: engineering-standards/guidelines/design-guidelines.md §2.
/// </summary>
public interface IUnitOfWork
{
    IRepository<T> Repository<T>() where T : AuditableEntity;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
