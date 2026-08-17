using UrlShortener.Domain.Entities;
using UrlShortener.Domain.ShortUrls;

namespace UrlShortener.Infrastructure.Messaging;

/// <summary>
/// The literal "NULL placeholder for Kafka" this MVP was asked for: a no-op
/// <see cref="IShortUrlEventPublisher"/> that does nothing on every call. Mirrors
/// <c>Caching.NullShortUrlCache</c>'s pattern exactly.
///
/// This is the one line that needs to change to introduce real event publishing later:
/// swap this registration in Infrastructure/InfrastructureServiceCollectionExtensions.cs
/// for a Kafka-backed implementation of <see cref="IShortUrlEventPublisher"/>. No caller
/// (<c>ShortUrlService</c>) needs to change, by construction (Dependency Inversion).
/// Full design: documentation/02-design/v2/design/considerations/05-kafka-comaporison.md
/// </summary>
public sealed class NullShortUrlEventPublisher : IShortUrlEventPublisher
{
    public Task PublishUrlCreatedAsync(ShortUrl entity, CancellationToken cancellationToken = default)
    {
        // --- What a real Confluent.Kafka producer call would look like here -----------
        // (commented out -- Confluent.Kafka is not referenced by this MVP; the full
        // at-least-once-delivery design wraps this in the outbox pattern rather than
        // calling ProduceAsync synchronously on the create request path -- see
        // documentation/02-design/v2/design/considerations/05-kafka-comaporison.md and
        // .../20-outbox-pattern.md):
        //
        // var message = new Message<string, string>
        // {
        //     Key = entity.Code,
        //     Value = JsonSerializer.Serialize(new
        //     {
        //         EventType = "UrlCreated",
        //         entity.Code,
        //         entity.CreatedAtUtc,
        //         // Deliberately NOT the OriginalUrl -- keep the same PII/data-minimization
        //         // posture as the rest of this MVP's logging (nfr-security.md §6).
        //     }),
        // };
        // await _producer.ProduceAsync("url-created", message, cancellationToken);
        // --------------------------------------------------------------------------------

        return Task.CompletedTask;
    }
}
