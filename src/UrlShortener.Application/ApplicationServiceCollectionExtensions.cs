using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application.Common;
using UrlShortener.Application.ShortUrls;

namespace UrlShortener.Application;

/// <summary>
/// Application-layer DI registration, called from Program.cs so the composition root
/// stays a short list of per-layer calls. See: global/guidelines/design-guidelines.md §6.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Item 7 (startup config validation): ValidateDataAnnotations() enforces
        // ShortUrlOptions's [Required]/[Url] attributes; ValidateOnStart() runs that
        // validation eagerly during host startup (via a registered IHostedService) rather
        // than lazily on first IOptions<ShortUrlOptions> access, so a bad "ShortUrl"
        // config section fails fast at boot with a clear error instead of misbehaving
        // (e.g. building a malformed short URL) the first time CreateAsync runs.
        services
            .AddOptions<ShortUrlOptions>()
            .Bind(configuration.GetSection(ShortUrlOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped (item 8 DI-lifetime audit, confirmed intentional): each holds no state
        // itself but is registered per-request alongside the Scoped repositories/DbContext
        // they depend on (design-guidelines.md §6).
        services.AddScoped<IShortUrlService, ShortUrlService>();
        services.AddScoped<IShortUrlResolverService, ShortUrlResolverService>();

        return services;
    }
}
