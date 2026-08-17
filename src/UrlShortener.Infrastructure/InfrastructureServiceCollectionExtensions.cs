using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Domain.Repositories;
using UrlShortener.Domain.ShortUrls.Abstractions;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Messaging;
using UrlShortener.Infrastructure.Persistence;
using UrlShortener.Infrastructure.Repositories;
using UrlShortener.Infrastructure.ShortUrls;

namespace UrlShortener.Infrastructure;

/// <summary>
/// Infrastructure-layer DI registration, called from Program.cs.
/// See: engineering-standards/guidelines/design-guidelines.md §6.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, string sqliteConnectionString)
    {
        // Scoped (item 8 DI-lifetime audit, confirmed intentional): one AppDbContext per
        // HTTP request -- EF Core's DbContext is not thread-safe for concurrent use, so it
        // and everything that wraps it (repositories, IUnitOfWork) must never be Singleton
        // (design-guidelines.md §6).
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(sqliteConnectionString));

        // Scoped (item 8, confirmed intentional): wraps the Scoped AppDbContext above --
        // sharing one instance's change tracker per request is exactly the point.
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IShortUrlRepository, ShortUrlRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Transient (item 8, confirmed intentional): stateless, cheap-to-create strategy
        // (design-guidelines.md §6) -- safe to construct a new instance per resolution.
        services.AddTransient<IShortCodeGenerator, RandomBase62ShortCodeGenerator>();

        // Singleton (item 8, confirmed intentional): the MVP's NULL Redis placeholder
        // (see NullShortUrlCache.cs). Safe because it holds no state at all; a real
        // Redis-backed implementation would typically also be Singleton (wrapping a
        // shared IConnectionMultiplexer).
        services.AddSingleton<IShortUrlCache, NullShortUrlCache>();

        // Singleton (item 8, confirmed intentional; item 4 NullKafka seam): the MVP's
        // NULL Kafka placeholder (see NullShortUrlEventPublisher.cs). Safe because it
        // holds no state; a real Kafka producer wrapper (IProducer<TKey,TValue>) is also
        // typically Singleton -- it's thread-safe and expensive to construct per call.
        services.AddSingleton<IShortUrlEventPublisher, NullShortUrlEventPublisher>();

        return services;
    }
}
