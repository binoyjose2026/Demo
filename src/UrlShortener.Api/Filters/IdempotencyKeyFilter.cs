using Microsoft.AspNetCore.Mvc.Filters;

namespace UrlShortener.Api.Filters;

/// <summary>
/// Item 5 / MVP placeholder seam: reads the <c>Idempotency-Key</c> header if the caller
/// sends one, but performs NO deduplication -- a retried create request carrying the same
/// key still creates a second row today. This filter exists purely to fix the contract
/// shape (the header name, the pipeline stage it is read at) so a real implementation is a
/// same-seam swap later, not a retrofit that changes the request contract callers rely on.
/// Full design (Redis-backed dedup store, cached-response replay on a duplicate key, TTL):
/// documentation/02-design/v2/design/considerations/11-idempotency-keys.md.
/// </summary>
internal sealed class IdempotencyKeyFilter : IAsyncActionFilter
{
    /// <summary>The header name a retrying client is expected to send.</summary>
    public const string HeaderName = "Idempotency-Key";

    private readonly ILogger<IdempotencyKeyFilter> _logger;

    public IdempotencyKeyFilter(ILogger<IdempotencyKeyFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var idempotencyKey) &&
            !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // MVP: observed and logged only -- no dedup-store lookup, no cached-response
            // replay. The key itself is an opaque client-generated token (typically a
            // GUID), not caller-identifying data, so logging it does not violate the
            // PII-safe logging rule (nfr-security.md §6).
            _logger.LogInformation(
                "Create request carried Idempotency-Key {IdempotencyKey} (not deduplicated -- MVP placeholder, see documentation/02-design/v2/design/considerations/11-idempotency-keys.md).",
                idempotencyKey.ToString());
        }

        await next();
    }
}
