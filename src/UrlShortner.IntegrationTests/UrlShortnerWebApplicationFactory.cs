using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortner.Infrastructure.Persistence;

namespace UrlShortner.IntegrationTests;

/// <summary>
/// Boots the real app via WebApplicationFactory, swapping only the database connection
/// for a private in-memory SQLite connection per test class (real EF Core migrations
/// still run against real SQLite -- nothing else in the pipeline is faked).
/// See: documentation/02-design/v1/design/nfr-integration-testing.md §2-3.
/// </summary>
public sealed class UrlShortnerWebApplicationFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Skips Program.cs's own Database.Migrate() call against the real src/db file --
        // this factory migrates its own private connection below instead
        // (nfr-integration-testing.md §3.2's documented exception).
        builder.UseEnvironment("IntegrationTest");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection?.Dispose();
        }
    }
}
