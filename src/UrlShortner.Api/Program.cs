using System.Reflection;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using UrlShortner.Api.Authentication;
using UrlShortner.Api.Middleware;
using UrlShortner.Application;
using UrlShortner.Infrastructure;
using UrlShortner.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// --- Logging --------------------------------------------------------------------------
// Serilog replaces/hosts the default ASP.NET Core logging provider (UseSerilog), reading
// its sink/level configuration from the "Serilog" section of appsettings.json (and
// appsettings.{Environment}.json) via Serilog.Settings.Configuration -- console sink,
// structured message templates (see ShortUrlService/ShortUrlResolverService/
// GlobalExceptionHandler), Information by default with Microsoft/System noise reduced to
// Warning. See: documentation/02-design/v3.MVP/design/exception-and-logging-strategy.md.
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

// --- Services -----------------------------------------------------------------------

builder.Services.AddControllers();

// Item 11: API versioning. Only ShortUrlsController opts in ([ApiVersion("1.0")],
// "api/v{version:apiVersion}/short-urls") -- RedirectController's GET /{code} stays
// unversioned by design (see its own doc comment). AssumeDefaultVersionWhenUnspecified
// is harmless here since every versioned route already pins "1.0" in its own route
// template; it just avoids a hard failure if that ever changes.
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
    })
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

// Item 1: Swagger/OpenAPI. XML doc comments (UrlShortner.Api.csproj's
// GenerateDocumentationFile) feed IncludeXmlComments so Swagger UI shows the real
// <summary>/<param>/<response> text on ShortUrlsController/RedirectController, not just
// auto-generated descriptions. UI itself is only mapped in Development, below.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "UrlShortner API",
        Version = "v1",
        Description = "A minimal URL shortener: create short links (versioned API) and redirect through them (unversioned public surface).",
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }
});

// Standard error response shape for every non-2xx response (design-guidelines.md §3),
// including automatic ValidationProblemDetails for [ApiController] model-binding failures.
builder.Services.AddProblemDetails();

// design-guidelines.md §4: global exception-handling middleware, wired via
// app.UseExceptionHandler() below.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Per-layer registration extensions (design-guidelines.md §6) keep this composition
// root a short list of calls rather than a long flat list of registrations. Item 7:
// AddApplicationServices now also registers ShortUrlOptions with
// ValidateDataAnnotations().ValidateOnStart(), so a bad "ShortUrl" config section fails
// fast at boot.
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddInfrastructureServices(ResolveSqliteConnectionString());

// Item 2: health checks (nfr-reliability-and-availability.md §4). "sqlite-db" is tagged
// "ready" so it is included in /health/ready but not /health/live (mapped below) --
// liveness must never depend on the database per that document's liveness/readiness split.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(name: "sqlite-db", tags: new[] { "ready" });

// Item 3 / MVP placeholder: real ASP.NET Core rate-limiting middleware
// (Microsoft.AspNetCore.RateLimiting), wired to an effectively-unlimited fixed-window
// policy so [EnableRateLimiting("CreatePolicy")] on ShortUrlsController.CreateAsync is
// exercised end-to-end without constraining this MVP's traffic. Real policy shape
// (sliding-window, Redis-backed counters shared across horizontally-scaled instances,
// since in-memory counters like this one are per-instance and do not hold up behind a
// load balancer): documentation/02-design/v2/design/considerations/12-distributed-rate-limiting.md
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("CreatePolicy", limiterOptions =>
    {
        limiterOptions.PermitLimit = int.MaxValue;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Item 3 / MVP placeholder: a minimal authentication scheme that always authenticates the
// caller as a single fixed placeholder identity (see MvpPlaceholderAuthenticationHandler's
// own doc comment), paired with an authorization policy that always succeeds
// (RequireAssertion(_ => true)). Together these let [Authorize(Policy = "Mvp-Bypass")] on
// the create action be a real, wired ASP.NET Core primitive rather than a decorative
// attribute, without requiring a real login system for this MVP -- consistent with
// nfr-security.md §10's "authenticated user context is assumed, not built" scope boundary.
builder.Services
    .AddAuthentication(MvpPlaceholderAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, MvpPlaceholderAuthenticationHandler>(
        MvpPlaceholderAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Mvp-Bypass", policy => policy.RequireAssertion(_ => true));
});

// Item 2: basic OpenTelemetry instrumentation (traces + metrics) for ASP.NET Core and EF
// Core, exported to the console -- a v1/MVP-appropriate seam that proves the
// instrumentation is wired end-to-end. Production would export to an OTEL
// collector/Grafana+Loki+Tempo stack instead of the console, and would add distributed
// trace-context propagation across async/broker boundaries; that infrastructure is
// explicitly NOT built here. Full design:
// documentation/02-design/v2/design/considerations/13-observability-at-scale.md
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("UrlShortner.Api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

// --- Pipeline -------------------------------------------------------------------------
// Order per design-guidelines.md §4 and nfr-security.md §9: exception handling ->
// correlation/logging -> HTTPS redirection/HSTS -> security response headers ->
// authentication -> authorization -> rate limiting -> MVC endpoints.

app.UseExceptionHandler();

// Item 6: correlation ID -- reads/generates X-Correlation-Id, pushes it into the Serilog
// LogContext for the request, echoes it back as a response header. Placed before
// UseSerilogRequestLogging() so that middleware's own summary log line already carries
// {CorrelationId} too.
app.UseMiddleware<CorrelationIdMiddleware>();

// Serilog's structured per-request summary log (method, path, status code, elapsed ms) --
// the "request logging" half of the design-guidelines.md §4 correlation/logging
// placeholder. Placed right after exception handling so it still logs a summary even for
// requests the GlobalExceptionHandler translates into a non-2xx ProblemDetails response.
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "UrlShortner API v1");
    });
}
else
{
    // nfr-security.md §8: HSTS for non-Development environments only (the default
    // ASP.NET Core template guidance -- HSTS actively hostile to local HTTP development).
    app.UseHsts();
}

app.UseHttpsRedirection();

// Item 10: baseline security response headers (X-Content-Type-Options, Referrer-Policy)
// not covered by UseHsts()/UseHttpsRedirection() above.
app.UseMiddleware<SecurityResponseHeadersMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Item 3: pairs with AddRateLimiter/[EnableRateLimiting("CreatePolicy")] above.
app.UseRateLimiter();

app.MapControllers();

// Item 2: liveness vs. readiness split per nfr-reliability-and-availability.md §4.2.
// Liveness: process-level only, no dependency checks -- a DB outage must not make an
// orchestrator think the process itself is dead and restart it needlessly.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});

// Readiness: gates traffic. Includes the SQLite DbContext check registered above, since
// the redirect path (ANFR-01) cannot function without it.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

// data-design-guidelines.md §8: apply pending EF Core migrations on startup so the
// shipped SQLite file always matches the current schema. Skipped for the test host --
// UrlShortnerWebApplicationFactory swaps in its own connection/migration lifecycle
// (nfr-integration-testing.md §3).
if (!app.Environment.IsEnvironment("IntegrationTest"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.Run();

// Resolves the SQLite connection string so the database file always lands at
// src/db/urlshortner.db regardless of the working directory the app is launched from
// (dotnet run vs. the built .exe vs. a test host) -- found by walking up from the
// running assembly's location to the solution (src) directory.
string ResolveSqliteConnectionString()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && directory.GetFiles("*.sln").Length == 0)
    {
        directory = directory.Parent;
    }

    if (directory is null)
    {
        throw new InvalidOperationException("Could not locate the solution (src) directory to resolve the SQLite database path.");
    }

    var dbDirectory = Path.Combine(directory.FullName, "db");
    Directory.CreateDirectory(dbDirectory);

    var dbPath = Path.Combine(dbDirectory, "urlshortner.db");
    return $"Data Source={dbPath}";
}

/// <summary>
/// Exposes the generated Program class publicly so UrlShortner.IntegrationTests can boot
/// this app via WebApplicationFactory&lt;Program&gt; (nfr-integration-testing.md §2).
/// </summary>
public partial class Program
{
}
