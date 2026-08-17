using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Domain.Exceptions;

namespace UrlShortener.Api.Middleware;

/// <summary>
/// Converts unhandled exceptions into a ProblemDetails response -- the standard error
/// shape for this API (engineering-standards/guidelines/design-guidelines.md §3-4). Implemented as an
/// <see cref="IExceptionHandler"/> (the current idiomatic ASP.NET Core mechanism, wired
/// via app.UseExceptionHandler() in Program.cs), which fills the same "global
/// exception-handling middleware" role the design guidelines placeholder describes.
/// </summary>
internal sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Exception-type -> HTTP status/title/detail mapping. Each concrete validation
        // failure (MissingUrlException, MalformedUrlException, UnsupportedUrlSchemeException,
        // UrlTooLongException) is a distinct type but all derive from ValidationAppException,
        // so the switch maps every one of them to 400 without needing a case per subtype
        // (Open/Closed). "detail" is deliberately NOT exception.Message for the generic
        // fallback case: coding-guidelines.md §6 / nfr-security.md §9 require that
        // internal exception messages/stack traces never leak to the client on a true 500
        // -- only the two known, intentionally-client-safe exception types below (whose
        // messages never contain internal state) get their own Message surfaced.
        var (statusCode, title, detail) = exception switch
        {
            ValidationAppException validationEx =>
                (StatusCodes.Status400BadRequest, "One or more validation errors occurred.", validationEx.Message),
            ShortCodeGenerationException =>
                (StatusCodes.Status500InternalServerError, "Could not generate a unique short code.", "The server could not generate a unique short code at this time. Please try again."),
            _ =>
                (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "An unexpected error occurred while processing the request."),
        };

        // ANFR-10: errors on core operations must be observable. Full request/response
        // logging middleware + correlation IDs are a design-guidelines.md §4 placeholder
        // not implemented in this MVP; this is the minimal logging needed to satisfy ANFR-10
        // for the two in-scope endpoints. Never logs the raw exception.Message back to the
        // client response body beyond what the mapped ProblemDetails.Detail already
        // exposes (validation messages only, never internal/500-class details) --
        // stack traces and internal exception details stay server-side, in this log.
        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Unhandled exception ({ExceptionType}) processing {Path}",
                exception.GetType().Name,
                httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Request validation failed ({ExceptionType}) for {Path}",
                exception.GetType().Name,
                httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;

        // Item 6: surface the same correlation ID CorrelationIdMiddleware already pushed
        // into the Serilog LogContext and echoed as a response header, as a "traceId"
        // ProblemDetails extension -- so a caller reporting an error and an operator
        // grepping server-side logs are looking at the same identifier.
        var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.HttpContextItemKey, out var correlationIdValue)
            ? correlationIdValue as string
            : null;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
        };

        if (exception is ValidationAppException { FieldName: { } fieldName })
        {
            var validationProblem = new ValidationProblemDetails(
                new Dictionary<string, string[]> { [fieldName] = new[] { exception.Message } })
            {
                Status = statusCode,
                Title = title,
                Instance = httpContext.Request.Path,
            };

            if (correlationId is not null)
            {
                validationProblem.Extensions["traceId"] = correlationId;
            }

            await httpContext.Response.WriteAsJsonAsync(
                validationProblem,
                options: null,
                contentType: "application/problem+json",
                cancellationToken: cancellationToken);
            return true;
        }

        if (correlationId is not null)
        {
            problemDetails.Extensions["traceId"] = correlationId;
        }

        await httpContext.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);
        return true;
    }
}
