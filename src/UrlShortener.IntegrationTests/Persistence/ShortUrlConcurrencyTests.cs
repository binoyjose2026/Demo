using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Domain.Entities;
using UrlShortener.Infrastructure.Persistence;
using Xunit;

namespace UrlShortener.IntegrationTests.Persistence;

/// <summary>
/// Item 17: proves optimistic concurrency actually works on the <c>RowVersion</c> column
/// (data-design-guidelines.md §4) -- exercises <see cref="AppDbContext"/>/EF Core
/// directly, not through an API endpoint, since there is no update endpoint in this MVP's
/// scope (the create/fetch-only surface documented in api-design.md). Two separate
/// <see cref="AppDbContext"/> instances share one open in-memory SQLite connection (the
/// same technique <see cref="UrlShortenerWebApplicationFactory"/> uses), simulating two
/// requests that both loaded the same row before either one wrote back.
/// </summary>
public sealed class ShortUrlConcurrencyTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var setupContext = new AppDbContext(_options);
        await setupContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTwoContextsUpdateSameRowConcurrently_ThrowsDbUpdateConcurrencyException()
    {
        // Arrange -- seed one row and let its RowVersion stamp to 1 (AppDbContext.StampAuditableEntities).
        await using (var seedContext = new AppDbContext(_options))
        {
            seedContext.ShortUrls.Add(new ShortUrl
            {
                Code = "concur1",
                OriginalUrl = "https://example.com/original",
                CreatedBy = "test",
            });
            await seedContext.SaveChangesAsync();
        }

        // Two independent contexts, each loading its own tracked copy of the same row --
        // the same shape as two concurrent HTTP requests each resolving the row via their
        // own per-request-scoped AppDbContext.
        await using var contextA = new AppDbContext(_options);
        await using var contextB = new AppDbContext(_options);

        var entityA = await contextA.ShortUrls.SingleAsync(s => s.Code == "concur1");
        var entityB = await contextB.ShortUrls.SingleAsync(s => s.Code == "concur1");

        // Act -- the first writer succeeds and bumps RowVersion (1 -> 2).
        entityA.OriginalUrl = "https://example.com/first-writer";
        await contextA.SaveChangesAsync();

        // The second writer still holds the stale RowVersion (1) it read before the first
        // writer committed.
        entityB.OriginalUrl = "https://example.com/second-writer-stale";

        // Assert -- EF Core's generated UPDATE includes "WHERE RowVersion = @original"
        // (AppDbContext.OnModelCreating's IsConcurrencyToken() configuration); it matches
        // zero rows and EF Core surfaces that as DbUpdateConcurrencyException instead of
        // silently overwriting the first writer's change.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());
    }
}
