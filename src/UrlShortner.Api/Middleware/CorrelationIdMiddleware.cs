using Serilog.Context;

namespace UrlShortner.Api.Middleware;

/// <summary>
/// Item 6: fills the "correlation-ID / request logging middleware" pipeline slot
/// design-guidelines.md §4 reserves. Accepts an inbound <c>X-Correlation-Id</c> header or
/// generates a new one, pushes it into the Serilog <see cref="LogContext"/> for the
/// lifetime of the request (every log line for this request now carries
/// <c>{CorrelationId}</c>, including <c>UseSerilogRequestLogging()</c>'s own summary
/// line), and echoes it back as a response header so a caller/ops engineer can correlate
/// a client-side error report with server-side log lines. Also stashed on
/// <see cref="HttpContext.Items"/> so <see cref="GlobalExceptionHandler"/> can surface it
/// as a <c>ProblemDetails.traceId</c> extension field.
/// </summary>
internal sealed class CorrelationIdMiddleware
{
    /// <summary>The inbound/outbound header name carrying the correlation ID.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary><see cref="HttpContext.Items"/> key other components (e.g. <see cref="GlobalExceptionHandler"/>) read the current request's correlation ID from.</summary>
    public const string HttpContextItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString();

        context.Items[HttpContextItemKey] = correlationId;

        // Headers must be set before the response starts writing -- OnStarting is the
        // standard ASP.NET Core hook for "set this header no matter which code path in
        // the pipeline produces the eventual response" (success, 4xx, or 5xx alike).
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
