# Consideration 24 — Batching Click/Stat Recording via a Redis Staging Counter

**Version:** v2 (extreme-scalability review)
**Status:** Draft — architectural consideration, not yet a committed decision
**Scope:** This document answers one question — *at up to ~100M fetches/day, how should the system record that AF-08's "a click happened" without paying a durable-storage write on every single fetch?* It covers the **write side** of click/stat recording (AF-08/AF-09) using **Redis as a temporary, in-memory staging counter**, batch-flushed to durable storage on a fixed interval. It does not redesign the redirect/resolve mechanics (`v1/design/fn-fetch.md`), the detailed per-click event pipeline (`05-kafka-comaporison.md`/`25-elasticsearch-bulk-indexing.md`), or the Redis short-code lookup cache (`07-redis-caching-and-invalidation.md`) — all three are inputs this document builds on, not outputs of it.

**Revision note:** an earlier version of this document focused on a client-facing batch metadata API and internal request-coalescing of Redis *lookups* on the redirect **read** path. That was a misreading of the review scope item — the intended request was *"the fetch stat gathering can be batched with the help of a temp database in Redis,"* i.e. batching the **recording of clicks** (the write side), not the resolution of short codes (the read side). This revision replaces that content with the correct design. A short note on the batch-metadata-API idea is retained in the Appendix for continuity, since it remains a reasonable idea in its own right — it is simply not what this document is about anymore.

**Traceability:** `v1/design/fn-analytics.md` AF-08 (record an access/click event), AF-09 (total click count), AF-10 (analytics retrieval API). Directly implements the optimization `fn-analytics.md` §3.1 explicitly pre-authorized: *"If read-time aggregation later proves too slow at scale, introduce a maintained counter as an explicit, documented optimization."* This document **is** that documented optimization.

**Companion documents (not duplicated here, cross-referenced by filename):**
- `07-redis-caching-and-invalidation.md` — the *other*, pre-existing use of Redis in this architecture: a shared cache for short-code → redirect-target lookups on the **read** path. This document adds a **second, distinct** use of Redis — a write-side stat-aggregation buffer — and Section 2 below makes the distinction between the two explicit.
- `05-kafka-comaporison.md` — the event-transport decision for the detailed `UrlClicked` event: fetch → publish event → broker → analytics-indexing consumer → Elasticsearch.
- `25-elasticsearch-bulk-indexing.md` — bulk-indexes that detailed `UrlClicked` event stream into Elasticsearch. This document's Redis counter is a **different, complementary** mechanism for the simple aggregate "total click count" number (AF-09), not a replacement for that pipeline. Section 5 states this explicitly.
- `21-background-job-hosting.md` — establishes that periodic, scheduled background work in this system runs as a containerized Worker Service (`BackgroundService`). The flush job designed here is hosted the same way.
- `v1/design/fn-analytics.md` — AF-09's total click count is the only analytics metric in v1/v2 scope; this document does not add new metrics, only changes how the existing one is kept up to date.

---

## 1. The Problem: One Durable Write Per Fetch Doesn't Scale

`fn-analytics.md` §4 already made event *recording* asynchronous relative to the redirect response (fire-and-forget via an in-process `Channel<T>`), so the redirect itself never blocks on analytics. That solved *latency*. It did not solve *volume*: v1's mechanism still results in **one durable-storage write per fetch**, just performed slightly later, off the request thread.

At v2 scale that stops being a rounding error:

| Load | Fetches/sec |
|---|---|
| 5-year projected average | 100,000,000 / 86,400 ≈ **~1,157/sec sustained** |
| 5-year projected peak (5–10x) | **~5,800–11,600/sec** |

Reused directly from `05-kafka-comaporison.md` §2.2–2.3 and `25-elasticsearch-bulk-indexing.md` §5, so the numbers stay consistent across every v2 document that reasons about fetch volume.

Every one of those ~1,157 fetches/sec, on the naive design, drives a durable-storage write:

- A row insert into `ShortUrlAccessEvent` (already covered by the async queue + `05`/`25`'s broker-and-bulk-index pipeline for the *detailed* record — that part is fine and out of scope here), **and/or**
- An increment to a maintained `ShortUrl.ClickCount` counter in SQL Server, if/when `fn-analytics.md` §3.1's "maintained counter" optimization is adopted (which it is, by this document) — naively, that would mean `UPDATE ShortUrl SET ClickCount = ClickCount + 1 WHERE ShortCode = @code` **once per fetch**, i.e. ~1,157 individual `UPDATE` statements/sec average, ~6,000–12,000/sec at peak.

That is fundamentally the wrong shape of work for what the operation actually is: **"add 1 to a counter."** SQL Server pays full transaction/lock/log-write overhead per statement for an operation that carries almost no information (one integer, one identity). This is the same class of problem `25-elasticsearch-bulk-indexing.md` §1 already diagnosed for single-document Elasticsearch indexing — request/transaction overhead dominating actual work — just on the SQL Server side of the architecture instead of the Elasticsearch side, and specifically for the aggregate counter rather than the detailed event log.

---

## 2. The Mechanism: Redis as a Temporary Aggregation Buffer — a Second, Distinct Use of Redis

**On each successful fetch/redirect, increment a counter in Redis instead of writing to durable storage.** A Redis `INCR`/`HINCRBY` is an in-memory, single-threaded, sub-millisecond operation — cheap enough to pay on every fetch with no meaningful cost to the redirect path's latency budget, and it touches no durable store at all.

### 2.1 This is not the same Redis use as `07-redis-caching-and-invalidation.md`

`07-redis-caching-and-invalidation.md` already puts Redis in this architecture, as a **shared lookup cache**: `shorturl:v1:code:{shortCode}` → `{OriginalUrl, ExpiresAtUtc, IsActive}`, read-heavy, cache-aside, TTL + active invalidation, sized to hold the hot subset of short codes under `allkeys-lru` eviction.

This document's use of Redis is a **second, functionally distinct role**, and the distinction matters enough to spell out explicitly:

| | Lookup cache (`07-redis-caching-and-invalidation.md`) | Stat staging buffer (this document) |
|---|---|---|
| **Purpose** | Serve redirect decisions without a DB round trip | Accumulate click-count deltas without a DB write per fetch |
| **Access pattern** | Read-heavy (many reads per write) | Write-heavy (every fetch writes; reads happen only once per flush cycle) |
| **Data lifetime** | Long-lived per key, refreshed on TTL/invalidation | Deliberately short-lived — a counter exists only until the next flush, then is deleted |
| **Loss tolerance** | Losing an entry is free (repopulated from DB on next miss — DB is authoritative) | Losing an unflushed counter is a **real, if accepted, data loss** (Section 4) — Redis is transiently authoritative for not-yet-flushed deltas |
| **Eviction policy needed** | `allkeys-lru` — approximate, evict-anything-cold is correct (Section 3.1 of `07-...`) | **Must not be evicted under memory pressure before flush** — an LRU-evicted stat key is a silently lost click count, not a harmless cache miss |
| **Keyspace** | `shorturl:v1:code:*` | `stats:clicks:*` |

The last two rows are the important ones: `07-redis-caching-and-invalidation.md`'s `allkeys-lru` policy is *correct for the cache and wrong for the counters* — if both shared one Redis instance/keyspace under the same `maxmemory-policy`, an unflushed `stats:clicks:*` key could be evicted the same way a cold cache entry is, silently discarding not-yet-durable click counts as a side effect of a policy tuned for a different use case.

**Recommendation: a logically and physically separate Redis deployment for the stat buffer**, not merely a different key prefix on the same instance. Concretely:

- A small, dedicated Redis instance (or, at minimum, a separate database index within the same managed Redis offering, if a second instance is judged not worth the operational/cost overhead at initial v2 rollout) configured with `maxmemory-policy noeviction` for this keyspace — the buffer is deliberately tiny (Section 3.2) and short-lived, so it does not need LRU eviction at all; if it ever approaches its memory budget, that is a sign the flush job has fallen behind, and the correct response is to surface that as an operational alert, not to silently drop counters the way `allkeys-lru` would.
- A separate instance also isolates the two workloads' very different traffic shapes (read-heavy low-value-loss cache vs. write-heavy zero-tolerance-for-silent-loss counters) so that a spike in one does not compete for memory or connection-pool headroom with the other, and so the two can be scaled, monitored, and alarmed on independently.
- If cost/operational overhead argues against a second managed instance in an initial rollout, the fallback is a separate **database index** (`SELECT` in classic Redis, or the equivalent in a managed offering that supports multiple logical DBs) with its own `maxmemory-policy` — clearly the weaker isolation of the two options (still shares the underlying node's memory and I/O), but still avoids the eviction-policy conflict, which is the correctness-critical part of this recommendation.

### 2.2 Key design

```
stats:clicks:{bucket}          → Redis Hash: field = shortCode, value = accumulated delta count
```

Rather than one Redis key per short code (`stats:clicks:{shortCode}`), this design uses **one Redis Hash per flush-cycle time bucket**, with the short code as the hash field. This choice is explained fully in Section 3, but the short version: it makes the flush safe and atomic *by construction*, without needing a separate swap step, and it makes reading "everything accumulated this cycle" a single `HGETALL` regardless of how many distinct short codes were touched.

If a future need arises to track more than one metric per code in the same buffer (e.g., a coarse per-bucket device-type breakdown), the same Hash shape extends naturally with composite fields (`{shortCode}:mobile`, `{shortCode}:desktop`) — noted for completeness, not designed further here, since AF-09's total click count is the only in-scope metric (`fn-analytics.md` §1.1).

### 2.3 Write path — fetch/redirect flow

```csharp
// Application/Analytics — increments the Redis stat buffer; never awaited by the
// redirect response, and never touches durable storage.
public interface IClickStatRecorder
{
    ValueTask RecordAsync(string shortCode, CancellationToken cancellationToken);
}

public sealed class RedisClickStatRecorder : IClickStatRecorder
{
    private readonly IConnectionMultiplexer _statsRedis; // separate instance/DB — Section 2.1
    private const int BucketSeconds = 15;                // must match the flush worker's cadence

    public RedisClickStatRecorder(IConnectionMultiplexer statsRedis) => _statsRedis = statsRedis;

    public ValueTask RecordAsync(string shortCode, CancellationToken cancellationToken)
    {
        var db = _statsRedis.GetDatabase();
        long bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / BucketSeconds;
        string bucketKey = $"stats:clicks:{bucket}";

        // Fire-and-forget: this is a staging increment, not a request the caller
        // waits on — same "must not block the redirect" contract fn-analytics.md
        // §4 already established for IAccessEventRecorder.
        return new ValueTask(db.HashIncrementAsync(bucketKey, shortCode, 1, flags: CommandFlags.FireAndForget));
    }
}
```

This slots into the existing redirect flow alongside (not instead of) `fn-analytics.md`'s `IAccessEventRecorder` — the redirect handler calls both: the existing recorder still enqueues the detailed event for the broker/Elasticsearch pipeline (Section 5), and this new recorder increments the cheap aggregate counter.

---

## 3. The Batch Flush: Interval, Trigger, and the Atomic-Swap Mechanism

### 3.1 Flush interval: every 15 seconds

**Recommendation: a fixed 15-second flush cycle**, hosted as a `BackgroundService` Worker Service per `21-background-job-hosting.md`'s recommended model for periodic, scheduled work.

This is a deliberate staleness-vs-write-load trade-off, not an arbitrary number:

- **Why not sub-second (matching `25-elasticsearch-bulk-indexing.md`'s 1-second trigger):** that document's 1-second ceiling exists because the detailed event stream feeds Elasticsearch, where `fn-analytics.md`'s tolerance is "a few seconds of staleness" for a record a user might reasonably expect to see soon. AF-09/AF-10's total click count has no such expectation — `fn-analytics.md` §1.1 confirms it is the *only* metric in scope and carries no real-time requirement, and AF-10 is a pull-based read API (`GET /api/short-urls/{code}/analytics`), not a live dashboard. A count that is up to ~15–30 seconds behind reality is functionally indistinguishable from "current" to a link creator checking their stats.
- **Why not longer (e.g., 1–5 minutes):** a longer interval reduces DB write frequency further, but linearly increases the blast radius of the durability trade-off in Section 4 — every second of un-flushed accumulation is a second of counts that would be lost if Redis crashed before the next flush. 15 seconds keeps that exposure small while still capturing the overwhelming majority of the write-reduction benefit (Section 3.4).
- **15 seconds sits inside, and is the chosen point within, the 10–30 second range this kind of staging buffer is normally tuned to:** short enough that AF-10 reads feel current, long enough that the DB write-rate reduction is not marginal (Section 3.4 quantifies this at roughly three to four orders of magnitude fewer write round trips than per-fetch writes).

### 3.2 The atomic-swap problem, precisely

The hazard any buffered-counter design must avoid: a flush job that reads a counter and then resets it is not atomic unless the read-then-reset is designed carefully — an increment that lands **between** "read the value" and "reset the key" is either lost (if the reset zeroes out the increment along with everything already flushed) or double-counted (if the flush re-reads the same value on the next cycle before the reset takes effect). Naively doing `GET key` then `SET key 0` as two separate commands is exactly this bug: any `INCR` from a concurrent redirect request landing between those two commands is silently discarded.

### 3.3 Chosen mechanism: time-bucketed keys — no swap step needed at all

Rather than reading and resetting a *single* counter key (which requires an explicit atomic-swap primitive such as `RENAME` or `GETSET`/`GETDEL` to be safe), this design sidesteps the problem structurally by giving each flush cycle **its own key**, derived purely from wall-clock time:

```
bucket = unixTimeSeconds / 15         // integer division — a new bucket id every 15s
key    = "stats:clicks:{bucket}"      // e.g. stats:clicks:119303118
```

Because every writer (every redirect-handling API instance) computes the same `bucket` value independently from its own clock, and Redis is single-threaded (each `HINCRBY` executes atomically, one at a time, with no interleaving), the following holds without any explicit swap operation:

- **No increment is ever lost:** an increment issued at time *t* always targets the bucket key for *t*'s window. It is either accumulated into that bucket before the flush job reads it (included in the flush) or, if it lands after the wall clock has rolled into the next bucket, it targets the *next* bucket's key instead — which the *next* flush cycle will pick up. There is no window where an increment can target a key that has already been read-and-cleared, because "read-and-cleared" only ever happens to a bucket whose time window has already fully closed.
- **No increment is ever double-counted:** each bucket key is deleted exactly once, after being read exactly once, and no writer ever targets an already-flushed bucket again (its time window is in the past relative to every clock in the fleet).
- **The flush job reads one bucket per cycle with a one-bucket grace period**, to absorb ordinary clock skew across API instances:

```csharp
// Worker/ClickStatFlushWorker.cs — hosted per 21-background-job-hosting.md's
// containerized BackgroundService model.
public sealed class ClickStatFlushWorker : BackgroundService
{
    private const int BucketSeconds = 15;
    private const int GraceBuckets = 1;   // flush the bucket *before* the previous one

    private readonly IConnectionMultiplexer _statsRedis;
    private readonly IServiceScopeFactory _scopeFactory;

    public ClickStatFlushWorker(IConnectionMultiplexer statsRedis, IServiceScopeFactory scopeFactory)
    {
        _statsRedis = statsRedis;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(BucketSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            long currentBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / BucketSeconds;
            long bucketToFlush = currentBucket - GraceBuckets; // fully closed — no writer still targets it
            string bucketKey = $"stats:clicks:{bucketToFlush}";

            var db = _statsRedis.GetDatabase();
            HashEntry[] deltas = await db.HashGetAllAsync(bucketKey);
            if (deltas.Length == 0)
            {
                continue; // nothing accumulated this cycle — nothing to flush
            }

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IShortUrlRepository>();

            // Single batched round trip for every code touched this cycle — see 3.4.
            await repository.BulkIncrementClickCountsAsync(
                deltas.Select(e => (ShortCode: (string)e.Name!, Delta: (long)e.Value)),
                stoppingToken);

            // Safe to delete now: this bucket's time window has fully closed and
            // has already been read — no writer can still be targeting it.
            await db.KeyDeleteAsync(bucketKey);
        }
    }
}
```

`IShortUrlRepository.BulkIncrementClickCountsAsync` issues **one batched `UPDATE`** for the whole set of deltas (e.g., via a table-valued parameter or a `VALUES` list joined against `ShortUrl`), not one `UPDATE` per short code:

```sql
UPDATE su
SET su.ClickCount = su.ClickCount + d.DeltaCount
FROM ShortUrl su
JOIN @Deltas d ON d.ShortCode = su.ShortCode; -- @Deltas: table-valued parameter (ShortCode, DeltaCount)
```

- **Why a one-bucket grace period, not flushing the just-closed bucket immediately:** if API instance clocks are not perfectly synchronized (ordinary NTP drift of tens to a few hundred milliseconds, not a design flaw to engineer around from scratch), an instance whose clock is a moment behind could still compute the *previous* bucket id as "current" and issue an `HINCRBY` into it just after the flush worker's clock has already rolled over. Waiting one full extra bucket (15 s) before flushing gives every reasonably-synchronized instance in the fleet time to finish writing into a bucket before it's read — the grace period is a cheap way to make the "already-closed" assumption safe in practice, not just in theory.
- **Orphan cleanup:** each bucket key should also carry a short `EXPIRE` (e.g., 5 minutes) at creation, purely as a backstop — if the flush worker is down for an extended period, unflushed bucket keys age out on their own rather than accumulating in Redis indefinitely. On restart, the worker's normal loop naturally resumes flushing the (now grace-period-old) most recent buckets; any bucket old enough to have already expired represents genuinely lost data, which is the accepted trade-off Section 4 discusses.

### 3.4 Why this meaningfully reduces durable-store write load

Reusing the fetch-volume figures from Section 1, at a 15-second flush cycle:

| Load | Fetches in a 15s window | Individual per-click `UPDATE`s avoided | Batched `UPDATE`s issued |
|---|---|---|---|
| 5-year average (~1,157/sec) | ~17,355 | ~17,355 | **1** |
| 5-year peak (~11,600/sec) | ~174,000 | ~174,000 | **1** |

Regardless of how many *distinct* short codes are touched in a given 15-second window (worst case, every single click is a different code — still just one batched statement carrying that many rows), the number of **round trips to SQL Server** drops from one per click to one per flush cycle: roughly a **1,150x–12,000x reduction in write round trips**, the same order-of-magnitude pattern `25-elasticsearch-bulk-indexing.md` §5 already demonstrated for the analogous Elasticsearch bulk-indexing case. This is the mechanism that makes AF-10's `ShortUrl.ClickCount` maintainable as a live counter at 100M fetches/day without it becoming the new bottleneck `fn-analytics.md` §3.1 flagged as a risk of read-time `COUNT(*)` aggregation.

---

## 4. Durability Trade-off — Stated Plainly

Redis counters live in memory. If the stats Redis instance crashes or restarts between the last flush and the next one, **any increments accumulated since the last flush are lost** — they were never written anywhere durable. This means, under this design, `ShortUrl.ClickCount` becomes an **approximate, eventually-consistent** figure rather than an exact one: it can under-count (never over-count — nothing double-flushes, per Section 3.3) by up to one flush interval's worth of clicks in the event of a Redis failure at the wrong moment.

**This is an acceptable trade-off, stated honestly rather than glossed over, for reasons already established elsewhere in this project's own scope decisions:**

- `fn-analytics.md` §1.1 scopes analytics down to a single metric — total click count — explicitly as a "nice to have" reporting figure for the link creator, not a billing input, not a security/authorization signal, and not the system of record for anything else in the application. Nothing else in this system's correctness depends on `ClickCount` being exact.
- `fn-analytics.md` §4 already accepted "losing an occasional click event under extreme load" as a trade-off for the *detailed* event log's fire-and-forget recording — this document extends the same accepted risk posture to the *aggregate counter*, which carries strictly less information per lost increment (a count, not a full event) and is therefore an easier trade-off to accept, not a harder one.
- The exposure window is bounded and small: at most one flush interval (15 s, plus whatever data was in-flight at the moment of the crash) — not unbounded, not silent forever, and self-correcting the moment normal operation resumes (the next successful flush simply picks up wherever counters currently stand).

**Partial mitigation, not a full guarantee: Redis AOF (Append Only File) persistence.** Enabling AOF with a short `fsync` policy (e.g., `appendfsync everysec`) on the stats Redis instance means increments are periodically written to disk and can be replayed on restart, narrowing the loss window from "everything since the last successful flush" to "roughly the last second of increments before the crash." This is worth enabling given how cheap it is relative to the risk it removes, but it is explicitly **not** presented here as eliminating the risk — a hard crash between an `fsync` and the next one still loses that window's writes, and AOF adds its own (small, non-blocking-at-`everysec`) write overhead. The honest framing: AOF shrinks the blast radius, it does not close it to zero, and closing it to zero was never a requirement AF-09 imposed in the first place.

---

## 5. How This Composes With the Existing Event Pipeline — Complementary, Not Redundant

It is important that this design not be read as contradicting or replacing `05-kafka-comaporison.md`/`25-elasticsearch-bulk-indexing.md`. The two mechanisms serve genuinely different purposes and both remain in place, side by side, triggered from the same fetch:

| | This document — Redis staging counter | Existing pipeline — broker → Elasticsearch |
|---|---|---|
| **What it produces** | One number: total click count (AF-09), kept fresh in `ShortUrl.ClickCount` | The full detailed event record: timestamp, referrer, device type, per-click, queryable and aggregable (AF-08, `fn-analytics.md` §3) |
| **System of record?** | No — a fast, approximate, derived cache of the true count; can be rebuilt at any time by re-aggregating the detailed event log if it ever needs correcting | Yes — Elasticsearch is the durable, replayable, queryable system of record for what actually happened on each click |
| **Serves** | AF-10's `TotalClickCount` field, read cheaply (an indexed column read, not an aggregation query) | Any future richer analytics need (trends, referrer/device breakdowns) if `fn-analytics.md` §1.1's current out-of-scope items are ever revisited |
| **Loss tolerance** | Approximate is acceptable (Section 4) | Also fire-and-forget/best-effort per `fn-analytics.md` §4, but backed by the broker's durable retention and `25`'s dead-letter handling for partial failures — a stronger delivery guarantee than the counter buffer, appropriate to it being the system of record |

Concretely, one successful fetch triggers **two independent, parallel side effects**, neither depending on the other:

```
Successful resolve (fn-fetch.md)
        │
        ├──► IAccessEventRecorder.Enqueue(...)         (fn-analytics.md §4, unchanged)
        │        │
        │        ▼
        │    Broker (05-kafka-comaporison.md) → analytics-indexing consumer
        │        │
        │        ▼
        │    Elasticsearch bulk index (25-elasticsearch-bulk-indexing.md)  ← detailed record, system of record
        │
        └──► IClickStatRecorder.RecordAsync(...)        (this document)
                 │
                 ▼
             Redis stats:clicks:{bucket} HINCRBY          ← cheap, in-memory, no durable write yet
                 │
                 ▼ (every 15s, ClickStatFlushWorker)
             Batched UPDATE → ShortUrl.ClickCount          ← fast aggregate, AF-10's read path
```

If the Redis counter were ever lost entirely (Section 4's worst case), AF-09's total click count is not unrecoverable — it can, in principle, be reconciled from the Elasticsearch event log (a `COUNT` aggregation per short code), exactly the read-time computation `fn-analytics.md` §3.1 originally specified before this optimization. That reconciliation path is not designed in this document (it would be a scheduled, low-frequency backstop job, analogous in spirit to `20-outbox-pattern.md`'s reconciliation sweep referenced in `21-background-job-hosting.md`), but it is worth naming as the reason this design is safe to adopt: the fast counter is a performance optimization layered on top of data that still exists durably elsewhere, not a second, independent source of truth that could drift unrecoverably.

---

## 6. Summary of Decisions

| # | Concern | Decision | Traces to |
|---|---|---|---|
| 1 | What gets batched | AF-08/AF-09 click-count **recording** (write side) — not the read-path lookup, and not client-facing batch reads | AF-08, AF-09 |
| 2 | Increment mechanism | Redis `HINCRBY` on a time-bucketed Hash key (`stats:clicks:{bucket}`, field = shortCode), fire-and-forget from the redirect path | Cheap, sub-ms, non-blocking |
| 3 | Redis keyspace/topology | Separate from the `07-...` lookup cache — distinct keyspace, `noeviction` policy, recommended separate instance (DB-index fallback if cost-constrained) | Avoids `allkeys-lru` silently discarding unflushed counters |
| 4 | Flush interval | **15 seconds**, fixed | Staleness-vs-write-load trade-off; AF-10 has no real-time requirement |
| 5 | Atomic-swap mechanism | **Time-bucketed keys** (`unixTime / 15`) — no explicit swap/rename needed; each bucket is written once, read once, deleted once, by construction | Structurally avoids lost/double-counted increments |
| 6 | Grace period | Flush `bucket - 1`, not the just-closed bucket, with a 5-minute key `EXPIRE` as an orphan backstop | Absorbs ordinary clock skew across API instances |
| 7 | Durable write shape | One batched `UPDATE`/bulk operation per flush cycle across all touched short codes, not one per click | ~1,150x–12,000x reduction in write round trips (Section 3.4) |
| 8 | Durability trade-off | Accepted — analytics is explicitly non-critical per `fn-analytics.md` §1.1; AOF persistence recommended as partial mitigation, not a full guarantee | `fn-analytics.md` §1.1, §4 |
| 9 | Relationship to broker/Elasticsearch pipeline | Complementary — this counter serves only the fast aggregate number; the existing pipeline remains the system of record for the detailed per-click event log | `05-kafka-comaporison.md`, `25-elasticsearch-bulk-indexing.md` |

---

## Appendix — Batch Metadata API (Secondary Idea, Out of Focus)

The review scope item this document was originally written against also raised a genuinely different idea: a **client-facing batch metadata API**, `POST /api/short-urls/batch`, letting a caller (e.g., a dashboard rendering many links at once) fetch metadata for up to 100 short codes in one request/one round trip, instead of firing N sequential `GET /api/short-urls/{code}` calls. That idea is sound on its own terms — it is an ordinary, low-risk API design improvement, not subject to the redirect path's latency budget, and carries no meaningful downside — but it answers a **different question** (client-side read batching for the metadata endpoint) from the one this document now focuses on (server-side write batching for click-stat recording), and conflating the two was the source of the original misread.

If this API is wanted, it belongs as its own short, focused consideration (or as a follow-up appended here later) rather than sharing a document with click-stat batching — the two have essentially nothing in common mechanically (one is a `POST` endpoint doing a fan-out cache/DB read; the other is a Redis counter and a background flush job) beyond both loosely involving the word "batching." Not designed further here.
