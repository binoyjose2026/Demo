namespace UrlShortner.Api.Middleware;

/// <summary>
/// Item 10 (nfr-security.md §8-9): adds the baseline security response headers ASP.NET
/// Core does not set by default. HSTS itself is handled by the framework's own
/// <c>UseHsts()</c> middleware (wired in Program.cs for non-Development environments per
/// nfr-security.md §8) -- this middleware adds the two remaining headers the threat model
/// (T9, "standard web vulnerabilities") calls for:
/// <list type="bullet">
/// <item><description><c>X-Content-Type-Options: nosniff</c> -- stops browsers from
/// MIME-sniffing a response into executing as something other than its declared
/// Content-Type.</description></item>
/// <item><description><c>Referrer-Policy: strict-origin-when-cross-origin</c> -- avoids
/// leaking the full request URL (including any query string) to a third-party
/// <c>Location</c> redirect target on the <c>GET /{code}</c> redirect path.</description></item>
/// </list>
/// </summary>
internal sealed class SecurityResponseHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityResponseHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });

        return _next(context);
    }
}
