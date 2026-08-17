using Microsoft.AspNetCore.Mvc;
using UrlShortner.Application.ShortUrls;
using UrlShortner.Domain.ShortUrls;

namespace UrlShortner.Api.Controllers;

/// <summary>
/// AF-02, AF-06: resolve a short code and redirect to its original URL.
/// See: documentation/02-design/v1/design/fn-fetch.md §3-4.
///
/// Design exception (documented, not accidental, per fn-fetch.md §3): this route lives at
/// the application root (GET /{code}), not under /api/..., because the whole point of a
/// short link is a short path. Item 11 (API versioning): for the same reason, this route
/// deliberately stays unversioned -- it is a public short-link surface, not a versioned
/// API contract.
/// </summary>
[ApiController]
public class RedirectController : ControllerBase
{
    private readonly IShortUrlResolverService _resolver;

    public RedirectController(IShortUrlResolverService resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Redirects to the original URL behind <paramref name="code"/>. Returns
    /// <c>400</c> immediately -- without a repository/DB lookup -- if
    /// <paramref name="code"/> contains any character outside the short-code generator's
    /// base62 alphabet, since such a code could never match a stored mapping.
    /// AF-02, AF-06.
    /// </summary>
    /// <param name="code">The short code segment from the URL path.</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    /// <response code="302">Redirects to the original URL.</response>
    /// <response code="400">The code segment contains characters outside the base62 alphabet.</response>
    /// <response code="404">No such short code exists.</response>
    [HttpGet("/{code}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RedirectAsync(string code, CancellationToken cancellationToken)
    {
        // Item 16: a syntactically-invalid code (any character outside the base62
        // alphabet every system-generated code is drawn from) can never resolve to a
        // stored mapping -- reject it with a clean 400 here, before it reaches
        // IShortUrlResolverService/the repository/the DB, instead of falling through to a
        // 404 lookup miss that spends a cache/DB round trip on input that is malformed by
        // construction.
        if (!ShortCodeValidationConstants.IsValidFormat(code))
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["code"] = new[] { "The short code contains characters outside the allowed base62 alphabet." },
                }));
        }

        var result = await _resolver.ResolveAsync(code, cancellationToken);

        return result.Status switch
        {
            // 302 (temporary), not 301, so every access re-hits the server -- see
            // fn-fetch.md §10 for why that matters once expiry/deactivation/analytics
            // (all out of scope for this MVP) exist: a browser-cached 301 would keep
            // redirecting locally even after this server would have said otherwise.
            ShortUrlResolutionStatus.Resolved => Redirect(result.OriginalUrl!),

            // AF-06: defined not-found response. fn-fetch.md's Q10 branded-HTML-page
            // behavior is a UI-content concern out of scope for this API-only MVP; a
            // ProblemDetails 404 (produced automatically by [ApiController] + AddProblemDetails)
            // is used here instead.
            _ => NotFound(),
        };
    }
}
