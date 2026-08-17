using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UrlShortner.Api.Authentication;

/// <summary>
/// Item 3 / MVP placeholder authentication scheme. Always authenticates the caller as a
/// single fixed placeholder identity so <c>[Authorize(Policy = "Mvp-Bypass")]</c> on the
/// create action does not 401 outright -- there is no real login/credential validation
/// here. See nfr-security.md §10's "Assumed vs. built" table: "a caller identity exists
/// for creation requests" is assumed/mocked for v1, not proven by this handler.
///
/// A real scheme (JWT bearer, cookie, API key -- whichever is chosen later) replaces only
/// this class and its registration in Program.cs; the <c>[Authorize]</c> attribute on
/// <c>ShortUrlsController.CreateAsync</c> and the "Mvp-Bypass" policy shape do not need to
/// change, by construction (the design elements that depend on identity -- rate-limit
/// tiering, audit fields, ownership model -- are built against the identity abstraction,
/// not this mock, per nfr-security.md §10.2).
/// </summary>
internal sealed class MvpPlaceholderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name registered in Program.cs's <c>AddAuthentication(...)</c> call.</summary>
    public const string SchemeName = "MvpPlaceholder";

    private const string PlaceholderUserName = "mvp-placeholder-user";

    public MvpPlaceholderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, PlaceholderUserName) };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
