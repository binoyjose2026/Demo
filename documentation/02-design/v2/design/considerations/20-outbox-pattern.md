# Consideration 02 — Do We Need the Outbox Pattern?

**Version:** v2 (extreme-scalability review)
**Status:** Draft
**Scope:** This document answers exactly one question — does the create-short-URL write path need the Outbox pattern to safely publish events to the message broker? It does not re-litigate *which* broker (see `05-kafka-comaporison.md`) or the overall write-path architecture (see `01-create-path-extreme-scalability.md`, which establishes that create will publish events such as `UrlCreated` for downstream consumers — analytics indexing, cache invalidation, etc.). Builds on the v1 create flow in `../../v1/design/fn-create.md`, specifically §11 (the `IUnitOfWork.SaveChangesAsync` call that persists `ShortUrl`).

---

## 1. What the Outbox Pattern Is, and the Problem It Solves

Once `01-create-path-extreme-scalability.md` introduces "publish an event after creating a link," the create flow has **two side effects that must both happen, or neither**:

1. Commit the `ShortUrl` row to the primary database.
2. Publish a `UrlCreated` event to the broker for downstream consumers.

These are two independent systems (a database and a message broker) with no shared transaction. This is the classic **dual-write problem**: there is no atomic operation that does both, so any ordering of "write DB, then publish" (or the reverse) has a window where one succeeds and the other fails:

- **DB commit succeeds, publish fails** (broker down, network blip, process crash between the two calls) → the link exists and is fully usable, but no downstream system ever learns about it.
- **Publish succeeds, DB transaction later fails/rolls back** (e.g., a concurrent unique-index collision on `Code` is only discovered at commit, per fn-create.md §11's collision-retry note) → a "phantom" event exists for a link that was never actually created.

The **Outbox pattern** solves this by turning the second write into a *local, transactional* write instead of a remote network call: the event is written as a row in an `OutboxMessage` table, **inside the exact same database transaction** as the `ShortUrl` insert. Since both writes go through the same `SaveChangesAsync` call against the same SQLite/relational transaction, they succeed or fail together — atomically, for free, using the database's own transaction guarantee. A separate, decoupled process then reads unpublished outbox rows and relays them to the broker, retrying independently of the original request.

This converts "two operations that must agree" into "one atomic local write, plus an at-least-once relay" — which is a strictly easier problem.

---

## 2. Concrete Failure Scenario in This System

Walking through the v1 create flow (`fn-create.md` §11) with a naive "publish after commit" approach added:

```
await _unitOfWork.SaveChangesAsync(cancellationToken);   // ShortUrl row committed — code "abc1234" now resolves
await _eventPublisher.PublishAsync(new UrlCreated(code, originalUrl, ownerDepartmentId, createdAtUtc));
// ---> broker connection times out here (broker under load, network partition, pod restart) <---
return _mapper.ToResponse(shortUrl);   // 201 Created returned to caller regardless
```

- The caller gets `201 Created`. The short URL `abc1234` works immediately for redirects (fn-fetch.md path reads straight from the primary store/cache, not from the event).
- The `UrlCreated` event is lost. **Silently.** No exception surfaces to the caller — from their point of view, creation succeeded, because it did.
- Every downstream consumer described in `01-create-path-extreme-scalability.md` — the analytics/search index (candidate for the Elasticsearch consideration document), any cache-warming consumer, any audit/reporting pipeline — never learns `abc1234` exists.
- The result: a link that redirects correctly forever, but is permanently invisible in search/analytics dashboards, unless someone notices the discrepancy and reconciles it by hand (e.g., a nightly full-table diff against the index — expensive and exactly the kind of "silent inconsistency" the Outbox pattern exists to avoid).

The inverse failure (publish-before-commit, or a crash between publish and commit under a "publish first" ordering) is worse: a consumer indexes `abc1234` for search, a user clicks a shared link, and it 404s — a phantom, undermining trust in the index itself.

---

## 3. Recommendation: **No — not for `UrlCreated` at this system's scale, with one narrow exception.**

This is deliberately not the "always add Outbox, it's a known pattern" answer. Justify it against the actual numbers and the actual cost of the failure:

**Volume:** 1M creates/day today (~12/sec average, bursty peaks well under 1,000/sec even generously) growing to 5M/day (~58/sec average) in five years. This is nowhere near a scale where manual reconciliation of a rare failure class is operationally expensive.

**Failure cost — what's actually lost:**

| Data | What a dual-write failure loses | Real-world impact |
|---|---|---|
| The `ShortUrl` row itself (core mapping: code → original URL) | N/A — this is a single local DB write, not a dual write. No outbox needed for it. | This is the one row that *must* never be lost; it already is safe by definition, being a single transactional write. |
| The `UrlCreated` event (analytics index, cache warm, reporting) | A missed index entry / stale search result / a cache miss on next fetch (self-healing on first read) | **Low.** The link still works. Analytics undercounts by a statistically negligible amount. No user-facing breakage. |

The core guarantee — a created link always resolves — was **never at risk** from the dual-write problem in the first place, because the `ShortUrl` insert is a single database write with no broker involved. The dual-write problem is specifically about the *event*, and the event's downstream consumers are exactly the class of thing this project's own framing calls out as secondary: "analytics indexing, cache invalidation, etc." A missed analytics event is a data-quality nuisance, correctable by a periodic reconciliation job (compare primary-store row count/latest-`RowVersion` against the index, per the delta-sync pattern already established in `data-design-guidelines.md` §4). A missed core mapping write is not possible here because there is no dual write on that path.

At 1–5M events/day, broker client libraries with reasonable retry/backoff (and a broker chosen for durability — see `05-kafka-comaporison.md`) already make outright publish failure a rare event, not a routine one. Adding a second table, a relay/publisher process, monitoring for that process's lag, and the operational surface area that comes with it is real cost for a problem whose worst case is "the search index is occasionally a few seconds-to-minutes stale" — which a lightweight reconciliation sweep already has to handle anyway (broker delivery is at-least-once/best-effort in most realistic configurations regardless of Outbox).

**The narrow exception:** if a *future* event on this same path is used to drive something with a real consequence when missed — e.g., a billing/quota-debit event, or a security/compliance audit event that must be provable as "definitely fired" — that event should go through an outbox, because for that event class a missed publish is no longer a cosmetic nuisance. `UrlCreated` as currently scoped (analytics/search/cache) is not that. If document 01 or a later revision adds an event type with hard consistency requirements, revisit this recommendation for that specific event — the answer is not "never use Outbox in this project," it's "not for this event, at this scale, given what's actually lost."

**Verdict:** **No**, do not build the Outbox pattern for the v2 create path as scoped. Rely on: (a) the core `ShortUrl` write already being a single atomic local transaction with no dual-write exposure, (b) the broker client's own retry/acknowledgement semantics (durable producer config — see `05-kafka-comaporison.md`), and (c) a cheap, `RowVersion`-driven periodic reconciliation job (consistent with `data-design-guidelines.md` §4's `WHERE RowVersion > @lastSeenValue` delta pattern) as the backstop for the rare event that's still lost. That backstop is simpler to build and operate than a full Outbox + relay, and it is needed regardless of whether Outbox exists, because Outbox only guarantees the message *left this service* — it does not guarantee the downstream consumer processed it, so a reconciliation path is unavoidable either way.

---

## 4. If the Exception Applies — The Mechanism (For Reference)

Documented here so that if a future high-consequence event is added to this system, the mechanism doesn't need to be re-derived. **Not being built for `UrlCreated` today**, per §3.

### 4.1 Schema

`OutboxMessage` follows the same audit/RowVersion conventions as every other table in this project (`data-design-guidelines.md`, Standard Column Set):

```csharp
public sealed class OutboxMessage : AuditableEntity   // Id, CreatedAtUtc, CreatedBy,
                                                        // LastModifiedAtUtc, LastModifiedBy,
                                                        // RowVersion, IsDeleted, DeletedAtUtc
{
    /// <summary>Event type discriminator, e.g. "UrlCreated". Consumers dispatch on this.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Serialized event payload (JSON), e.g. the UrlCreated DTO.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Null until the relay has successfully published this row to the broker.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>Number of publish attempts so far, for retry/backoff and dead-lettering.</summary>
    public int AttemptCount { get; set; }
}
```

```sql
-- Equivalent raw SQL, standard columns per data-design-guidelines.md plus the outbox-specific ones:
Id                INTEGER PRIMARY KEY AUTOINCREMENT,
CreatedAtUtc      TEXT    NOT NULL,
CreatedBy         TEXT    NOT NULL,
LastModifiedAtUtc TEXT,
LastModifiedBy    TEXT,
RowVersion        INTEGER NOT NULL DEFAULT 1,
IsDeleted         INTEGER NOT NULL DEFAULT 0,
DeletedAtUtc      TEXT,
EventType         TEXT    NOT NULL,
Payload           TEXT    NOT NULL,
PublishedAtUtc    TEXT,
AttemptCount      INTEGER NOT NULL DEFAULT 0
```

Index: `IX_OutboxMessage_PublishedAtUtc` (or a composite `IX_OutboxMessage_PublishedAtUtc_CreatedAtUtc`) — the relay's core query is "find unpublished rows, oldest first," so this is a frequent-`WHERE`/`ORDER BY` index per `data-design-guidelines.md` §7.

### 4.2 Write path (same transaction as the entity write)

```csharp
await _unitOfWork.Repository<ShortUrl>().AddAsync(shortUrl, cancellationToken);
await _unitOfWork.Repository<OutboxMessage>().AddAsync(new OutboxMessage
{
    EventType = nameof(UrlCreated),
    Payload = JsonSerializer.Serialize(new UrlCreated(shortUrl.Code, shortUrl.OriginalUrl, shortUrl.CreatedAtUtc)),
}, cancellationToken);

await _unitOfWork.SaveChangesAsync(cancellationToken); // single transaction — both rows commit or neither does
```

### 4.3 Relay/publisher

A separate, independently-deployable process (or hosted background service) that:

1. Polls (or is woken by) `SELECT * FROM OutboxMessage WHERE PublishedAtUtc IS NULL ORDER BY Id LIMIT N`.
2. Publishes each row's payload to the broker.
3. On success, sets `PublishedAtUtc = UtcNow` (an update — bumps `RowVersion` and `LastModifiedAtUtc` per the standard convention).
4. On failure, increments `AttemptCount` and retries with backoff; a row stuck past a threshold attempt count is routed to a dead-letter path for manual inspection rather than retried forever.

**Alternative relay implementation — CDC:** instead of an application-level polling relay, a Change Data Capture tool (e.g., **Debezium**) can tail the database's write-ahead/transaction log and stream new `OutboxMessage` rows to the broker directly, without the application polling its own table.

**Which one for this project, if/when the exception applies:** the polling relay, not CDC/Debezium. CDC is the better choice at genuinely high throughput because it avoids polling overhead and gets near-real-time propagation straight from the transaction log — but it requires a log-shipping-capable database (SQL Server CDC, PostgreSQL logical replication, MySQL binlog) and a Kafka Connect/Debezium deployment to operate. This project's primary store is SQLite (`data-design-guidelines.md` §1), which has no CDC/log-streaming support at all, and even the "grow past SQLite" trajectory implied elsewhere in this v2 review lands on a mainstream server RDBMS, not a CDC-first architecture. A simple polling relay (a hosted `BackgroundService` or a small worker) is trivially simple to build, test, and reason about at this project's volumes, and avoids introducing an entirely new piece of infrastructure (Kafka Connect) for a benefit (near-real-time relay latency) this project's failure-cost analysis in §3 says isn't needed.

---

## 5. Explicit Trade-Offs / Exceptions (Named, Not Hidden)

Per this project's convention of calling out trade-offs rather than hiding them:

1. **The recommendation is "no" for `UrlCreated`, which means the dual-write risk for that event is accepted, not eliminated.** A rare publish failure will occasionally leave a link invisible to analytics/search until the reconciliation sweep catches it (§3). This is a deliberate scope/cost decision, not an oversight — revisit if an event with hard consistency needs is added later.
2. **If Outbox is adopted for a future event, it is not free even though it solves the dual-write problem:**
   - **Added latency before the event is visible downstream** — the event isn't published at commit time, it's published whenever the relay next polls (typically sub-second to a few seconds, tunable), which is a real (if usually small) delay compared to a direct publish.
   - **An extra table to maintain and grow-manage** — `OutboxMessage` accumulates a row per event; it needs its own retention/cleanup policy (e.g., purge or archive rows with `PublishedAtUtc` older than N days) or it grows unbounded, same operational category as any other high-write-volume table.
   - **A new process to deploy, monitor, and alert on** — the relay is a new operational component with its own failure mode (relay itself down → nothing publishes even though the DB writes succeed fine), so "Outbox solves the dual-write problem" is not the same as "Outbox has no failure modes of its own" — it narrows the failure surface, it doesn't remove it.
   - **At-least-once delivery, not exactly-once** — a relay crash after publish but before marking `PublishedAtUtc` will republish on restart, so downstream consumers still need to be idempotent (e.g., dedupe on the `UrlCreated` event's short code + a monotonic version). This is unrelated to whether Outbox is used and would be true of any at-least-once broker delivery — noted here because it's a common misconception that Outbox implies exactly-once end-to-end.
3. **The reconciliation-sweep backstop (§3) is required either way.** Even with Outbox in place, the guarantee stops at "the message left this service" — it says nothing about whether the downstream consumer actually processed it. A periodic diff/reconciliation job is not a fallback for skipping Outbox, it's a permanent, independent piece of the design regardless of this decision.
