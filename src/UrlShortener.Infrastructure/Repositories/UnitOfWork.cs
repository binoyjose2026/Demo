using UrlShortener.Domain.Entities;
using UrlShortener.Domain.Repositories;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure.Repositories;

/// <summary>
/// Coordinates repositories under a single AppDbContext/SaveChangesAsync call.
/// See: engineering-standards/guidelines/design-guidelines.md §2.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _dbContext;
    private readonly Dictionary<Type, object> _repositories = new();

    public UnitOfWork(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IRepository<T> Repository<T>() where T : AuditableEntity
    {
        if (_repositories.TryGetValue(typeof(T), out var existing))
        {
            return (IRepository<T>)existing;
        }

        var repository = new Repository<T>(_dbContext);
        _repositories[typeof(T)] = repository;
        return repository;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
