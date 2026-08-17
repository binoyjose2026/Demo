# 10 — Database Partitioning / Sharding Strategy for the Core `ShortUrl` Table

**Version:** v2 (scalability exploration)
**Status:** Draft — architectural consideration, not a committed decision
**Scope:** This document covers **only** the core `ShortUrl` mapping table (short code → target URL, expiry, status) — the authoritative, transactional system of record the redirect path depends on (`fn-fetch.md`). It does **not** cover the click/analytics event store, which is already addressed separately and resolved to Elasticsearch (`03-elasticsearch-vs-sql-server.md`); that document explicitly scopes itself away from this table ("does not cover the core `ShortUrl` mapping table... which remains a relational, ACID system of record"). This document is the other half of that split.
**Traceability:** `01-create-path-extreme-scalability.md` §4 ("Partitioned/sharded relational store" is named there as a realistic direction and briefly sketched — this document is the deep-dive that section points to, not a duplicate of it); `data-design-guidelines.md` §§1–2, 4 (`Id`/`RowVersion` surrogate key conventions this scheme must remain compatible with); `nfr-scalability.md` §5 ("`Id`/`RowVersion` Conventions as Future Partitioning Groundwork" — explicitly deferred sharding design that this document now does); `fn-create.md` §6 (short-code generation); `fn-fetch.md` §5 (the hot lookup-by-code path this scheme must keep fast).

---

## 0. Framing — This Is a Late-Stage Escalation, Not a Day-One Design

`nfr-scalability.md` §5 already drew the line correctly for v1: *"no shard key, no partition scheme... is proposed in this document... sharding is a follow-on concern for a scale of traffic well beyond this PoC's stated scope."* This document is that follow-on, worked through for the 5-year extreme-scale horizon this v2 review is scoped to (1M→5M creates/day, 10M→100M fetches/day). It is written in the same spirit as `01-create-path-extreme-scalability.md`: a scoped exploration of what extreme scale *would* require, not a claim that the `ShortUrl` table needs sharding today, or even at the start of the 5-year horizon. Section 6 makes the "when" explicit and is arguably the most important section in this document — sharding is the most operationally expensive lever available, and reaching for it too early is exactly the kind of over-engineering this project's guidelines caution against.

---

## 1. Why a Single-Node Relational Database Eventually Hits a Wall

`nfr-scalability.md` §3 already names SQLite's single-writer file-locking ceiling as v1's binding constraint. The escalation path both `data-design-guidelines.md` §1 and `01-create-path-extreme-scalability.md` §4 point to first is a **server-based RDBMS with read replicas** (SQL Server/PostgreSQL) — and that escalation genuinely solves SQLite's specific problems (no network access, no true concurrent writers). But it does not make single-node relational storage limitless. At billions of rows and thousands of sustained writes/sec, a *properly scaled, properly tuned* server RDBMS — not just SQLite — runs into a different, harder ceiling than SQLite's. These are the concrete pressure points, not abstract ones:

| Pressure point | What actually happens | Why more hardware doesn't fix it |
|---|---|---|
| **Index size** | The unique index on `ShortUrl.Code` (`fn-fetch.md` §5, `IX_ShortUrl_ShortCode`) is the table's single hottest structure — every redirect and every create-time uniqueness check touches it. At billions of rows, a B-tree index no longer fits in buffer-pool memory; leaf-level lookups start paying random disk I/O per seek instead of resolving from cache. | You can add RAM, but the index keeps growing with the table — at some point the working set of "recently/frequently accessed codes" still fits in a smaller footprint than "the whole index," but a growing tail of cold, rarely-hit codes keeps pushing the index's total size past what any single box can cache, so cache-miss rate creeps up regardless of instance size. |
| **Write contention** | Every insert (`fn-create.md` §11) takes a write lock on the unique index while checking/enforcing uniqueness, and every insert appends to the transaction log. A single primary has exactly one log-writer stream; at sustained thousands of writes/sec, log-flush I/O and index-page-split contention become the binding constraint, not CPU. | Scaling vertically raises the ceiling but doesn't remove it — log writes are inherently serial per primary (it's what makes the primary the single source of truth), so there is a throughput number beyond which "buy a bigger box" stops moving the needle, only delays hitting it. |
| **Backup/restore time** | A full backup of a multi-terabyte table (billions of rows × a small row width still adds up) takes hours, and a restore-from-backup disaster-recovery drill takes at least as long. RTO/RPO commitments (however informal at this project's PoC stage — `01-summary.md` §H notes no formal SLA today, but a v2-scale production system would have one) become bounded by how fast one node's storage can stream a backup, not by application logic. | Faster storage/network helps linearly, but a table that's 10x bigger in 5 years (per this review's own growth assumption) needs a proportionally longer backup/restore window on the same architecture — the problem scales with data volume, which a single node's I/O bandwidth cannot outrun indefinitely. |
| **Vacuum/maintenance windows** | Index rebuilds, statistics updates, and (on PostgreSQL specifically) `VACUUM`/autovacuum passes to reclaim dead tuples all scan large fractions of the table. On a large, high-write table these maintenance operations either run continuously in the background (competing for the same I/O the write path needs) or require a scheduled window — and at billions of rows plus thousands of writes/sec, that background competition becomes measurable, ongoing latency pressure on the hot path, not a once-a-week non-event. | This is a structural property of maintaining a B-tree/MVCC relational table at this size, not a tuning gap — every row-oriented relational engine pays some version of this cost as table size grows, regardless of vendor or hardware tier. |

**Net effect:** these four pressures compound with table size and write rate in a way that a bigger single box, or even a well-configured primary-plus-read-replicas topology, cannot fully absorb past some point — because read replicas fan out *reads*, not writes, and none of the four pressures above are read problems. This is the honest reason partitioning/sharding eventually enters the conversation for a table with the growth trajectory this review assumes (cumulative rows into the billions over 5 years). Section 6 is specific about *how far* "properly tuned single primary + replicas" actually gets before that point is reached — it is farther than intuition suggests.

---

## 2. Partition Key Choice

### 2.1 The access pattern this key must serve

`fn-fetch.md` §5 is unambiguous about the dominant query: a single, indexed point lookup by `Code` on every redirect (ANFR-01, ANFR-05, ANFR-06 — the highest-volume operation in the whole system, 100M/day at the 5-year horizon). Any partitioning scheme that does not let that lookup go directly to the one partition holding the row — without querying every partition to find it — actively regresses the system's most latency-sensitive path. That constraint eliminates more options than it might first appear to.

### 2.2 Option A — Hash of the short code

Compute a stable hash of `Code` (or of the pre-allocated integer ID before obfuscation, per `01-create-path-extreme-scalability.md` §2.2 — see the note below on why the *code* is the safer choice) and use it to select a partition.

- **Lookup efficiency:** perfect fit for the hot path. `Code` is the only input available at lookup time (`GET /{shortCode}` — `fn-fetch.md` §3), and a hash of it is computable in the application/routing layer with no database round-trip, no secondary index, and no fan-out. One hash computation → exactly one partition → one indexed point read, identical in shape to today's single-table lookup.
- **Write distribution:** creates spread evenly across partitions, because a good hash function has no correlation with insertion time or any other attribute — no single partition absorbs a disproportionate share of write traffic no matter how creation volume grows or spikes.
- **Downside:** a short code carries no semantic meaning about *when* it was created, so hash partitioning gives up the ability to cheaply query "all links created this month" as a partition-local scan — that becomes a scatter-gather query (Section 5).

### 2.3 Option B — Time-based (creation date) partition

Partition by `CreatedAtUtc` (e.g., monthly or quarterly ranges), the standard approach for append-heavy tables — and in fact exactly what `03-elasticsearch-vs-sql-server.md` §3.1/§4 recommends for the *click-event* store (daily/weekly time-based indices with ILM rollover).

- **Lookup efficiency:** poor fit for this table specifically. A redirect request arrives with only `Code` — it carries no creation-date information the router can use to pick a partition without first knowing when that code was created, which is exactly the fact being looked up. Without a secondary code→date index (itself a scaling problem, and a second thing that must never be stale or missing), every lookup by code degenerates into a fan-out query across every time partition — the opposite of what the hot path needs.
- **Write distribution:** actively bad for this table's write pattern. All of "today's" creates land in the single most-recent time partition — the write-hot partition rotates forward but is never spread; this is the mirror image of the write-distribution property that makes time-based partitioning good for the append-only, sequentially-written click-event log, and a liability for a table whose writes need to spread evenly to avoid a moving hotspot.
- **Where it *would* win:** retention/rollover ("drop links older than N years") becomes cheap partition-drop instead of row-by-row delete — but this table has no stated retention/expiry-driven bulk-delete requirement the way the click-event log does (`fn-create.md` §8's `ExpiresAtUtc` is a per-row soft-delete-adjacent flag checked at read time, not a bulk-purge policy), so this advantage doesn't actually apply here.

### 2.4 Recommendation: hash of the short code

**Recommended partition key: a stable hash of `Code`.** This is a direct consequence of the access pattern in Section 2.1 — lookups are always by code, never by creation date, so the partition key must be derivable from the one input the hot path actually has. Time-based partitioning is the right call for the click-event store (already decided in `03-elasticsearch-vs-sql-server.md`) precisely because that workload's dominant query shape is time-range aggregation; the `ShortUrl` table's dominant query shape is point-lookup-by-code, which is the opposite profile.

**One refinement worth naming explicitly:** hash the `Code` string itself, not the pre-allocated raw integer ID from `01-create-path-extreme-scalability.md` §2.2, even though the ID is available at write time and might look like a cheaper hash input. Two reasons:
- The raw ID is allocated in **dense, sequential blocks per instance** (`[N, N+9999]`) specifically so creates avoid a per-request coordination round-trip — hashing the raw ID before its reversible-obfuscation step would put every ID in a given block on a *correlated*, not uniformly distributed, hash value if the hash function has any structure at all relative to sequential inputs, undermining the even-write-distribution property Section 2.2 needs.
- The redirect path only ever has `Code` (the obfuscated, public-facing value) to route with — it never sees the raw ID. Hashing `Code` means the exact same hash computation is used for both the create-time write route and the fetch-time read route, so there is one routing function, not two that must always agree.

### 2.5 Compatibility with the `Id`/`RowVersion` conventions

The prompt for this document is explicit that this scheme must be compatible with `data-design-guidelines.md`'s surrogate-key conventions, not replace them — and it is, with no change to either column's type or meaning:

- **`Id` stays exactly what `data-design-guidelines.md` §2 specifies:** an auto-incrementing `long`, the single surrogate primary key, per table. What changes is only the scope of "per table" — in a sharded topology, each shard hosts its own independent copy of the `ShortUrl` table (same schema, same `AuditableEntity`-derived columns, same EF Core mapping) with its **own independent `AUTOINCREMENT` sequence**. `Id` is unique *within its shard*, exactly as the convention already requires ("every table has a single surrogate primary key... `Id`") — nothing in that guideline claims or requires global uniqueness across separately-hosted copies of a table, so this is a scope clarification, not a deviation. `Code` (already required to be globally unique by the application's own business rule, independent of sharding) remains the system's actual global identifier for a link; `Id` remains the row's local, EF Core-idiomatic surrogate key exactly as designed.
- **`RowVersion` is completely unaffected.** A given row lives on exactly one shard for its entire lifetime — nothing about this scheme ever moves a row between shards or updates it from more than one place — so optimistic concurrency (`IsConcurrencyToken()`, `data-design-guidelines.md` §4) and delta/incremental-sync queries (`WHERE RowVersion > @lastSeenValue`) work identically to today, scoped per shard. A consumer that needs a fleet-wide delta feed queries each shard's `RowVersion` watermark independently and merges — an operational detail (Section 5), not a schema change.
- **Nothing about soft delete (§5), audit fields (§3), or naming (§6) changes.** Every shard's `ShortUrl` table is schema-identical to a single, unsharded table — the same EF Core Migrations (`data-design-guidelines.md` §8) apply to every shard, just replayed N times instead of once.

---

## 3. Sharding Topology

### 3.1 Consistent hashing vs. simple range-based sharding

- **Range-based sharding** (e.g., shard 0 owns hash range `[0, X)`, shard 1 owns `[X, 2X)`, …) is simple to reason about but has a well-known growth problem: adding a shard to relieve a hot/full existing shard requires recomputing and moving a large fraction of *every* shard's data to rebalance the ranges — an operation that gets more expensive precisely as the dataset grows, which is exactly the moment you're adding the shard to escape that expense.
- **Consistent hashing** (a hash ring, optionally with virtual nodes per physical shard for even distribution) is built specifically to solve that problem: adding a shard only requires moving the slice of the ring newly assigned to it — on the order of `1/N` of the data for `N` resulting shards — not a full reshuffle. This is the standard, well-established answer to "how do I grow shard count without a full-dataset migration."

### 3.2 Recommendation: consistent hashing, sized for room to grow

**Recommended topology: consistent hashing with virtual nodes, starting at a modest physical shard count with headroom built into the ring.**

- **Why this fits the read-heavy-by-code pattern specifically:** Section 2's routing function (hash `Code` → shard) is exactly what a consistent-hash ring computes — the same function serves both "which shard do I write this new row to" and "which shard do I read this code from," with no coordination service, no lookup table that itself needs to scale, and no fan-out. This is the core reason consistent hashing is the better fit here versus range-based: the *entire value* of hashing by code (Section 2.4) is undermined if growing the cluster then requires touching most of the data — that would reintroduce exactly the full-table-rewrite cost this design exists to avoid.
- **Starting shard count:** begin with a small number of **physical** shards (e.g., 4) mapped onto a larger number of **virtual** nodes on the ring (e.g., 128–256 total virtual nodes, ~32–64 per physical shard) — the virtual-node layer is what makes future rebalancing cheap and evenly distributed; the physical count is what's cheap to operate day one. Four shards is deliberately conservative: it is enough to break the single-primary write ceiling (Section 1) by a meaningful factor without taking on the operational cost (Section 5) of a large fleet before the data actually justifies it.
- **Growing the cluster:** adding a fifth physical shard means reassigning a subset of virtual nodes (and the ring-slice of data behind them) from the existing four shards to the new one — a background data-migration job moving roughly `1/5` of total data, not `4/5` as a naive range re-split would require, and the application-layer routing function (Section 4) needs only its ring-membership map updated, not a code change.

---

## 4. Application/Repository-Layer Routing

The routing decision must live entirely behind `IShortUrlRepository`, exactly as the Decorator-based caching layer already does in `nfr-scalability.md` §2 — no caller of `IShortUrlService`/`IShortUrlRepository` should be aware sharding exists at all. This keeps the abstraction from leaking into `Application` or `Api`, consistent with `design-guidelines.md` §2's stated purpose for the Repository pattern ("keeping `Infrastructure` swappable").

```csharp
// Infrastructure/Sharding/IShardResolver.cs — new seam, Infrastructure-only
namespace UrlShortner.Infrastructure.Sharding;

/// <summary>
/// Resolves which physical shard owns a given short code, via a consistent-hash ring
/// over the shards currently in the cluster (Section 3). No caller outside Infrastructure
/// depends on this — IShortUrlRepository is the only consumer.
/// </summary>
public interface IShardResolver
{
    /// <summary>Returns the connection/DbContext key for the shard that owns this code.</summary>
    string ResolveShardKey(string code);
}

public sealed class ConsistentHashShardResolver : IShardResolver
{
    private readonly SortedDictionary<uint, string> _ring; // ring position -> shard key

    public ConsistentHashShardResolver(IShardTopologyOptions topology)
    {
        _ring = BuildRing(topology.PhysicalShards, topology.VirtualNodesPerShard);
    }

    public string ResolveShardKey(string code)
    {
        var hash = StableHash.Compute(code); // same hash fn used at create-time and fetch-time (Section 2.4)
        // Walk the ring clockwise from `hash` to the first virtual node >= hash (wrap to first entry if none).
        foreach (var (ringPosition, shardKey) in _ring)
        {
            if (ringPosition >= hash) return shardKey;
        }
        return _ring.First().Value;
    }

    private static SortedDictionary<uint, string> BuildRing(IReadOnlyList<string> shards, int virtualNodesPerShard)
    {
        var ring = new SortedDictionary<uint, string>();
        foreach (var shard in shards)
            for (var v = 0; v < virtualNodesPerShard; v++)
                ring[StableHash.Compute($"{shard}#{v}")] = shard;
        return ring;
    }
}
```

```csharp
// Infrastructure/ShortUrls/ShardedShortUrlRepository.cs
// Same public contract as today's IShortUrlRepository — callers (ShortUrlService) do not change.
public sealed class ShardedShortUrlRepository : IShortUrlRepository
{
    private readonly IShardResolver _shardResolver;
    private readonly IShardDbContextFactory _dbContextFactory; // per-shard AppDbContext, one connection string per shard

    public ShardedShortUrlRepository(IShardResolver shardResolver, IShardDbContextFactory dbContextFactory)
    {
        _shardResolver = shardResolver;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<ShortUrl?> GetByShortCodeAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        var shardKey = _shardResolver.ResolveShardKey(shortCode);       // one hash computation
        await using var db = _dbContextFactory.CreateFor(shardKey);     // exactly one shard's DbContext
        return await db.ShortUrls.SingleOrDefaultAsync(s => s.ShortCode == shortCode, cancellationToken);
    }

    public async Task AddAsync(ShortUrl entity, CancellationToken cancellationToken = default)
    {
        var shardKey = _shardResolver.ResolveShardKey(entity.ShortCode);
        await using var db = _dbContextFactory.CreateFor(shardKey);
        await db.ShortUrls.AddAsync(entity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // stamps CreatedAtUtc/RowVersion=1, same as today (fn-create.md §11)
    }

    // ExistsByCodeAsync (fn-create.md §6/§7 collision + custom-alias checks) routes identically —
    // the candidate code IS the routing key, so this check also resolves to exactly one shard, never a fan-out.
}
```

**What this preserves from the existing design:**
- `IShortUrlService.CreateAsync`/`ResolveAsync` (`fn-create.md`, `fn-fetch.md`) call `IShortUrlRepository`/`IUnitOfWork` exactly as before — zero changes above `Infrastructure`.
- `ShortUrlsController`/`RedirectController` are unaffected — sharding is invisible three layers up, per `design-guidelines.md` §1's dependency-direction rule (`Application` depends on `Domain`-defined abstractions only).
- The Decorator-based cache (`nfr-scalability.md` §2, `CachingShortUrlRepository`) slots in **above** `ShardedShortUrlRepository` unchanged — it still wraps whatever implements `IShortUrlRepository`; it has no idea sharding exists underneath it, which is the entire point of composing Decorator over a swappable inner implementation.
- Registration is a DI swap (`design-guidelines.md` §6), consistent with how `data-design-guidelines.md` §1 already frames the SQLite→server-RDBMS move: `services.AddScoped<IShortUrlRepository, ShardedShortUrlRepository>()` replaces the single-`AppDbContext`-backed registration — `Application`/`Api` require no rebuild beyond that.

---

## 5. What Gets Harder — Honest Costs of This Approach

Sharding is not a free scale multiplier. These costs are real and should weigh directly into Section 6's "when":

- **Cross-shard queries.** "List all links for this department" (`OwnerDepartmentId`, captured per `fn-create.md` §4 though not yet enforced) has no single-shard answer once `ShortUrl` rows for one department are scattered across every shard by the code-hash key (Section 2) — a department's links land wherever their codes' hashes land, with no correlation to department. Answering this now requires either (a) a scatter-gather query fanning out to all N shards and merging results in the application layer, paying N times the query cost and taking on partial-failure handling if one shard is slow/unavailable, or (b) a secondary index/materialized view (e.g., department → code list, maintained separately, likely in the same async-event-driven way `01-create-path-extreme-scalability.md` §5 already proposes for other downstream-of-create work) — an entire additional piece of infrastructure to keep in sync. Either way, a query that is a simple indexed `WHERE` today becomes materially more expensive or requires new infrastructure.
- **Transactions that span shards.** A use case that needs to atomically touch two `ShortUrl` rows that happen to land on different shards (e.g., a hypothetical future "merge/transfer links between owners in one transaction") loses the single-database ACID guarantee `IUnitOfWork.SaveChangesAsync` gives today (`design-guidelines.md` §2). Distributed-transaction patterns (two-phase commit, sagas) exist but are themselves a significant complexity and latency cost, and are usually avoided by design rather than adopted — the honest position is that this scheme should be paired with **not building cross-shard-transactional features** in the first place, not with solving distributed transactions after the fact. Fortunately, nothing in the current functional design (`fn-create.md`, `fn-fetch.md`) needs a multi-row transaction that could span shards — each create/update touches exactly one `ShortUrl` row — so this cost is currently latent, not active; it becomes a real constraint only if a future feature needs it.
- **Operational complexity, multiplied by shard count.** Everything that was "manage one database" becomes "manage N databases": N sets of backups (Section 1's backup-time pressure is *relieved* per-shard but the operational job of running/verifying N backups instead of one is new work), N EF Core Migrations applied consistently (a migration must be replayed against every shard, and a partially-applied migration across shards is a new failure mode that doesn't exist with one database), N connection pools, N sets of monitoring/alerting dashboards, and shard-rebalancing operations (Section 3.2) that have no equivalent in a single-database world. This is real, ongoing operational burden that a single well-tuned server RDBMS with read replicas simply does not have.
- **Custom-alias validation stays single-shard (a genuine win, worth naming) but every other cross-cutting admin/reporting query does not.** Section 4's code sample shows `ExistsByCodeAsync` routes cleanly because the candidate code is itself the routing key — this is one of the few operations that *doesn't* get harder. It's called out here specifically so this section isn't read as "everything gets worse" — the hot paths (create, redirect, alias-uniqueness) stay exactly as cheap as an unsharded table; it's the secondary, lower-volume, cross-cutting queries that pay the cost.

---

## 6. When This Becomes Necessary — A Specific Threshold, Not "At Extreme Scale"

This is the section that keeps this document honest about not recommending sharding on day one.

### 6.1 What a single, well-tuned server RDBMS with read replicas actually absorbs

Server-based relational databases (SQL Server, PostgreSQL) at production scale routinely handle:
- **Table sizes in the low billions of rows** with acceptable point-lookup latency, given adequate RAM to keep the hot working-set slice of the index cached and properly maintained indexing (Section 1's "index size" pressure is real but has a long runway before it's the binding constraint — it degrades gradually, not catastrophically).
- **Sustained write throughput in the low thousands of writes/sec** on a single primary with modern NVMe storage and a properly sized transaction log, before log-flush contention (Section 1's "write contention" pressure) becomes the dominant latency source.
- **Read scale-out via replicas** that fully absorbs this table's *read* traffic growth (100M fetches/day at the 5-year horizon ≈ ~1,160/sec average, several thousand/sec at peak per `01-create-path-extreme-scalability.md`'s own framing) — reads are not the pressure that drives sharding for this table at all; a handful of read replicas plus the caching tier (`nfr-scalability.md` §2, and the Redis tier in `07-redis-caching-and-invalidation.md`) comfortably absorbs read growth without touching the primary's write path.

### 6.2 Where this review's own numbers land against that

- **Writes:** 5M creates/day at the 5-year horizon averages ~58/sec; even a generous peak multiplier (10–50x, per `01-create-path-extreme-scalability.md` §0) lands in the hundreds to low-thousands of writes/sec at peak — inside, or right at the edge of, what a single well-tuned primary can sustain, *provided* the write-path optimizations already recommended elsewhere in this series are in place (pre-allocated ID blocks reducing per-write contention, per `01-create-path-extreme-scalability.md` §2.2; async decoupling of non-critical downstream work, per §5).
- **Cumulative rows:** growing into the billions over 5 years (this document's own framing, Section 0) is real, but "billions" is still within the range Section 6.1 describes as absorbable by a single primary with proper indexing and maintenance discipline — it is a lot of data, but not automatically a sharding trigger by itself.

### 6.3 The recommendation

**Do not shard at the start of this 5-year horizon, and do not shard purely because "the data will eventually reach billions of rows."** Follow this escalation order instead, each step reversible and cheaper than the next:

1. **Migrate off SQLite to a server RDBMS with read replicas** — the escalation `data-design-guidelines.md` §1 and `01-create-path-extreme-scalability.md` §4 already establish as the first move, and the one that solves the actual v1 ceiling (no concurrent writers, no network access).
2. **Apply native, single-database table partitioning** (e.g., SQL Server partitioned tables/indexes) before reaching for multi-database sharding. This is a materially cheaper intermediate step than what this document describes: it stays a single logical database (one backup job, one migration target, no cross-shard query problem at all — Section 5's costs largely don't apply), while still pushing out the index-size and maintenance-window pressures from Section 1 by letting the engine operate on smaller physical partitions internally.
3. **Shard (this document's design) only once concrete, measured signals cross a threshold** — not a projection, an *observed* one:
   - Sustained peak write throughput against the primary consistently exceeds roughly **2,000–3,000 writes/sec** and vertical scaling/write-path tuning (step 1's replicas don't help here — replicas serve reads) is no longer closing the gap, **or**
   - Table size and index-maintenance windows (Section 1) have grown to the point that backup/restore time or maintenance operations no longer fit the system's actual operational SLA (whatever that is when this system carries a real production SLA, unlike today's PoC framing — `01-summary.md` §H), **or**
   - A genuine multi-region active-write requirement emerges that a single primary structurally cannot satisfy, independent of raw throughput.

Given this review's own numbers (Section 6.2), that threshold is plausibly reached **late in the 5-year horizon, at sustained peak load, not at the start of it** — and possibly not at all within 5 years if step 2's native partitioning is applied well. This document's design is the answer to have ready *when* that threshold is crossed, not a recommendation to build it now.

---

## 7. Summary of This Document's Positions

| # | Question | Position taken here | Traces to |
|---|---|---|---|
| 1 | Why does a single-node RDBMS eventually need partitioning? | Index size, write contention, backup/restore time, and maintenance windows all compound with table size/write rate past what one primary can absorb, independent of hardware tier | Section 1 |
| 2 | Partition key | Hash of `Code` (not creation date, not the raw pre-obfuscation ID) — matches the code-only lookup pattern on the hot redirect path | Section 2; `fn-fetch.md` §5 |
| 3 | Compatibility with `Id`/`RowVersion` | Unchanged in type/meaning — `Id` is per-shard-unique (as the convention already only requires per-table), `RowVersion` unaffected since a row never moves shards | Section 2.5; `data-design-guidelines.md` §§2, 4 |
| 4 | Sharding topology | Consistent hashing with virtual nodes, starting at ~4 physical shards, over range-based partitioning | Section 3 |
| 5 | Routing | `IShardResolver` + `ShardedShortUrlRepository`, fully behind the existing `IShortUrlRepository` contract — no leakage into `Application`/`Api` | Section 4; `design-guidelines.md` §2 |
| 6 | Costs | Cross-shard queries (department listings) need scatter-gather or a secondary index; cross-shard transactions are avoided by design, not solved; operational load multiplies by shard count | Section 5 |
| 7 | When | Not at the start of the 5-year horizon. Migrate off SQLite → try native single-database table partitioning → shard only once sustained peak writes exceed ~2,000–3,000/sec or backup/maintenance windows breach the operational SLA | Section 6 |

**This document does not claim the `ShortUrl` table needs sharding today, or that it will definitely need it within 5 years.** It is a scoped answer to "what would extreme scale require and when," per the review prompt's framing — consistent with every other document in this series.
