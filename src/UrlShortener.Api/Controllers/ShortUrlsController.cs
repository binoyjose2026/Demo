using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.Api.Filters;
using UrlShortener.Application.ShortUrls;

namespace UrlShortener.Api.Controllers;

/// <summary>
/// AF-01: create a new short URL. Thin controller -- binds/validates the request, calls
/// one Application service method, maps the result to an HTTP response. No business logic.
/// See: engineering-standards/guidelines/design-guidelines.md §3, documentation/02-design/v1/design/fn-create.md.
/// </summary>
/// <remarks>
/// Item 11 (API versioning): this is this MVP's one real API contract surface, so it is
/// the endpoint versioned under <c>/api/v1/short-urls</c>. <c>GET /{code}</c>
/// (<see cref="RedirectController"/>) deliberately stays unversioned at the application
/// root -- it is a public short-link surface (the whole point is a short, stable path),
/// not a versioned API contract a client negotiates against.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/short-urls")]
public class ShortUrlsController : ControllerBase
{
    private readonly IShortUrlService _shortUrlService;

    /// <summary>Initializes a new instance of <see cref="ShortUrlsController"/>.</summary>
    /// <param name="shortUrlService">The create-short-URL use case (AF-01, AF-03, AF-04).</param>
    public ShortUrlsController(IShortUrlService shortUrlService)
    {
        _shortUrlService = shortUrlService;
    }

    /// <summary>
    /// Creates a short URL for the given original URL. Only <c>http</c>/<c>https</c>
    /// absolute URLs up to 2048 characters are accepted; the response includes the
    /// generated short code and the fully-qualified short URL a caller can share.
    /// AF-01, AF-03, AF-04.
    /// </summary>
    /// <param name="request">The original (long) URL to shorten.</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    /// <response code="201">The short URL was created.</response>
    /// <response code="400">The original URL was missing, malformed, not http/https, or exceeded the maximum length.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ShortUrlResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    // Item 3 / MVP placeholder (real ASP.NET Core rate-limiting primitive, wired to an
    // effectively-unlimited policy -- see "CreatePolicy" in Program.cs -- so the
    // attribute is exercised end-to-end without actually constraining this MVP's
    // traffic). Real policy (sliding-window, Redis-backed counters shared across
    // horizontally-scaled instances): documentation/02-design/v2/design/considerations/12-distributed-rate-limiting.md
    [EnableRateLimiting("CreatePolicy")]
    // Item 3 / MVP placeholder (real [Authorize] + policy, wired to a
    // RequireAssertion(_ => true) policy backed by a fixed-identity placeholder
    // authentication scheme -- see "Mvp-Bypass" and MvpPlaceholderAuthenticationHandler
    // in Program.cs, so the pipeline slot is real even though auth is mocked for v1).
    // Full scope boundary: documentation/02-design/v1/design/nfr-security.md §10's
    // "Assumed vs. built" table.
    [Authorize(Policy = "Mvp-Bypass")]
    // Item 5 / MVP placeholder (reads Idempotency-Key if present, does not deduplicate --
    // see IdempotencyKeyFilter's own doc comment for the full design pointer).
    [TypeFilter(typeof(IdempotencyKeyFilter))]
    public async Task<ActionResult<ShortUrlResponse>> CreateAsync(
        CreateShortUrlRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _shortUrlService.CreateAsync(request, cancellationToken);

        // No metadata-retrieval endpoint exists in this MVP (AF-05 is out of scope --
        // documentation/02-design/v1/design/fn-fetch.md §9), so there is no CreatedAtAction
        // target to point the Location header at yet; it points at the redirect endpoint
        // itself, which is the one route that does resolve this code today.
        return Created($"/{result.Code}", result);
    }
}
