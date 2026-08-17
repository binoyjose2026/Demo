using UrlShortner.Domain.Entities;

namespace UrlShortner.Domain.Repositories;

/// <summary>
/// Generic CRUD abstraction over an <see cref="AuditableEntity"/>-derived type.
/// See: global/guidelines/design-guidelines.md §2 (Repository pattern).
/// </summary>
public interface IRepository<T> where T : AuditableEntity
{
    Task<T?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(T entity, CancellationToken cancellationToken = default);

    void Update(T entity);

    /// <summary>Soft delete — sets IsDeleted/DeletedAtUtc, does not physically remove the row.</summary>
    void Delete(T entity);
}
