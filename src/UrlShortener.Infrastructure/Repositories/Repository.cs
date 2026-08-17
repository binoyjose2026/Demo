using Microsoft.EntityFrameworkCore;
using UrlShortener.Domain.Entities;
using UrlShortener.Domain.Repositories;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure.Repositories;

/// <summary>
/// Generic EF Core-backed implementation of <see cref="IRepository{T}"/>.
/// See: global/guidelines/design-guidelines.md §2.
/// </summary>
public class Repository<T> : IRepository<T> where T : AuditableEntity
{
    protected readonly AppDbContext DbContext;
    protected readonly DbSet<T> DbSet;

    public Repository(AppDbContext dbContext)
    {
        DbContext = dbContext;
        DbSet = dbContext.Set<T>();
    }

    public Task<T?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        DbSet.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default) =>
        await DbSet.ToListAsync(cancellationToken);

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default) =>
        await DbSet.AddAsync(entity, cancellationToken);

    public void Update(T entity) => DbSet.Update(entity);

    public void Delete(T entity)
    {
        // Soft delete per data-design-guidelines.md §5 -- never a hard DELETE.
        entity.IsDeleted = true;
        entity.DeletedAtUtc = DateTime.UtcNow;
        DbSet.Update(entity);
    }
}
