# Consideration 04 — Elasticsearch vs. MongoDB for Analytics Events

**Version:** v2 (scalability exploration)
**Status:** Draft
**Scope:** Storage/serving engine for **click/analytics event data only** (`ShortUrlAccessEvent`). The core `ShortUrl` mapping table is explicitly out of scope for this document — per the companion v2 documents it stays relational (SQL Server), and is compared separately in `03-elasticsearch-vs-sql-server.md`.
**Companion docs:** `fn-analytics.md` (v1 analytics design — what is captured, why event recording is fire-and-forget, why retention (Q27) was left open), `03-elasticsearch-vs-sql-server.md` (ES vs. the primary relational store), `05-kafka-comparison.md` (how events get from the redirect path to the store)

---

## 1. Workload Shape (Recap)

This document is about one specific data store decision, so the workload it has to fit is worth restating narrowly:

- **Append-only writes.** Every successful redirect produces exactly one immutable `ShortUrlAccessEvent` row (`ShortUrlId`, `AccessedAtUtc`, `Referrer`, `DeviceType`). Rows are never updated in place; per `fn-analytics.md` Section 3.1, even the soft-delete/audit columns are vestigial here, not functionally used.
- **Volume.** 10M fetches/day today, up to 100M/day at the 5-year horizon (AF-08). At peak that's roughly 1,150 writes/sec sustained, with multiples of that at traffic peaks — a write-heavy, not read-heavy-per-row, ingestion pattern.
- **Reads are aggregations, not point lookups.** `fn-analytics.md` Section 5.3 defines the v1 query shape as `COUNT(*) WHERE ShortUrlId = @id` and `MAX(AccessedAtUtc) WHERE ShortUrlId = @id` — i.e., "roll up N events for one link into a summary," not "fetch event #12345." A v2 richer analytics surface (Section 5 below) would extend this to counts/trends bucketed over time (daily/hourly click curves), which is aggregation over a range, not retrieval of individual event documents.
- **No full-text search requirement.** Referrer is a stored string, not something users query with relevance ranking. This matters for the comparison below — it's the one dimension where Elasticsearch's headline strength (full-text search) is *not* actually being exercised by this workload; what's being exercised is its aggregation engine specifically.

Given this shape, the real question is not "which database is more featureful" but "which one aggregates hundreds of millions of append-only rows into counts/trends fastest, and manages the lifecycle of that ever-growing dataset with the least operational effort."

---

## 2. MongoDB — Strengths for This Workload

- **Flexible document schema.** `ShortUrlAccessEvent` already has a fixed, small shape today, but if the analytics surface grows (device breakdown, campaign tags, custom event metadata per link), MongoDB's schema-less documents absorb new fields without a migration. This is a genuine advantage over the relational store it's being compared against for other reasons, and it's shared with Elasticsearch (also schema-flexible at the document level) — so it's not a differentiator between ES and Mongo specifically, but worth naming as a reason Mongo is a legitimate candidate at all.
- **Decent write throughput.** MongoDB handles high-volume append-only inserts well, particularly with unordered bulk writes and, since MongoDB 5.0, time-series collections purpose-built for exactly this "many small immutable timestamped documents" pattern. At 1,150 writes/sec average this is comfortably within a well-sharded MongoDB cluster's capability.
- **Simpler operational model — if the team already runs MongoDB elsewhere.** This is the strongest practical argument for MongoDB in this comparison, not a technical one: a single document-database technology to operate, back up, monitor, and staff for, instead of adding a second, structurally different cluster (Elasticsearch) purely for analytics. Elasticsearch clusters have real operational surface area — shard/replica sizing, JVM heap tuning, split-brain avoidance, index lifecycle management — that a team without existing ES experience takes on cold.
- **Native TTL indexes for retention.** `fn-analytics.md` Section 6 flags retention (Q27) as an explicitly open item in v1, deferred rather than decided. MongoDB's TTL index (`expireAfterSeconds` on `AccessedAtUtc`) is a one-line, built-in, per-document expiry mechanism — exactly the kind of "when Q27 is confirmed, drop rows older than window X" behavior the v1 doc anticipates, with essentially zero implementation cost. This is a real strength worth taking seriously.

## 3. MongoDB — Weaknesses for This Specific Workload

- **The aggregation pipeline is general-purpose, not purpose-built for this.** MongoDB's aggregation framework (`$match` / `$group` / `$bucket`) can compute the same counts and time-bucketed trends Elasticsearch can, but it is a general document-query engine doing aggregation as one of many jobs, not a system built around aggregation as the primary access pattern. At low-hundreds-of-millions of documents, `$group` stages over large collections cost materially more in both latency and cluster CPU than the same rollup expressed as an Elasticsearch `date_histogram` or `terms` aggregation running over a purpose-built inverted index and columnar `doc_values` store. The gap widens specifically as cardinality grows — e.g., "trend broken down by `ShortUrlId` × day × device type" across millions of distinct short URLs is the case Elasticsearch's aggregation engine was designed for and MongoDB's was not.
- **Indexing model is row/document-oriented, not analytics-columnar.** MongoDB indexes support the equality/range lookups this workload also needs (`ShortUrlId`, `AccessedAtUtc`), but multi-dimensional aggregate queries (count by link, bucketed by day, filtered by device) don't get the same benefit from a B-tree index that they get from Elasticsearch's `doc_values` — a column-oriented structure built specifically for fast aggregation and sorting, independent of the inverted index used for search.
- **Less mature ecosystem for time-series dashboards.** If AF-10 grows into a trends/visualization surface (Section 5), Elasticsearch pairs natively with Kibana — index patterns, date histograms, and dashboard panels are first-class, zero-additional-integration-code features. MongoDB's equivalents (Atlas Charts, or a third-party BI tool over the aggregation pipeline) are less mature, more often bolted on, and generally assume Atlas (MongoDB's own cloud) for the smoothest experience rather than being an infrastructure-agnostic built-in.
- **Sharding for write scale is an added operational decision, not automatic.** To sustain 100M writes/day with room to grow, MongoDB requires a shard key chosen up front (e.g., hashed `ShortUrlId` or `AccessedAtUtc`) and an actively managed sharded cluster. This is a solvable, well-documented problem, but it is additional design and operational work layered on top of the general-purpose weaknesses above — it doesn't buy back the aggregation-performance gap, it only addresses write throughput.

## 4. Elasticsearch — Strengths for This Workload (Restated Briefly)

Full detail lives in `03-elasticsearch-vs-sql-server.md`; restated here only as the other half of this specific comparison:

- **Aggregation performance is the core strength, and it matches this workload's dominant access pattern exactly.** `date_histogram`, `terms`, and `cardinality` aggregations over `doc_values` are what Elasticsearch is built for — computing "clicks per day per link" over hundreds of millions of documents is the textbook use case, not a workaround.
- **Index Lifecycle Management (ILM)** automates exactly the retention behavior `fn-analytics.md` Section 6 leaves open: roll over to a new index daily/weekly, move aging indices to cheaper storage tiers, and delete indices past a configured age — declaratively, without a custom purge job. This is a closer functional match to "we don't yet know the retention window, but when we do, apply it uniformly and automatically" than MongoDB's per-document TTL index, because it operates at the index level (cheap to drop an entire time-bounded index) rather than the document level (which still has to scan/delete matching documents as they expire).
- **Kibana is directly relevant to AF-10.** The v1 analytics API is deliberately minimal (`TotalClickCount`, `LastAccessedAtUtc` — `fn-analytics.md` Section 5.2), but AF-10's underlying intent is "expose click counts/trends to the creator." If that intent grows past the v1 API into an actual visualization surface, Kibana is a built-in, no-additional-integration-code way to deliver it directly against the same store already used for aggregation, rather than building a separate charting layer against a general-purpose database.

## 5. Comparison Table

| Dimension | MongoDB | Elasticsearch |
|---|---|---|
| Write throughput at 100M events/day | Good, with sharding designed up front | Good, with shard/index design (typically time-based indices) |
| Aggregation performance (counts, trends, high-cardinality group-by) | Adequate at moderate scale; degrades as cardinality/volume grow — general-purpose pipeline | Purpose-built; `doc_values` + aggregation engine designed for exactly this access pattern |
| Retention / data lifecycle | TTL index — simple, per-document expiry | ILM — index-level rollover/delete, cheaper at scale, more automation |
| Built-in visualization (relevant to AF-10) | Weak (Atlas Charts, mostly Atlas-cloud-oriented) | Strong (Kibana, first-class, infra-agnostic) |
| Schema flexibility | High (schema-less documents) | High (schema-flexible documents, with more structure via mappings) |
| Full-text search relevance | Not this workload's need | Not this workload's need either (headline ES strength, not actually exercised here) |
| Operational complexity, greenfield | Lower if the org has no document-DB experience at all; still nontrivial to shard for write scale | Higher — cluster sizing, shard/replica tuning, JVM heap, ILM policy authoring |
| Operational complexity, if team already runs one of these elsewhere | Effectively zero added surface area if MongoDB is already in the stack | Effectively zero added surface area if Elasticsearch is already in the stack |
| Best fit when... | Team is MongoDB-centric already, or analytics stays simple (counts only, low cardinality) | Analytics workload is aggregation-heavy and/or expected to grow into trends/dashboards |

---

## 6. Recommendation

**Elasticsearch is the recommended store for `ShortUrlAccessEvent` at this project's target scale.**

Justification, tied specifically to what this project's analytics requirement is and where it's headed:

- **v1 scope alone (click count + last-accessed) does not, by itself, demand Elasticsearch.** `fn-analytics.md` is explicit that AF-09/AF-10 are deliberately minimal — a single `COUNT(*)` and `MAX(AccessedAtUtc)` per link. Either store, or even the existing relational table, can serve that query cheaply at today's volume. If v1's scope were frozen forever, this decision would be closer, and the "simpler ops if already MongoDB-centric" argument in Section 6 would carry real weight.
- **But the reason this document exists at all is the 5-year volume curve (AF-08): up to 100M events/day**, and the v1 document itself flags in Section 6 that the read-time `COUNT`/`MAX` query "affects... as event volume grows" and calls the ever-growing table "a risk being flagged, not solved" — i.e., v1 already anticipates that this specific query pattern needs a scale answer, just defers it. That is exactly the gap this v2 document is closing.
- **The realistic trajectory of AF-10 is toward richer analytics, not away from it.** Section 1.1 of `fn-analytics.md` excludes trends/device/geo breakdowns from v1 scope, but notes `Referrer` and `DeviceType` are captured now specifically "to avoid a future schema migration if referrer breakdown is ever added." That's a strong signal the requirement is expected to grow into exactly the aggregation-heavy, time-bucketed, dashboard-shaped feature (click trends over time, device/referrer breakdown) that is Elasticsearch's core strength and MongoDB's comparative weakness (Section 3). Choosing the store now that fits where AF-10 is plausibly going avoids a second migration later, the same reasoning `fn-analytics.md` itself already applied when it decided to over-capture two unused columns.
- **ILM directly resolves the one open item v1 left unresolved (Q27 retention).** Whatever retention window gets confirmed, Elasticsearch applies it as index-level rollover/deletion — cheaper and more automatic at 100M events/day than any per-document expiry mechanism, MongoDB's TTL index included.
- **Kibana removes future integration work for AF-10's underlying intent** ("expose click counts/trends") if the API ever needs to become a real chart, not just three numbers in a DTO.

Net: for a workload that is fundamentally "ingest a firehose of immutable events, then aggregate them into counts and trends," Elasticsearch's aggregation engine, ILM, and Kibana integration line up with both the current requirement and its most likely growth path better than MongoDB's more general-purpose document model does.

## 7. When This Recommendation Flips

This is a genuine caveat, not a hedge — there are real conditions under which MongoDB is the better choice for this exact workload:

- **If the team's primary operational skill and existing infrastructure is already MongoDB-centric** (e.g., the rest of the platform already runs on MongoDB/Atlas, the team has MongoDB DBAs/SREs but no Elasticsearch experience), the operational-simplicity argument in Section 2 dominates. Standing up and running a *second*, structurally different database technology (Elasticsearch) purely for analytics — with its own cluster topology, JVM tuning, and ILM policy authoring to learn from scratch — can easily cost more in practice than the aggregation-performance gap costs in query latency, especially if analytics queries stay at "count and last-accessed per link" rather than growing into dashboards. In that scenario, take the write-throughput and TTL-index wins MongoDB already offers and accept the aggregation ceiling until it's actually reached.
- **If AF-10 is confirmed to stay frozen at v1 scope** (product explicitly decides no trends/breakdowns, ever — not just "not yet" per Q24), a large part of Elasticsearch's advantage (aggregation depth, Kibana) goes unused, and the simplest possible store — arguably even the existing relational table with a maintained counter column, as `fn-analytics.md` Section 3.1 already floats as a fallback — may beat both ES and MongoDB on total cost of ownership. That scenario is out of scope for this document (which assumes AF-08's 100M/day growth is real) but is worth naming so the recommendation above isn't read as unconditional.
