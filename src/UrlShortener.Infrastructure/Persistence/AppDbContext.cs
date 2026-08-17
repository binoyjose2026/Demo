using Microsoft.EntityFrameworkCore;
using UrlShortener.Domain.Entities;

namespace UrlShortener.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext, targeting SQLite. Owns audit-field/RowVersion stamping (data-design-guidelines.md §3-4)
/// and the global soft-delete query filter (data-design-guidelines.md §5) for every AuditableEntity.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<ShortUrl> ShortUrls => Set<ShortUrl>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ShortUrl>(builder =>
        {
            builder.Property(s => s.Code).IsRequired().HasMaxLength(32);
            builder.Property(s => s.OriginalUrl).IsRequired().HasMaxLength(2048); // Q14

            // Unique index: the authoritative uniqueness guarantee for AF-04's collision
            // handling and the hot lookup path for AF-02. See fn-create.md §11, fn-fetch.md §5,
            // data-design-guidelines.md §7 (index columns used in frequent WHERE clauses).
            builder.HasIndex(s => s.Code).IsUnique().HasDatabaseName("IX_ShortUrl_Code");

            builder.Property(s => s.RowVersion).IsConcurrencyToken();

            // data-design-guidelines.md §5: global filter excludes soft-deleted rows from
            // every standard query without every LINQ call needing to remember to filter.
            builder.HasQueryFilter(s => !s.IsDeleted);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditableEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAuditableEntities();
        return base.SaveChanges();
    }

    // data-design-guidelines.md §3-4: audit fields and RowVersion are set by a common
    // mechanism here, not by each call site remembering to set them by hand.
    private void StampAuditableEntities()
    {
        var utcNow = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = utcNow;
                entry.Entity.RowVersion = 1;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastModifiedAtUtc = utcNow;
                entry.Entity.RowVersion++;
            }
        }
    }
}
