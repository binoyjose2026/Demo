# Consideration 03 — Elasticsearch vs. SQL Server for the Click/Analytics Event Store

**Version:** v2 (scalability exploration)
**Status:** Draft — architectural consideration, not yet a committed decision
**Scope:** This document covers **only** the high-volume click/analytics event store (the v2 successor to `ShortUrlAccessEvent`, see `fn-analytics.md`). It does **not** cover the core `ShortUrl` mapping table (code → target URL), which remains a relational, ACID system of record per `data-design-guidelines.md` regardless of the outcome of this comparison. See Section 6 for the explicit split decision.
**Traceability:** `agent-prompt.md` (review scope: "Explain why Elastic Search is a better solution for this due to extreme size"); `fn-analytics.md` (v1 analytics scope — click count only, no PII, `ShortUrlAccessEvent` design, Section 6 open retention item); `data-design-guidelines.md` (relational conventions the core mapping table keeps).
**Companion docs:** `05-kafka-comparison.md` (how events get from the redirect path to the store — this document assumes events arrive and asks what stores them), an outbox-pattern consideration (if written), a Redis-caching consideration (if written).

---

## 1. Why This Question Needs Its Own Document

The v1 design (`fn-analytics.md`) stores one row per successful redirect in `ShortUrlAccessEvent`, in the same SQLite database as everything else, and derives `TotalClickCount`/`LastAccessedAtUtc` with `COUNT(*)`/`MAX(...)` at read time. That is the right call at v1 scale (Section 6 of `fn-analytics.md` even flags future volume as an open risk rather than a solved problem).

At v2 scale, the click-event workload stops looking like "a table in the app's database" and starts looking like a distinct system with its own throughput, growth, and query profile. That's worth isolating and reasoning about on its own terms, separately from the core `ShortUrl` mapping table, which has a completely different shape (low volume, needs strict consistency, is the thing the redirect path cannot function without).

---

## 2. The Workload Shape

This is the load-bearing section: the storage decision follows from the shape of the data, not from a general "Elasticsearch is for big data" assumption.

| Dimension | Characteristic | Why it matters |
|---|---|---|
| **Write volume** | Up to 100M events/day at 5-year scale (per AF-08, one event per successful fetch). Averaged over 86,400s that's ~1,160 writes/sec sustained; realistic diurnal/traffic-spike peaks push this to several thousand writes/sec for sustained windows. | This is the dominant cost driver. The store must be optimized for sustained high-throughput ingest, not for occasional writes. |
| **Update rate** | Effectively zero. Events are **append-only and immutable** — `fn-analytics.md` already models `ShortUrlAccessEvent` as "append-only from the application's perspective" (Section 3.1). No event is ever edited once written; the only lifecycle event is eventual deletion/rollover for retention. | Removes the need for row-level update locking, MVCC overhead for updates, or transactional multi-row consistency across events. This single fact is what makes non-relational options viable. |
| **Delete rate** | Bulk, time-boundary deletes only (retention/rollover — e.g., "drop everything older than 90 days"), never single-row deletes. | Favors a store that can drop whole time-partitions cheaply over one that has to delete rows individually. |
| **Read pattern** | Overwhelmingly **aggregation over time ranges** — click counts, trends, last-accessed, and (per AF-10's future direction implied by Section 1.1 of `fn-analytics.md`) potentially referrer/device breakdowns once "richer analytics" moves in scope. Point lookups of a single event by its own ID are essentially never a real use case; nobody queries "give me click-event row #48213902." | The store needs to be good at `GROUP BY` / `COUNT` / time-bucketed aggregation over millions-to-billions of rows, not at fast single-row retrieval by primary key. This is the opposite optimization target from an OLTP table. |
| **Consistency requirement** | Low. A click count that's a few seconds stale, or drops an occasional event under extreme load, is explicitly an acceptable trade-off per `fn-analytics.md` Section 4 ("Losing an occasional click event under extreme load is an acceptable trade-off; losing or delaying a redirect is not"). | Frees the store from needing strict read-after-write consistency or cross-row transactional guarantees — a hard requirement to relax for a relational OLTP engine, but a natural fit for many purpose-built analytics stores. |
| **Data volume at rest** | 100M events/day × a retention window (say 90–365 days) is 9B–36.5B rows, growing daily. Even a slim ~150–300 byte row is tens of terabytes at the upper end. | Storage/index growth and the cost of maintaining indexes on a table this size become first-order design concerns, not an afterthought. |

This combination — **extreme append-only write volume, near-zero update rate, aggregation-heavy reads over huge time-boxed datasets, relaxed consistency** — is close to the textbook profile that time-series/log/analytics stores (Elasticsearch, ClickHouse, time-series DBs) are built for, and is a poor match for what a relational OLTP engine is optimized for.

---

## 3. Scaling SQL Server to This Workload

SQL Server *can* be pushed to handle this, and it's worth being concrete about what that takes and where it starts to hurt, rather than dismissing it outright.

### 3.1 What scaling SQL Server would require

- **Table partitioning** — partition `ShortUrlAccessEvent` by date (e.g., daily or weekly range partitions on `AccessedAtUtc`), so retention becomes `ALTER TABLE ... SWITCH PARTITION` / `TRUNCATE PARTITION` instead of a row-by-row `DELETE`, and so queries scoped to a recent time range can benefit from partition elimination.
- **Careful indexing** — a composite index on `(ShortUrlId, AccessedAtUtc)` (already anticipated in `fn-analytics.md` Section 3.1) is necessary for per-link queries, but every additional index needed for aggregation queries (by referrer, by device, by day) is a second B-tree that must be maintained on **every one of up to ~1,160+ writes/sec**.
- **Read replicas** — offload aggregation/reporting queries (AF-10 and any future trend/dashboard queries) to one or more read replicas so they don't contend with the primary's write path, since large `GROUP BY` scans and high-frequency inserts compete for the same buffer pool and I/O.
- **Possibly columnstore indexes** — SQL Server's clustered columnstore index is the realistic path to making aggregation queries fast at this scale (it's SQL Server's own purpose-built answer to "analytics over huge append-heavy tables"), but it comes with its own tuning complexity (delta store management, tuple mover behavior under high insert rate) and is itself an admission that plain rowstore B-tree tables don't scale to this pattern.
- **Scaled-up compute/storage** — sustained multi-thousand-writes/sec ingest plus concurrent large aggregation scans needs a materially larger (and more expensive) SQL Server tier than the core mapping table ever will, sized for a workload that's structurally different from — and much larger than — the transactional workload SQL Server is used for elsewhere in this system.

### 3.2 Where it starts to strain

- **Aggregation query cost over huge tables.** Even with partitioning and indexing, a `COUNT(*) ... GROUP BY day, referrer` (or any ad hoc trend query) over billions of rows is fundamentally a large scan-and-aggregate operation on a row-oriented engine. Columnstore mitigates this but doesn't eliminate it, and every new aggregation dimension (device breakdown, geographic breakdown) is a query pattern that has to be explicitly indexed or accepted as slow.
- **Index maintenance overhead on high-volume inserts.** Every non-clustered B-tree index maintained on the table adds write amplification at insert time. At 1,000+ writes/sec, each additional index is a real, measurable tax on ingest throughput and lock contention — the opposite of what an append-only, ingest-dominated workload wants.
- **Storage and compute cost at this scale.** Tens of terabytes of row data plus index overhead, replicated for read scaling, sized for peak ingest — this is a large, expensive SQL Server footprint dedicated to what is, functionally, log data. It is very likely to cost more, for worse aggregation performance, than a store purpose-built for this shape.
- **Retention becomes an ongoing operational job either way.** Partition switching helps, but the fact remains: this table's natural lifecycle (append, aggregate, expire) is not what relational engines are optimized to make cheap and simple.

**Honest assessment:** SQL Server does not fall over at this volume — it is used at far larger scale than this in production elsewhere — but every mitigation above is effectively backing SQL Server into an architecture that mimics what log/analytics-oriented stores do natively (partition-based retention, columnar scan-friendly storage, replica-based read scaling), while still paying relational-engine overhead (transaction log, B-tree index maintenance, MVCC/locking machinery) for a workload that doesn't need most of those guarantees.

---

## 4. Why Elasticsearch Fits This Specific Workload

Elasticsearch's architecture lines up with the workload shape in Section 2 more directly than a relational engine's does:

- **Inverted index + purpose-built aggregation engine.** Elasticsearch's core data structure is designed for fast filtering and aggregation over large document sets (`terms`, `date_histogram`, `cardinality`, nested aggregations for "clicks per day per referrer," etc.) — this is precisely the AF-10-style query shape (counts and trends over time), and it's Elasticsearch's primary design target, not a secondary capability bolted onto a transactional engine.
- **Horizontal sharding.** An index is split across shards distributed over multiple nodes, so both ingest and aggregation queries scale out by adding nodes rather than scaling up a single instance. This matches the "5x growth over 5 years" trajectory in the requirements far better than a single scaled-up SQL Server tier — capacity is added incrementally, not by pre-provisioning for a 5-years-out peak.
- **Append-heavy write path.** Elasticsearch's write path (segment-based, Lucene-backed, refresh/merge model) is built around continuous document ingestion, not update-in-place — a strong match for immutable, append-only click events. There's no equivalent of B-tree index maintenance fighting against every insert.
- **Index Lifecycle Management (ILM) / time-based indices.** The standard pattern is one index per day/week (e.g., `clicks-2026.08.17`), with ILM policies to roll over, downsample, and eventually delete old indices automatically. This directly solves the open retention question flagged in `fn-analytics.md` Section 6 — "drop everything older than N days" becomes deleting whole indices, which is cheap, versus SQL Server's `DELETE`/partition-switch machinery bolted on afterward.
- **Cost/performance shape.** Because ingest and aggregation are both first-class design targets, the same volume of data is typically cheaper to query (for the aggregation-heavy access pattern this document is scoped to) than pushing a relational engine through columnstore/replica/partition tuning to approximate the same thing.

---

## 5. Where Elasticsearch Is Genuinely Worse — Be Honest About This

Elasticsearch is not a strictly-better database; it is a different tool with real weaknesses that matter elsewhere in this system:

- **No ACID transactions.** There is no multi-document transactional guarantee. A batch of events either gets indexed or it doesn't, per-document — there's no "all-or-nothing" semantics across a set of writes the way a SQL Server transaction gives you. For click events (each one independent, idempotent-ish, loss-tolerant per `fn-analytics.md` Section 4) this is an acceptable trade, but it would be unacceptable for anything requiring correctness guarantees, like decrementing a quota or updating the `ShortUrl` mapping itself.
- **Eventual consistency.** By default, a just-indexed document is not immediately visible to search/aggregation queries (the refresh interval, ~1s by default, controls this). Near-real-time is fine for a "clicks in the last hour" trend chart; it would be wrong for a system where a create must always be immediately, reliably visible on the next fetch (which is exactly why the core `ShortUrl` mapping does not belong here — see Section 6).
- **Operational complexity of running a cluster.** Elasticsearch requires actively managing shard counts, replica counts, node sizing, JVM heap tuning, and cluster health (yellow/red states, split-brain avoidance, snapshot/restore for backup) — meaningfully more operational surface area than a managed or well-understood SQL Server instance. This is a real cost: it is a second, different piece of infrastructure the team must learn to operate, monitor, and troubleshoot, not a drop-in replacement table.
- **Not a system of record.** Elasticsearch is explicitly not designed to be the durable, authoritative source of truth for data that must never be lost or inconsistently represented. Its own documentation and common practice treat it as a secondary/derived store fed from an authoritative source, not the other way around. Losing an index or having a cluster issue lose recent, unflushed documents is a tolerable risk for click analytics; it would be a serious incident for the URL mapping that the entire redirect path depends on.
- **No relational joins / referential integrity.** Elasticsearch is document-oriented; it doesn't enforce foreign keys or support relational joins the way SQL Server does. Any relationship between a click event and its `ShortUrl` (e.g., to show analytics alongside the link's live metadata) has to be handled at the application layer (denormalizing `ShortUrlId`/`Code` into each event document, or fanning out a lookup) rather than via constraint or join.

---

## 6. Comparison Summary

| Criterion | SQL Server (scaled) | Elasticsearch |
|---|---|---|
| Sustained write throughput (target: ~1,000–5,000+ events/sec at peak) | Achievable, but every index adds write-path cost; requires careful partitioning/index tuning to sustain | Native strength — built for continuous document ingest |
| Aggregation over billions of rows (counts, trends, breakdowns) | Requires columnstore indexes and/or read replicas to stay fast; still fundamentally a scan-and-aggregate operation on rowstore-derived structures | Native strength — inverted index + purpose-built aggregation framework is the core design target |
| Point lookup by ID | Fast (native strength) | Not the target access pattern; not needed for this workload anyway (Section 2) |
| Retention / rollover of old data | Partition switching — effective but bolted-on, needs explicit maintenance jobs | ILM + time-based indices — a first-class, designed-in feature (delete a whole index) |
| ACID transactions | Yes — full transactional guarantees | No — no multi-document transactions |
| Consistency | Strong (read-after-write) | Eventual (near-real-time, ~1s refresh by default) |
| Operational model | Well-understood, likely already-operated infrastructure in this stack | A second infrastructure component: cluster, shard, and JVM management |
| Fit as system of record | Yes — this is what it's for | No — explicitly not designed to be the durable source of truth |
| Storage/compute cost at 10s of TB with heavy aggregation load | Higher — general-purpose engine paying for guarantees this workload doesn't need | Lower, for this specific access pattern — cost model matches the workload |

---

## 7. Recommendation and Explicit Architectural Decision

**Recommendation: use Elasticsearch specifically for the click/analytics event store at v2 scale. Keep the core `ShortUrl` mapping table relational (SQL Server, or its successor per the data-design-guidelines migration path from SQLite).**

This is an explicit **polyglot persistence** decision, not a wholesale "switch the database" decision:

| Data | Store | Why |
|---|---|---|
| `ShortUrl` mapping (code → target URL, expiry, status) | **Relational** (SQL Server at v2 scale, per `data-design-guidelines.md`'s own migration guidance in Section 1) | Low volume relative to click events, needs strong consistency (a redirect must always resolve correctly), needs ACID guarantees, is the authoritative system of record the whole application depends on. Point-lookup-by-code is exactly what a relational primary-key/indexed lookup is best at. |
| Click/analytics events (the v2 successor to `ShortUrlAccessEvent`) | **Elasticsearch** | Extreme append-only write volume, near-zero updates, aggregation-heavy reads over time, relaxed consistency already accepted in the v1 design (`fn-analytics.md` Section 4), and a built-in answer (ILM) to the retention question v1 left open (`fn-analytics.md` Section 6). |

**Why the split, not a single answer for both:** the two datasets have opposite shapes. The mapping table is small, transactional, and correctness-critical — exactly what a relational engine is built for. The event store is huge, append-only, and aggregation-heavy — exactly what Elasticsearch is built for. Forcing both into the same engine means one of them is being poorly served; keeping them apart lets each store do what it's good at, at the cost of operating two systems instead of one. That operational cost (Section 5) is real and should be weighed against the throughput/aggregation gains — but at the stated scale (up to 100M events/day, aggregation-heavy AF-10 reads), the trade is justified.

**What this decision does not do:** it does not migrate the core mapping table to Elasticsearch, does not make Elasticsearch a system of record for anything, and does not imply Elasticsearch replaces SQL Server project-wide. It scopes Elasticsearch narrowly to the one dataset whose shape actually calls for it.

**Open follow-ups this document deliberately leaves for other considerations:** how events get from the redirect path into Elasticsearch without blocking the redirect (`05-kafka-comparison.md`, and an outbox-pattern consideration if the review calls for one), and how event ingestion should be buffered/batched under peak load — this document is scoped to the storage engine choice, not the ingestion pipeline.
