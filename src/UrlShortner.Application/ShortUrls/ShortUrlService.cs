using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortner.Application.Common;
using UrlShortner.Domain.Entities;
using UrlShortner.Domain.Exceptions;
using UrlShortner.Domain.Repositories;
using UrlShortner.Domain.ShortUrls;

namespace UrlShortner.Application.ShortUrls;

/// <summary>
/// Create-short-URL use case (AF-01, AF-03, AF-04). Thin orchestration only -- validation
/// rules and the collision-retry loop are the only business logic that lives here; actual
/// generation and persistence are delegated to injected abstractions (Dependency Inversion,
/// global/guidelines/design-guidelines.md §7).
/// See: documentation/02-design/v1/design/fn-create.md for the full (pre-MVP-trim) design
/// this is scoped down from.
/// </summary>
public sealed class ShortUrlService : IShortUrlService
{
    // AF-04, ANFR-08 (documentation/02-design/v1/design/fn-create.md §6): random-generation
    // collisions are expected, not exceptional -- retried up to this budget before giving up.
    private const int MaxGenerationAttempts = 5;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IShortUrlRepository _shortUrlRepository;
    private readonly IShortCodeGenerator _shortCodeGenerator;
    private readonly IShortUrlEventPublisher _eventPublisher;
    private readonly ShortUrlOptions _options;
    private readonly ILogger<ShortUrlService> _logger;

    public ShortUrlService(
        IUnitOfWork unitOfWork,
        IShortUrlRepository shortUrlRepository,
        IShortCodeGenerator shortCodeGenerator,
        IShortUrlEventPublisher eventPublisher,
        IOptions<ShortUrlOptions> options,
        ILogger<ShortUrlService> logger)
    {
        _unitOfWork = unitOfWork;
        _shortUrlRepository = shortUrlRepository;
        _shortCodeGenerator = shortCodeGenerator;
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ShortUrlResponse> CreateAsync(CreateShortUrlRequest request, CancellationToken cancellationToken = default)
    {
        // --- Deliberately out-of-scope guard clauses for this MVP, left as comments at
        // --- the exact point they would run, per the project's "show excellence, defer
        // --- with a pointer" convention:
        //
        // 1. Authenticated user context (Q1/Q2) -- would be the first guard clause here.
        //    Full design: documentation/02-design/v1/design/fn-create.md §4.
        //    if (!_currentUser.IsAuthenticated) throw new UnauthorizedAppException();
        //
        // 2. Per-caller usage ceiling / quota (Q13/Q16, ANFR-09) -- would run next.
        //    Full design: documentation/02-design/v1/design/fn-create.md §10,
        //    documentation/02-design/v1/design/nfr-security.md.
        //    await _usageQuotaPolicy.EnsureWithinLimitAsync(_currentUser, cancellationToken);

        // AF-03, Q14: well-formed absolute URI, http/https only, max length.
        var originalUrl = ValidateAndNormalizeUrl(request.OriginalUrl);

        // 3. Malicious/phishing domain check (Q17/Q18, ANFR-07) -- would run here, after
        //    syntactic validation and before code generation/persistence.
        //    Full design: documentation/02-design/v1/design/fn-create.md §9,
        //    documentation/02-design/v1/design/nfr-security.md.
        //    await _linkSafetyChecker.EnsureNotMaliciousAsync(originalUrl, cancellationToken);

        // 4. Custom/vanity alias (AF-01, Q20/Q21) -- if this MVP accepted CustomAlias on
        //    the request, it would branch here instead of always system-generating.
        //    Full design: documentation/02-design/v1/design/fn-create.md §7.
        var code = await ResolveSystemGeneratedCodeAsync(cancellationToken);

        // 5. Optional expiration (Q8/Q9) -- would validate request.ExpiresAtUtc here and
        //    set it on the entity below. Full design: fn-create.md §8.

        var shortUrl = new ShortUrl
        {
            Code = code,
            OriginalUrl = originalUrl,
            // CreatedBy would be set from ICurrentUserContext.UserId once auth exists
            // (see guard clause 1 above); "system" is a placeholder identity for the MVP.
            CreatedBy = "system",
        };

        await _unitOfWork.Repository<ShortUrl>().AddAsync(shortUrl, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken); // stamps CreatedAtUtc, RowVersion=1

        // PII-safe (nfr-security.md §6): logs the generated short code only, never the
        // caller-supplied original URL/any caller-identifying data (IP, etc.).
        _logger.LogInformation("Short URL created: {ShortCode}", shortUrl.Code);

        // Item 4: publish a UrlCreated domain event, fire-and-forget -- deliberately NOT
        // awaited inline so a slow/failing publisher (irrelevant today since
        // NullShortUrlEventPublisher always no-ops instantly, but relevant the moment a
        // real Kafka-backed publisher is swapped in) can never block or fail this
        // response. Any exception is caught and logged inside the helper below, never
        // rethrown. See IShortUrlEventPublisher's contract and
        // documentation/02-design/v2/design/considerations/05-kafka-comaporison.md.
        _ = PublishUrlCreatedEventSafelyAsync(shortUrl);

        return new ShortUrlResponse
        {
            Code = shortUrl.Code,
            OriginalUrl = shortUrl.OriginalUrl,
            CreatedAtUtc = shortUrl.CreatedAtUtc,
            ShortUrl = BuildShortUrl(shortUrl.Code),
        };
    }

    private string BuildShortUrl(string code) => $"{_options.BaseUrl.TrimEnd('/')}/{code}";

    // Item 4: intentionally uses CancellationToken.None -- this is a fire-and-forget side
    // effect of a request that has already returned its response by the time a real
    // (non-null) publisher's I/O would complete, so it must not be tied to the inbound
    // HTTP request's (already-cancelled-by-then) token.
    private async Task PublishUrlCreatedEventSafelyAsync(ShortUrl shortUrl)
    {
        try
        {
            await _eventPublisher.PublishUrlCreatedAsync(shortUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Fire-and-forget contract: a publish failure must never surface to the
            // caller of CreateAsync -- the short URL is already durably committed.
            _logger.LogWarning(ex, "Failed to publish UrlCreated event for {ShortCode}", shortUrl.Code);
        }
    }

    // AF-03, Q14 (fn-create.md §5, steps 3-5). Each failure reason gets its own distinct
    // exception type (see UrlShortner.Domain.Exceptions) instead of one generic exception
    // doing double duty, so the reason is unambiguous both to callers and in logs.
    private string ValidateAndNormalizeUrl(string? originalUrl)
    {
        const string fieldName = nameof(CreateShortUrlRequest.OriginalUrl);

        if (string.IsNullOrWhiteSpace(originalUrl))
        {
            _logger.LogWarning("Short URL creation failed validation: {Reason}", "Original URL is required.");
            throw new MissingUrlException(fieldName);
        }

        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("Short URL creation failed validation: {Reason}", "Original URL is not a well-formed absolute URI.");
            throw new MalformedUrlException(fieldName);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning(
                "Short URL creation failed validation: disallowed scheme {Scheme}",
                uri.Scheme);
            throw new UnsupportedUrlSchemeException(uri.Scheme, fieldName);
        }

        if (originalUrl.Length > UrlValidationConstants.MaxOriginalUrlLength)
        {
            _logger.LogWarning(
                "Short URL creation failed validation: URL length {Length} exceeds max {MaxLength}",
                originalUrl.Length,
                UrlValidationConstants.MaxOriginalUrlLength);
            throw new UrlTooLongException(UrlValidationConstants.MaxOriginalUrlLength, fieldName);
        }

        return uri.ToString();
    }

    // AF-04, ANFR-08 (fn-create.md §6): random candidate + collision retry against the DB.
    private async Task<string> ResolveSystemGeneratedCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxGenerationAttempts; attempt++)
        {
            var candidate = _shortCodeGenerator.GenerateCandidate();
            if (!await _shortUrlRepository.ExistsByCodeAsync(candidate, cancellationToken))
            {
                return candidate;
            }

            // AF-04: collisions are expected, not exceptional -- logged as a warning so
            // an abnormally high collision rate (e.g. a generator bug) is observable
            // without treating any single collision as an error.
            _logger.LogWarning(
                "Short code collision on attempt {Attempt} of {MaxAttempts}; retrying with a new candidate.",
                attempt,
                MaxGenerationAttempts);
        }

        throw new ShortCodeGenerationException($"Exhausted {MaxGenerationAttempts} short-code generation attempts.");
    }
}
