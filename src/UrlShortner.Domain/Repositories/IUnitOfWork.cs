using UrlShortner.Domain.Entities;

namespace UrlShortner.Domain.Repositories;

/// <summary>
/// Coordinates one or more repositories under a single SaveChangesAsync call.
/// See: global/guidelines/design-guidelines.md §2.
/// </summary>
public interface IUnitOfWork
{
    IRepository<T> Repository<T>() where T : AuditableEntity;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
