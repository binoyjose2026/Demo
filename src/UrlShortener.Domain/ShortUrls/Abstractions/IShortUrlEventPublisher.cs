using UrlShortener.Domain.Entities;

namespace UrlShortener.Domain.ShortUrls.Abstractions;

/// <summary>
/// Fire-and-forget domain-event publishing seam for the create flow -- mirrors
/// <see cref="IShortUrlCache"/>'s "NULL placeholder" pattern exactly (same MVP decision,
/// same swap-in shape).
///
/// MVP decision: a real message broker (Kafka) is explicitly OUT of scope for this MVP.
/// This interface exists purely as the seam the prompt asked for; the only registered
/// implementation today is <c>NullShortUrlEventPublisher</c> (Infrastructure), which
/// always no-ops. Swapping in a real Kafka-backed publisher later is a DI registration
/// change only -- no caller of this interface (<c>ShortUrlService</c>) needs to change.
/// Full design for the real implementation (UrlCreated/UrlClicked events, producer
/// config, consumers, at-least-once delivery): documentation/02-design/v2/design/considerations/05-kafka-comaporison.md,
/// documentation/02-design/v2/design/considerations/20-outbox-pattern.md.
/// </summary>
public interface IShortUrlEventPublisher
{
    /// <summary>
    /// Publishes a "short URL created" event for <paramref name="entity"/>. Called
    /// fire-and-forget from the create flow after the entity is durably committed --
    /// implementations (and callers) must never let a publish failure block or fail the
    /// create response. Always no-ops for the MVP null publisher.
    /// </summary>
    Task PublishUrlCreatedAsync(ShortUrl entity, CancellationToken cancellationToken = default);
}
