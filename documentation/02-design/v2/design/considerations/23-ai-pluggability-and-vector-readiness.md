# Consideration 23 — AI Pluggability and Vector Readiness

**Version:** v2 (scalability exploration)
**Status:** Draft — architectural readiness consideration, **not** a feature commitment
**Scope:** This document answers one question — *does anything in this architecture block bolting on AI-driven capabilities later, and if not, where would they attach?* It is explicitly **not** a design for any specific AI feature, does not select an embedding model or AI vendor, and does not propose building anything now. See Section 4 for what is deliberately out of scope.
**Traceability:** `prompt@review-desig.md` ("create a document on how AI can be plugged into the app — making the design flexible for AI to operate. Metadata and URLs can be optionally represented as vectors at a later point in time").
**Companion docs (not duplicated here, cross-referenced by filename):**
- `03-elasticsearch-vs-sql-server.md` — establishes Elasticsearch as the v2 analytics/event store; this document leans on that decision rather than re-litigating it.
- `05-kafka-comaporison.md` — establishes the event-driven broker pipeline (`UrlCreated`/`UrlClicked` events, independent consumers) that this document treats as the natural AI attachment point.
- `08-metadata-management.md` — covers how link/file metadata is modeled and stored today; this document only adds an optional field to that shape, it does not redesign it.
- `nfr-security.md` / a future `22-security-reputation-hacking-authorization.md` — own the actual threat model and the existing `IMaliciousUrlChecker` design (Strategy/Adapter, per `design-guidelines.md` §8); this document only observes that an AI-based classifier is a drop-in alternative implementation of that same interface, not a replacement design.
- `UrlShortner/global/guidelines/design-guidelines.md` — the layered architecture and Design Pattern Catalog (§8) this document maps every extension point onto. No new architectural primitive is introduced.
- `fn-create.md`, `fn-analytics.md` (v1) — the create and analytics flows this document is careful *not* to modify.

---

## 1. Why Extensibility Matters Here, Concretely

A URL shortener's data — links, their metadata, and click behavior — is exactly the kind of data that AI/ML techniques are commonly applied to elsewhere. None of the following are being proposed for implementation now; they exist here purely to justify *why* it's worth spending design effort on readiness rather than doing nothing until asked:

- **Semantic/similarity search over previously-shortened links.** "Find links similar to this one" — a user pastes a URL or a title and the system surfaces other short links pointing at conceptually similar content, using a vector-similarity comparison over embedded URL/metadata text rather than exact keyword match. This is the requester's specific anchor use case and is addressed in depth in Section 2.
- **AI-assisted content moderation, as a smarter successor to v1's basic domain check.** v1 and the early v2 threat model rely on `IMaliciousUrlChecker` — a denylist or reputation-API lookup against the target *domain* (`nfr-security.md` §4, cross-referenced not restated). An AI classifier — one that reasons over page content, redirect chains, or visual similarity to known phishing kits, rather than just domain reputation — is a plausible future *upgrade* to that same check, not a different feature. See Section 3.2 for how the existing Strategy seam already accommodates this without a redesign.
- **AI-driven analytics summarization or anomaly detection over click data.** Once click events live in Elasticsearch at v2 scale (`03-elasticsearch-vs-sql-server.md`), the aggregation-heavy nature of that store is also exactly the shape ML anomaly-detection or summarization tooling wants to consume (e.g., "this link's click pattern looks like a bot swarm," or a natural-language summary of a trend chart) — that data is already accumulating for entirely non-AI reasons (AF-09/AF-10), so making it AI-consumable later is a matter of read access, not a new pipeline.

These three are **illustrative, not a roadmap**. The point of naming them is to give the extension points below a concrete reason to exist — not to schedule their delivery.

---

## 2. Vector Representation — The Requester's Concrete Ask

The specific requirement to design against: *URLs and their metadata should be optionally representable as vectors at a later point in time, without a redesign.* Three questions need answers: where is the vector computed, where does it live, and how does it stay strictly optional.

### 2.1 Where the embedding would be generated: off the hot path, via the existing event pipeline

The create (`fn-create.md`) and fetch (`fn-fetch.md`) flows are latency- and availability-critical (`nfr-performance.md`'s redirect budget; the entire premise of `03-elasticsearch-vs-sql-server.md` and `05-kafka-comaporison.md` is *don't make the hot path wait on anything that doesn't have to happen synchronously*). Embedding generation — a call to an embedding model, whatever form that eventually takes — is exactly the kind of variable-latency, non-critical-path work that must never sit inside `ShortUrlService.CreateAsync` or the redirect resolver.

This is a direct, no-new-machinery application of the event-driven pipeline already justified in `05-kafka-comaporison.md`:

- `fn-create.md`'s flow already ends by persisting a `ShortUrl` row; the v2 create path already publishes a `UrlCreated` event onto the broker (`05-kafka-comaporison.md` §1) for other independent consumers (safety re-check, cache warm).
- A new, independent **AI-enrichment consumer** subscribes to `UrlCreated` (and, if metadata changes are ever supported, an equivalent update event) the same way the analytics-indexing consumer subscribes to `UrlClicked`. It calls an embedding model, then writes the resulting vector back onto the corresponding Elasticsearch document.
- This consumer touches **nothing** in `Api`, `Application`, or the core `ShortUrl` write path. It is purely an additional subscriber on a stream that already exists for other reasons. If the embedding call is slow, times out, or the provider is down, nothing about create or fetch is affected — the same fire-and-forget, no-blocking-guarantee already established for click recording (`fn-analytics.md` §4) applies here by construction, not by new design work.

### 2.2 Where the vector would be stored: Elasticsearch `dense_vector`, not a separate vector database

Elasticsearch is already the chosen store for the analytics/event data at this scale (`03-elasticsearch-vs-sql-server.md`). Elasticsearch natively supports a `dense_vector` field type and approximate k-NN search (`_knn_search` / the `knn` query clause) as a first-class feature, not a bolt-on. Given that:

- **Recommendation: use Elasticsearch's native `dense_vector` + k-NN support. Do not introduce a dedicated vector database (e.g., Pinecone, Weaviate, Milvus) at this system's scale.**
- The justification for adding a *second* purpose-built store must clear the same bar `03-elasticsearch-vs-sql-server.md` §5 sets for adding Elasticsearch itself in the first place — a workload shape the existing store genuinely can't serve. A standalone vector database earns its keep at a scale/recall profile this system isn't near: billions of high-dimensional vectors, sub-10ms ANN query SLAs at very high QPS, or vector-specific tuning (product quantization, specialized index algorithms) beyond what Lucene's HNSW-based `dense_vector` implementation offers. Nothing in the use cases named in Section 1 — similarity search over a shortener's own link corpus, not a general-purpose embedding search product — approaches that.
- Running a second, different data platform purely for vectors reintroduces the exact operational cost `03-elasticsearch-vs-sql-server.md` §5 already had to justify paying once (cluster ops, a new system the team must learn) — paying it *twice*, for two stores holding data about the same entities, is a cost this document is not prepared to justify speculatively. If the scale or recall requirements ever genuinely exceed what Elasticsearch's vector support offers, that would be a reason to revisit — stated as an explicit, named upgrade path (the same posture `05-kafka-comaporison.md` §4 takes toward Kafka), not a default.

### 2.3 How this stays optional and additive

The vector field must be a **nullable, additive extension** to the existing document shape — never a required field, never something that changes the meaning of a document that lacks it, and never something existing queries need to account for.

```jsonc
// Elasticsearch mapping excerpt — the click/analytics-adjacent "link document"
// index (illustrative; field/index naming is not fixed by this document).
// Every field below the divider is new; everything above already exists
// for reasons unrelated to AI (03-elasticsearch-vs-sql-server.md).
{
  "mappings": {
    "properties": {
      "shortUrlId":     { "type": "keyword" },
      "code":           { "type": "keyword" },
      "originalUrl":    { "type": "text" },
      "createdAtUtc":   { "type": "date" },

      // ---- optional, additive, populated asynchronously (Section 2.1) ----
      "metadataEmbedding": {
        "type": "dense_vector",
        "dims": 768,                 // illustrative — depends on the (unselected) embedding model
        "index": true,
        "similarity": "cosine"
      },
      "embeddingGeneratedAtUtc": { "type": "date" },   // null until the enrichment consumer runs
      "embeddingModelVersion":   { "type": "keyword" } // null until populated; supports re-embedding on model upgrades
    }
  }
}
```

Consequences of this shape, stated explicitly:

- **A document with no vector is a completely normal, valid document.** Every existing query (click aggregation, lookup by `shortUrlId`) is written against fields that are unaffected by whether `metadataEmbedding` exists. Elasticsearch does not require every document in an index to populate every mapped field.
- **Lazily populated.** The enrichment consumer (Section 2.1) can run late, be re-run to backfill older links, or never run at all for a given link — none of that is a correctness problem for the rest of the system, because nothing else in the system reads this field today.
- **A k-NN similarity query is additive, not a replacement for existing lookups.** "Find links similar to this one" would be a new, separate query path (`knn` search clause) layered on top of the existing store — it does not change how a short code is resolved to a target URL (still the relational `ShortUrl` table, per `03-elasticsearch-vs-sql-server.md` §7's explicit split) or how click counts are aggregated.
- **Versioned, not fire-and-forget-forever.** `embeddingModelVersion` is included specifically so that a future change of embedding model doesn't require a schema migration — it requires the enrichment consumer to re-process and overwrite, using metadata the schema already carries.

---

## 3. Architectural Extension Points — Mapped to the Existing Pattern Catalog

The point of this section is that **the same event-driven design chosen in `05-kafka-comaporison.md` for scaling reasons is also what makes AI extensibility cheap** — this is not a coincidence that needs new machinery to exploit, and every attachment point below maps onto a pattern `design-guidelines.md` §8 already lists.

### 3.1 The broker pipeline as the primary attachment point (most important point in this document)

`05-kafka-comaporison.md` establishes that create/fetch publish `UrlCreated`/`UrlClicked` events and that adding a new independent consumer is exactly the kind of change the broker exists to make cheap ("producers... should never block on, or fail because of, a downstream consumer" — §1). An AI-enrichment consumer is not a new architectural concept; it is the **third named consumer type** in a pipeline that was already going to have several:

| Event | Existing/planned consumers | AI-enrichment consumer |
|---|---|---|
| `UrlCreated` | Async malicious-domain re-check, cache warm/seed (`05-kafka-comaporison.md` §1) | Generate and store `metadataEmbedding` (Section 2.1) |
| `UrlClicked` | Analytics-indexing into Elasticsearch, cache-invalidation (`05-kafka-comaporison.md` §1) | (Illustrative, not designed here) feed click-pattern data to an anomaly-detection consumer |

Because this consumer is *just another subscriber*, adopting it later requires zero changes to `ShortUrlService`, the `Api`/`Application` layers, or anything in `fn-create.md`/`fn-analytics.md`. This is the central claim of this document: **the scaling decision already made (decouple via broker) is the same decision that buys AI-readiness — there is no separate "make it AI-ready" architecture to design.**

### 3.2 Strategy pattern — swappable moderation/classification algorithm

`design-guidelines.md` §8 already lists Strategy as the pattern for "pluggable... algorithm behind a common interface, swappable via DI," and `fn-create.md` §9 / `nfr-security.md` §4 already apply it to `IMaliciousUrlChecker` / `IShortCodeGenerator`. A future AI-based classifier is simply a second implementation of the same seam, not a new one:

```csharp
namespace UrlShortner.Domain.ShortUrls;

/// <summary>
/// Strategy for deciding whether a submitted URL should be rejected on safety/policy
/// grounds. Already the seam v1 uses for a domain-denylist/reputation-API check
/// (nfr-security.md §4). A future AI-based classifier is a second implementation
/// of this same interface, selected via DI/configuration — ShortUrlService does
/// not change either way (Open/Closed, design-guidelines.md §7).
/// </summary>
public interface ILinkSafetyChecker
{
    Task<LinkSafetyResult> CheckAsync(string originalUrl, CancellationToken cancellationToken = default);
}

// Existing v1/v2 implementation: denylist or reputation-API lookup (nfr-security.md §4).
public sealed class DenylistLinkSafetyChecker : ILinkSafetyChecker { /* ... */ }

// Illustrative future implementation — NOT designed or committed to by this document.
// Shown only to demonstrate that no interface change, and no ShortUrlService change,
// would be required to introduce it.
public sealed class AiClassifierLinkSafetyChecker : ILinkSafetyChecker { /* ... */ }
```

Whether a future classifier runs synchronously (replacing the current check inline) or asynchronously (as a post-creation re-check publishing a "flagged" outcome, per `05-kafka-comaporison.md` §6's existing discussion of this exact idea) is a decision for whoever eventually builds it — both options fit the Strategy seam without touching the interface.

### 3.3 Decorator pattern — an enrichment wrapper around the repository

`design-guidelines.md` §8 lists Decorator as the pattern already earmarked for "optional caching layer over `IRepository<T>`/`IShortUrlRepository`... added without changing consumers." The same wrapping technique composes an AI-enrichment step the identical way a caching decorator would:

```csharp
/// <summary>
/// Illustrative only — demonstrates that an AI-enrichment step composes via the
/// same Decorator seam already earmarked for caching (design-guidelines.md §8),
/// not a new architectural concept. Not a committed implementation.
/// </summary>
public sealed class EnrichingShortUrlRepository : IShortUrlRepository
{
    private readonly IShortUrlRepository _inner;      // e.g., the EF Core repository, or a caching decorator around it
    private readonly IAiEnrichmentQueue _enrichment;   // publishes to the broker (Section 3.1); never awaited inline

    public EnrichingShortUrlRepository(IShortUrlRepository inner, IAiEnrichmentQueue enrichment)
    {
        _inner = inner;
        _enrichment = enrichment;
    }

    public async Task AddAsync(ShortUrl entity, CancellationToken cancellationToken = default)
    {
        await _inner.AddAsync(entity, cancellationToken);
        _enrichment.EnqueueFireAndForget(entity.Id); // same non-blocking contract as IAccessEventRecorder (fn-analytics.md §4)
    }

    // All other members delegate to _inner unchanged — Liskov substitution,
    // design-guidelines.md §7, exactly as the caching-decorator example already relies on.
}
```

This is offered as a **second, equally valid** attachment point to the broker-consumer approach in Section 3.1, not a competing design — in practice the broker-consumer model is the better fit here (it doesn't touch the write path even indirectly, and it survives multiple writers/instances), but the decorator seam existing at all is further evidence that nothing about the current layered architecture resists this kind of addition.

### 3.4 Adapter pattern — isolating the AI provider itself

Whatever embedding or classification provider is eventually chosen, `design-guidelines.md` §8's Adapter entry ("wraps external service integrations... behind a `Domain`/`Application`-defined interface, isolating third-party SDK shapes to `Infrastructure`") is the same pattern `ILinkSafetyChecker`'s reputation-API implementation and any future analytics/BFF integration already use. An `IEmbeddingGenerator` or `IContentClassifier` interface, implemented in `Infrastructure` against whichever vendor SDK is chosen later, keeps `Application` and `Domain` free of any AI-vendor-specific type — consistent with the dependency-direction rule in `design-guidelines.md` §1.

---

## 4. What This Document Is Not Doing

Stated explicitly, per this project's convention of naming trade-offs and scope boundaries rather than leaving them implicit:

- **This is not a design for any specific AI feature.** Similarity search, AI moderation, and analytics summarization (Section 1) are illustrative motivating examples, not specifications. None has request/response contracts, UI, or acceptance criteria defined here.
- **This does not select an embedding model.** Dimension counts (`768` in Section 2.3) are illustrative placeholders showing the mapping shape, not a chosen model's actual output size.
- **This does not commit to an AI vendor or API.** No OpenAI/Azure OpenAI/Anthropic/open-source-model decision is made or implied. Section 3.4's Adapter pattern exists precisely so that decision can be deferred without cost.
- **This does not change any v1 or already-decided v2 behavior.** `fn-create.md`, `fn-analytics.md`, `03-elasticsearch-vs-sql-server.md`, and `05-kafka-comaporison.md` are unmodified by this document; it only observes that their existing shape has room for the extensions described above.
- **This does not argue AI capability is needed.** No use case in Section 1 is asserted to be a real, scheduled requirement — the same "don't build against a speculative need" discipline `05-kafka-comaporison.md` §3 applies to Kafka's replay capability applies here to AI generally: readiness is being designed for; nothing is being built.

---

## 5. Closing Checklist — If an AI Feature Is Ever Built

| If you build... | Extension point to use | Why it doesn't touch core create/fetch/analytics |
|---|---|---|
| Similarity/semantic search over links | Elasticsearch `dense_vector` field + `knn` query (Section 2.2–2.3), populated by an AI-enrichment consumer (Section 2.1/3.1) | The vector field is additive/nullable on a store that already isn't the system of record for redirects (`03-elasticsearch-vs-sql-server.md` §7); a new k-NN query is a new read path, not a change to existing ones. |
| A smarter (AI-based) moderation check | New `ILinkSafetyChecker` implementation (Section 3.2) | Same interface, selected via DI/config; `ShortUrlService` is unchanged (Open/Closed). |
| Any enrichment triggered at creation or click time | New consumer on the existing `UrlCreated`/`UrlClicked` broker topics (Section 3.1) | Producers (create/fetch) already don't know or care who consumes their events (`05-kafka-comaporison.md` §1); adding a subscriber is a deploy of a new consumer process, not a change to the request path. |
| Analytics summarization/anomaly detection | Reads directly from the existing Elasticsearch click-event store (`03-elasticsearch-vs-sql-server.md`) | Read-only against data already being captured for AF-09/AF-10; no write-path change at all. |
| Any third-party AI provider integration | `Infrastructure`-layer Adapter behind a `Domain`/`Application`-defined interface (Section 3.4) | Keeps vendor SDK types out of `Application`/`Domain`, consistent with the existing dependency-direction rule (`design-guidelines.md` §1). |

The common thread across every row: the extension point already exists, for reasons unrelated to AI, because of decisions already made in `03-elasticsearch-vs-sql-server.md`, `05-kafka-comaporison.md`, and the pattern catalog in `design-guidelines.md` §8. Nothing in this document asks for a new architectural primitive.
