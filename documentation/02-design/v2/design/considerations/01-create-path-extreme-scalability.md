# 01 — Create (Write) Path: Extreme Scalability Overview

**Series:** v2 Scalability Review — this overview plus companion documents 03–07 and 20 (see Section 6, "Where to Go Next")
**Status:** Draft — architectural exploration, not an implementation commitment
**Scope of this document:** Overview only. Each numbered concern below is drilled into by a dedicated companion document (see Section 6, "Where to Go Next"). This document does not duplicate their content — it frames *why* each is needed and how they fit together.

---

## 0. Why This Review Exists, and What It Is Not

This is a **scoped "what would we do at extreme scale" exploration**, requested against a specific hypothetical load:

| Metric | Today | 5-year horizon |
|---|---|---|
| Link creations/day (writes) | 1,000,000 | 5,000,000 |
| Fetches/day (redirects, reads) | 10,000,000 | 100,000,000 |

At today's volume, 1M creates/day averages ~12 creates/sec, and 10M fetches/day averages ~116 fetches/sec — modest, bursty numbers a single well-tuned instance could plausibly absorb. The interesting engineering problem is the **5-year number and its peak multiplier**: 5M creates/day (~58/sec average, plausibly 10-50x that at peak — flash-traffic events, marketing campaigns, bot-driven bulk creation) and 100M fetches/day (~1,160/sec average, likely several thousand/sec at peak). This document, and the six that follow it, design for that peak-load future — not because v1's choices were wrong for their context, but because a different scale changes which trade-offs are correct.

**This is explicitly not a claim that v1's SQLite + `IMemoryCache` design was a mistake.** `data-design-guidelines.md` §1 frames SQLite as the right fit for "a single-application, low-to-moderate concurrency workload" and pre-commits to exactly this escalation path: *"If the project later needs multi-user server concurrency, horizontal scale, or remote access, that is a signal to migrate to a server-based RDBMS."* `nfr-scalability.md` §3 and §4 already name SQLite's single-writer ceiling as a documented Exception and lay out the swap-the-provider escalation path. This v2 review is that escalation, worked through in detail for a specific target scale. v3 will trim the result back down to whatever the actual shipping requirement turns out to be — this document is not proposing that the extreme-scale design ships as-is.

This document covers the **create (write) path only** — AF-01 (create a short URL) and AF-04 (system-generated short code). The read/redirect path (ANFR-05, ANFR-06) is addressed by the caching/CDN/BFF documents in this series (06, 07) where it intersects with write-path decisions (e.g., cache invalidation on create).

---

## 1. Why the v1 Approach Hits a Wall at This Scale

`nfr-scalability.md` §3 already documents SQLite's ceiling as an Exception for v1's actual (low) traffic shape. At 5M creates/day peaking in bursts, three specific bottlenecks stop being theoretical and start being the binding constraint:

### 1.1 Single writer, file-level locking
SQLite serializes all writers against one database file — there is effectively one write transaction in flight at a time for the whole system (`data-design-guidelines.md` §1). Every `POST /api/short-urls` call is a write (the `ShortUrl` insert). At a sustained peak of, say, 500-2,000 creates/sec, write transactions cannot be parallelized no matter how much application-layer concurrency exists in front of them — each request queues behind the last writer's commit. This isn't a tuning problem; it's the storage engine's concurrency model.

### 1.2 No horizontal scale-out of the database tier
`nfr-scalability.md` §4 already states this plainly: "the API layer can be horizontally scaled today; the database tier cannot." A SQLite file is local to one filesystem — running N stateless API instances against the *same* file over shared/network storage is a fragile, unsupported pattern (`data-design-guidelines.md` §1: "no native network access"), not a scale-out strategy. Adding more API instances at this scale does not add write capacity; it adds more processes contending for the same single-writer bottleneck.

### 1.3 No distributed cache — per-instance staleness compounds under scale-out
`nfr-scalability.md` §4 already flags this as an accepted trade-off at v1 scale: `IMemoryCache` is per-process, so cache hit rate is per-instance and invalidation (on delete/deactivate) only clears the instance that handled the request — other instances serve stale data until TTL expiry. At v1's traffic this is a bounded, tolerable staleness window. At 100M fetches/day spread across dozens of horizontally-scaled API instances (Section 3), the *number* of instances holding independently-stale copies of a given short code grows with fleet size, and the fraction of total traffic that can hit a cold per-instance cache also grows — every instance has to independently re-warm every hot code it happens to receive traffic for. This is the setup for the Redis-based distributed cache in doc 06.

**Net effect:** none of these three bottlenecks are fixed by "buying a bigger box" or "adding more API instances" alone. Each requires a specific architectural change — a different ID-generation scheme (Section 2), a different datastore (Section 4), and a shared cache tier (doc 06) — which is exactly why this review is structured as a series of targeted documents rather than one blanket "scale it up" recommendation.

---

## 2. Short-Code / ID Generation at Scale (AF-04)

### 2.1 Why v1's approach doesn't survive as-is

`fn-create.md` §6 documents v1's decision: **random 7-character base62 candidate, retried against the persistence layer on collision** (`ExistsByCodeAsync` pre-check, with a `DbUpdateException` unique-constraint violation as the authoritative fallback safety net), bounded at 5 attempts. This was the *correct* choice for v1 — it satisfies AF-04's collision-handling requirement and ANFR-08's non-enumerability requirement without over-engineering, and the collision math in `fn-create.md` §6 (≈0.0014% birthday-bound collision probability at 10M links) was calculated against v1's assumed scale.

Two things change at extreme scale, independent of each other:

- **The uniqueness check itself becomes a write-path bottleneck.** `ExistsByCodeAsync` is a read, but the authoritative guarantee is the unique index enforced at insert — under many concurrent writers across multiple API instances (Section 3) hitting a single logical table, every collision check and every insert contends for the same index, and retries under contention (a collision *and* a concurrent write racing it) become more frequent exactly when the system is under the most load. A single-writer SQLite file amplifies this; even a multi-writer server RDBMS (Section 4) still pays a round-trip and an index-contention cost per attempt.
- **The birthday-bound math changes at 5-year volume.** At 5M creates/day sustained for 5 years, cumulative created links approach ~9 billion (5M × 365 × 5, plus the preceding years' volume). Recomputing `fn-create.md`'s formula at this order of magnitude (62⁷ ≈ 3.5×10¹² possible codes) still yields a low absolute collision probability, but the *retry-under-contention* cost (previous bullet) — not raw collision probability — is the actual problem at scale, and it gets worse precisely as instance count and write concurrency grow.

### 2.2 Recommendation: pre-allocated ID blocks per instance, encoded to a short code

**Recommended approach:** each stateless API instance requests and locally caches a **contiguous block of unique integer IDs** (e.g., 10,000 at a time) from a lightweight, centralized block-allocator (a single-row "next available block" counter in the primary datastore, or a dedicated sequence/allocator service), then assigns IDs to incoming create requests **out of its local block, in-process, with no network round-trip and no collision check per request**. Each assigned ID is encoded to a short code via base62 encoding, exactly as v1's rejected "incrementing ID + base62" alternative — but obfuscated before encoding (see below) so it does not reintroduce the enumerability problem `fn-create.md` §6 explicitly rejected that approach for.

Concretely:
- Instance requests block `[N, N+9999]` from the allocator (one contended write, amortized over 10,000 creates).
- Instance hands out `N, N+1, N+2, …` locally as creates arrive — zero contention, zero collision risk *by construction* (each block is disjoint by allocation, not by chance).
- Before base62-encoding, each raw ID is passed through a reversible bit-mixing/permutation step (e.g., a Feistel-network-based format-preserving obfuscation, or XOR-and-bit-rotate against a fixed application secret) so consecutive raw IDs do not produce consecutive-looking output codes — satisfying ANFR-08's non-enumerability requirement the same way v1's random approach did, but deterministically instead of via retry.
- On instance restart/crash before exhausting a block, the unused remainder of that block is simply abandoned (gaps in the ID space are harmless — nothing in AF-01/AF-04 requires dense, gapless codes).

**Alternative considered and not recommended as the primary mechanism: Snowflake-style IDs** (timestamp-bits + machine-id-bits + sequence-bits packed into a 64-bit integer, generated fully locally with no allocator round-trip at all). This is a legitimate, widely-used pattern (Twitter/X's original Snowflake, Discord, Instagram's variant) and is **worth naming as the fallback if the block-allocator's single centralized counter ever becomes its own bottleneck** at the 5-year peak — because Snowflake IDs require no shared allocator at all, they scale writer-count with zero added contention. It is not the *primary* recommendation here because:
- It requires a reliable per-instance "machine ID" assignment mechanism (avoiding two instances issued the same machine ID, which reintroduces a coordination problem it was meant to avoid) — solvable, but it's an added operational dependency (e.g., on a coordination service) that pre-allocated blocks avoid by only needing a single monotonic counter.
- The resulting 64-bit ID is larger than a pre-allocated block's dense counter once encoded, producing a slightly longer base62 short code for the same time horizon (Snowflake reserves bits for timestamp + machine ID that a dense counter does not need) — a minor but real cost against the "short" in "short URL."
- Pre-allocated blocks give a simpler mental model and audit trail (the allocator's single counter is trivially inspectable) for a system that, per Section 2.1, does not yet have so many writer nodes that a shared counter is genuinely the bottleneck.

**Recommendation in one line:** pre-allocated ID blocks (10K per instance, refilled on exhaustion) + reversible obfuscation before base62 encoding, with Snowflake-style local generation documented as the next escalation if the block allocator itself becomes contended at higher instance counts than currently planned.

### 2.3 Why this beats "just keep the random-retry approach, scaled"

- **Collision risk:** eliminated by construction (disjoint blocks), not merely made statistically improbable — no retry loop, no `MaxGenerationAttempts` exhaustion path (`fn-create.md` §6's `500` error case) needed for system-generated codes at all.
- **Contention:** one allocator round-trip per 10,000 creates, not one uniqueness check + one insert per create — a ~4-order-of-magnitude reduction in contention against the shared counter.
- **Non-enumerability (ANFR-08):** preserved via obfuscation, not sacrificed for the throughput win.
- **Custom aliases (AF-01, Q20) are unaffected** — this section only changes *system-generated* code assignment (`fn-create.md` §6); the custom-alias uniqueness path (`fn-create.md` §7) still needs a uniqueness check against caller-supplied input, which is a comparatively rare path (opt-in) and not the throughput-critical one.

---

## 3. Horizontal Scaling of the API Layer

This is the one piece of the write path that v2 does **not** need to redesign — `nfr-scalability.md` §4 already establishes it correctly for v1 and the same reasoning holds, just at larger fleet size:

- Controllers and `Application` services are already stateless (thin controller → one service call → response mapping, no per-request/cross-request mutable state, per `design-guidelines.md` §3). Any number of instances can run behind a load balancer with no session affinity requirement.
- What changes at extreme scale is **how many** instances and **what they coordinate on**: at peak (thousands of creates/sec), the fleet needs auto-scaling behind the load balancer, health-check-driven instance replacement, and — per Section 2 — each instance independently owning its own ID block, which is precisely designed to *not* require coordination on the hot path.
- The one thing that must be externalized as the fleet grows (already flagged in `nfr-scalability.md` §4 as a "future" concern, now a "now" concern): any per-caller rate-limit counter for the usage ceiling check (`fn-create.md` §5 step 2, §10; ANFR-09) must live in a shared store (e.g., the same Redis tier proposed in doc 06), not in-process — otherwise a caller's usage ceiling is only enforced per-instance, not fleet-wide, which defeats the quota's purpose once there are enough instances for a caller to spread requests across.

No dedicated document in this series re-covers this point in depth — it's carried forward largely unchanged from `nfr-scalability.md` §4, just validated at larger scale.

---

## 4. Datastore: Moving Off a Single SQLite File

Section 1.1–1.2 already established *why* a single SQLite file cannot be the write-path datastore at this scale. This section is a brief pointer to the realistic options, not a full comparison — the SQL-vs-Elasticsearch and Elasticsearch-vs-MongoDB trade-offs get their own dedicated documents (03, 04).

Realistic directions for the **primary, authoritative write-path store**:

- **Server-based relational DB with read replicas** (SQL Server, PostgreSQL) — the escalation path `data-design-guidelines.md` §1 already pre-authorizes ("migrate to a server-based RDBMS... EF Core's provider model makes that a swappable decision, not a rewrite"). Because v1 already isolates all data access behind `IRepository<T>`/`IUnitOfWork` (`design-guidelines.md` §§1-2), this remains largely a provider-and-connection-string swap in `Infrastructure`, not an `Application`/`Api` rewrite. Read replicas absorb the (much larger) read/redirect volume; the primary handles writes. This does not, by itself, solve single-primary write contention at extreme scale — it solves "no network access" and "no true multi-writer concurrency" versus SQLite, but a single relational primary still has a ceiling.
- **Partitioned/sharded relational store** — split the `ShortUrl` table by a shard key (e.g., hash of the short code, or the high bits of the pre-allocated ID block from Section 2) across multiple database instances, each independently write-capable. This directly removes the single-primary ceiling but adds cross-shard query complexity (e.g., "does this alias already exist" custom-alias checks now need to query the correct shard or a secondary lookup index) and operational overhead (shard rebalancing, cross-shard transactions).
- **Purpose-built high-write-throughput stores** (the subject of docs 03/04) — this is where Elasticsearch-vs-SQL-Server (doc 03) and Elasticsearch-vs-MongoDB (doc 04) pick up: whether a document/search-oriented store is a better fit for some part of this system's data (likely the read-heavy/analytics side more than the authoritative write-of-record — see those documents for the actual argument, which is not restated here).

**This document's position:** the authoritative, transactional write-of-record for "this code maps to this URL" is a relational-shape problem (small row, strict uniqueness constraint, needs ACID on insert) and is best served by a server RDBMS with read replicas and/or sharding, not by a search engine or document store as the primary write target — but the *rationale* for that split, and where Elasticsearch/MongoDB genuinely earn their place in this architecture, is doc 03/04's job to make, not this overview's.

---

## 5. Decoupling "Acknowledged" from "Done": Why the Write Path Needs Async Events

### 5.1 The problem

`fn-create.md` §2's end-to-end flow already does more than one insert conceptually adjacent to "create a short URL": validation, malicious-link checking (`ILinkSafetyChecker`), audit-field stamping, and (per `nfr-scalability.md` §3's Exception) analytics/click-count infrastructure that must not block the hot redirect path. At v1 scale, doing this work inline is fine — it's the *dominant cost* of a low-volume operation. At 5M creates/day peaking in bursts, every additional synchronous unit of work on the create request's critical path is work the caller waits on, multiplied across every concurrent create — and it's work that has nothing to do with whether the create *itself* succeeded.

The specific downstream work that does not belong on the synchronous critical path of a `201 Created` response:
- **Analytics indexing** — making the new link discoverable/reportable in whatever store backs AF-05/AF-10 metadata and analytics retrieval (a candidate for the Elasticsearch options in docs 03/04).
- **Moderation / follow-up checks** beyond the inline `ILinkSafetyChecker` gate (e.g., asynchronous deeper scanning, if a future requirement adds one).
- **Cache warm/invalidate** — proactively populating the distributed cache (doc 06) for a newly-created code, or invalidating any stale negative-lookup cache entry.

### 5.2 Why "just await it inline, but make each step fast" isn't the answer at this scale

Making each downstream step individually fast reduces *latency* but does not reduce *coupling*: if the create request's success is contingent on an analytics-indexing call succeeding, then an analytics-store outage becomes a create-path outage — which is precisely the kind of availability coupling a scalability review at this volume has to design out. The fix is architectural, not a latency optimization: the create request should be **acknowledged (durably persisted, `201 Created` returned) as soon as the authoritative write succeeds**, and everything downstream should happen via **asynchronous event publication** that the create request does not wait on.

### 5.3 What this requires — pointers, not mechanics

Two questions fall out of "publish an event after the create," and each has a dedicated document in this series because the mechanics are non-trivial:

- **How do we guarantee the event is actually published, given that the database write and the event publish are two separate operations that could partially fail (write succeeds, publish fails, or vice versa)?** This is the dual-write problem, and it's the reason this series has a dedicated document — see **20-outbox-pattern.md** — rather than this overview hand-waving "just publish an event."
- **What's the actual event transport/broker, and is it warranted at this volume versus a simpler alternative?** See **05-kafka-comparison.md** for whether Kafka specifically is the right fit for this event volume (5M create-events/day at peak, plus whatever downstream consumers multiply that by) versus lighter-weight alternatives.

This document takes no position on Outbox-vs-not or Kafka-vs-not — that's deliberately those two documents' job. What this section establishes is only the *shape* of the requirement: the create path's critical section is "validate, generate/reserve code, persist" (Sections 2 and 4); everything else is a downstream, asynchronously-triggered consequence of that persisted fact, not a co-requirement for acknowledging the caller.

---

## 6. Where to Go Next — Document Map

This overview sets up six follow-on documents, each owning one piece of the extreme-scale story in depth:

| Doc | Title | What it answers |
|---|---|---|
| **20** | Outbox Pattern | How to guarantee the create write and its downstream event publish don't diverge (Section 5.3) |
| **03** | Elasticsearch vs. SQL Server | Whether/where a search-oriented store fits versus the relational primary discussed in Section 4 |
| **04** | Elasticsearch vs. MongoDB | Document-store vs. search-engine trade-off for the same data-placement question as doc 03 |
| **05** | Kafka Comparison | Whether Kafka specifically is the right event-transport choice for the async publishing need in Section 5, at this event volume |
| **06** | Output Caching / BFF / CDN | Read-path throughput (fetches) via output caching, a BFF layer, and CDN — the read-side counterpart to this write-path document |
| **07** | Redis Caching | The distributed-cache tier that replaces v1's per-instance `IMemoryCache` (Section 1.3), including cache invalidation on create |

**Read this document first, then whichever of 03-07 or 20 matches the question you're chasing.** Docs 20 and 05 together answer "how does the create path publish events safely and at scale" (Section 5). Docs 03 and 04 together answer "where does data live once it's off a single SQLite file" (Section 4). Docs 06 and 07 are primarily read-path documents but are listed here because cache invalidation is triggered *by* the create path (Section 1.3) — a write-path concern with a read-path payoff.

---

## 7. Summary of This Document's Positions

| # | Concern | Position taken here | Traces to |
|---|---|---|---|
| 1 | v1 bottlenecks at extreme scale | Single-writer SQLite file, no DB horizontal scale-out, per-instance cache staleness compounding with fleet size | ANFR-05, ANFR-06; `nfr-scalability.md` §§3-4 |
| 2 | Short-code generation | Pre-allocated ID blocks per instance + reversible obfuscation before base62 encoding; Snowflake-style local generation as the named fallback if the allocator itself contends | AF-04, ANFR-08; `fn-create.md` §6 |
| 3 | API layer | Stateless, horizontally scaled — unchanged in kind from v1, larger in degree; rate-limit counters must externalize | ANFR-01, ANFR-06; `nfr-scalability.md` §4 |
| 4 | Primary datastore | Server RDBMS with read replicas and/or sharding as the write-of-record; Elasticsearch/MongoDB placement deferred to docs 03/04 | `data-design-guidelines.md` §1 |
| 5 | Downstream work | Decouple via asynchronous event publication after the authoritative write; Outbox (20) and Kafka (05) own the mechanics | AF-01; sets up docs 20, 05 |
| 6 | Document map | 20/05 = safe async publishing; 03/04 = data placement; 06/07 = read-path payoff of write-path events | — |

**This document does not claim these are v1's requirements.** It is a scoped answer to "what would extreme scale require," per the review prompt's framing — v3 determines what, if anything, of this actually ships.
