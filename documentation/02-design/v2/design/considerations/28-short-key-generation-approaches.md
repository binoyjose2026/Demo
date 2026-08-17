# 28 — Short-Key Generation Approaches: Survey and Comparison

**Version:** v2 (scalability exploration)
**Status:** Draft — architectural consideration, not introducing a new decision
**Scope:** This document surveys the realistic ways a short code can be generated for a URL shortener at this system's scale, with concrete pros/cons and quantitative collision analysis for each. It does **not** introduce a new recommendation — `01-create-path-extreme-scalability.md` §2 already picked pre-allocated ID blocks + reversible obfuscation as the extreme-scale approach for this system, and `fn-create.md` §6 already picked random-generation-with-retry as v1's approach. This document places both of those decisions inside the full landscape of options so the choices can be evaluated against every realistic alternative, not just against each other.
**Traceability:** AF-04 (unique short code generation, collision handling), ANFR-08 (non-enumerability), `10-database-partitioning-sharding.md` (sharded/multi-instance write tier this comparison assumes).

---

## 0. The Scale and Constraints This Comparison Is Judged Against

Per `01-create-path-extreme-scalability.md` §0: 1M creates/day today, growing to 5M creates/day at a 5-year horizon, served by a horizontally-scaled, potentially-sharded fleet of stateless API instances (`10-database-partitioning-sharding.md`). Two requirements from the source documents are non-negotiable filters on every option below, not just tiebreakers:

- **AF-04** — the system must generate a *unique* short code and *handle collisions* when they occur (collision handling must be a real, designed-for code path, not an assumption-that-never-fires).
- **ANFR-08** — short codes must not be trivially sequential/enumerable. An option that produces `1z`, `20`, `21`, `22`, … in creation order fails this requirement outright, regardless of how well it scores elsewhere.

Every option is scored against: collision risk, enumerability/security, coordination overhead across many instances, resulting code length, and operational complexity. Section 8 is the summary table; Section 9 is the final recommendation.

---

## 1. Random Generation + Collision Check/Retry

Generate a random string over a fixed alphabet (v1: 7 characters, base62 — `fn-create.md` §6), check for a collision (`ExistsByCodeAsync` pre-check, unique-index violation as the authoritative fallback), retry on collision up to a bounded attempt count.

### Pros
- **Non-enumerable by construction** — codes carry no relationship to insertion order, satisfying ANFR-08 directly, with no separate obfuscation step needed.
- **Simple** — one random-string generator, one existence check, a small retry loop. No shared state, no allocator, no per-instance identity to manage.
- **No coordination needed between instances** — any instance can generate any candidate independently; the only shared resource is the uniqueness check against the database, which every option that persists to a shared store needs anyway.

### Cons
- **Collision probability grows as the keyspace fills** — quantified below.
- **Retry adds latency and complexity to the hot path** — every collision costs a database round-trip before the request can proceed, and the retry loop itself (bounded attempts, an exhaustion error path) is a piece of designed-for failure handling that other options simply don't need.
- **No ordering or insight from the code itself** — not a functional defect (nothing in AF-01–AF-10 requires ordering), but it does mean any operational need to reason about "roughly when was this created" from the code alone (debugging, ad hoc auditing) gets nothing for free the way a time-embedding scheme (Option 6) would.

### The birthday-paradox math, worked for this system's actual scale

v1's own math (`fn-create.md` §6) computed collision probability at 10M links: with 7-character base62 codes, keyspace size N = 62⁷ ≈ 3.5 × 10¹² possible codes, and birthday-bound approximation P(collision) ≈ k² / (2N) for k existing codes:

- At k = 10,000,000: P ≈ (10⁷)² / (2 × 3.5×10¹²) ≈ 10¹⁴ / 7×10¹² ≈ **1.4%** cumulative probability that *some* pair among 10M codes collides — but the *per-attempt* collision probability (the number that actually matters for retry-loop behavior) is k/N ≈ 10⁷ / 3.5×10¹² ≈ **0.00029%**, i.e., roughly 1 collision per 350,000 create attempts at that volume. (`fn-create.md` §6's "≈0.0014%" figure describes the same order of magnitude via a slightly different framing; both agree collisions are rare, not that they're zero.)

Now extend to this review's 5-year cumulative volume. Per `01-create-path-extreme-scalability.md` §2.1, at 5M creates/day sustained toward the end of a 5-year ramp from 1M/day, cumulative created links approach roughly **9 billion** (k ≈ 9 × 10⁹):

- **Per-attempt collision probability at k = 9×10⁹:** k/N = 9×10⁹ / 3.5×10¹² ≈ **0.26%** — roughly 1 in 390 create attempts hits an existing code and must retry. This is no longer negligible: at a peak of, say, 2,000 creates/sec, that's on the order of 5 retried requests per second, each paying an extra database round-trip.
- **Cumulative "has any collision occurred across all codes issued" probability** by this point is effectively 100% (it crosses 50% far earlier, around k ≈ √(N·ln2) ≈ 1.56×10⁶ — i.e., collisions start happening routinely well before 10M total codes, let alone 9 billion).

**Verdict on the math:** at v1's assumed 10M-link scale, retry rate is genuinely negligible (~1 in 350,000 attempts) — the "expected to never realistically trigger" framing in `fn-create.md` §6 is accurate for v1's *exhaustion* case (5 consecutive collisions), even if individual single collisions are not literally impossible. At the 5-year, 9-billion-cumulative-link horizon this v2 review is scoped to, the picture changes materially: **per-attempt collision probability rises to roughly 1 in 390**, which is a real, measurable tax on write-path latency and database load under concurrent writers (`01-create-path-extreme-scalability.md` §2.1's point about index contention compounding with retry). The fix at that point is not "the math was wrong" — it's "lengthen the code" (8 characters pushes N to 62⁸ ≈ 2.18×10¹⁴, dropping per-attempt probability back to ≈0.004%) or abandon retry-based collision avoidance altogether in favor of a collision-free-by-construction scheme (Option 7). This is precisely the argument `01-create-path-extreme-scalability.md` §2.1 makes for why v1's approach doesn't survive as-is at extreme scale — not because the collision math was wrong for v1, but because the same formula evaluated at 1000x the volume crosses from "negligible" to "a real contention source."

---

## 2. Auto-Increment Database ID + Base62 Encoding

Use a sequential, database-generated numeric ID (e.g., an `IDENTITY`/`SERIAL` column) and encode it directly to base62 with no further transformation.

### Pros
- **Trivially unique** — the database's sequence guarantees no two rows ever get the same ID; there is no collision to check for or handle, full stop.
- **Short** — a 64-bit sequential ID encodes to at most 11 base62 characters, and for any realistic multi-year volume (millions to low billions) encodes to 6-7 characters, competitive with or shorter than a random scheme's fixed length.
- **Simple** — no generator abstraction, no retry loop, no `IShortCodeGenerator` interface at all; the code is a pure function of a value the database already produces for free.
- **No collision handling needed at all** — AF-04's "handle collisions" requirement becomes vacuous under this scheme, which is exactly why v1 rejected it (see below).

### Cons
- **Sequential/enumerable — directly conflicts with ANFR-08.** `fn-create.md` §6 names this exact failure mode: encoding `Id=1,2,3,…` as base62 produces `b`, `c`, `d`, … (or with a slightly different alphabet mapping, `1z`, `20`, `21`, …) — anyone can increment the code in the URL bar and walk the entire link table, scraping every mapping in the system, including ones the owner never intended to be discoverable. This is not a probabilistic weakness to be tolerated at low odds (unlike Option 1's collision math) — it is a deterministic, 100%-certain enumeration path the moment a single valid code is known.
- **Single point of coordination that doesn't scale across a sharded/multi-instance write tier.** A single auto-increment sequence is, definitionally, one shared piece of mutable state every writer must serialize against for every single insert — the opposite of the horizontally-scaled, potentially-sharded fleet `10-database-partitioning-sharding.md` designs for. Making it scale requires either a single hot sequence object that becomes the write-path bottleneck at thousands of creates/sec, or a per-shard-offset scheme (e.g., shard 1 issues IDs ≡ 1 mod N, shard 2 issues IDs ≡ 2 mod N) that reintroduces exactly the kind of custom coordination logic a "just use auto-increment" approach was supposed to avoid — and even then, the *values* are still sequential-within-shard, so the enumerability problem in the previous bullet does not go away.

**Relationship to Option 7:** Option 7 (the system's actual recommendation) is, structurally, "sequential ID + base62" plus a reversible obfuscation step specifically inserted to neutralize this option's enumerability failure. Every con listed here except enumerability carries over as a pro for Option 7; enumerability is the one axis Option 7 fixes.

---

## 3. Hash-Based (e.g., MD5/SHA-256 of the Long URL, Truncated)

Compute a cryptographic hash of the submitted long URL, truncate it to the desired short-code length (e.g., first 7 base62-encoded characters of a SHA-256 digest).

### Pros
- **Deterministic** — the same long URL always produces the same short code. This gives free dedup: "has this URL already been shortened?" becomes a hash lookup instead of a full-table scan or a separate uniqueness index on `OriginalUrl`, and repeat submissions of the same URL don't consume additional code space.
- **No DB round-trip needed to check "has this URL been shortened before"** — the hash *is* the answer; a client (or the service itself) can compute it locally before ever touching the database, and a cache-hit on a previously-seen hash short-circuits the entire create path.

### Cons
- **Truncated hashes collide far more often than the full hash length suggests — birthday paradox again, but worse.** Truncating a 256-bit SHA-256 digest to a 7-character base62 code discards essentially all of the hash's collision resistance: the *output* keyspace is 62⁷ ≈ 3.5×10¹² regardless of how large the underlying hash was, because only the truncated bits matter for output collisions. The full 256 bits protect against finding two *inputs* that hash identically (a cryptographic property irrelevant here); they do nothing to protect against two *different* long URLs whose *truncated* hashes happen to match, which is the only collision that matters for this use case.
  - Applying the same birthday-bound math as Option 1, at this system's 5-year cumulative volume (k ≈ 9×10⁹ distinct long URLs, N ≈ 3.5×10¹²): per-attempt collision probability ≈ k/N ≈ **0.26%** — identical order of magnitude to Option 1's random-generation math, because a truncated hash's output distribution is (by design, for a good hash function) indistinguishable from uniform random over the truncated space. Truncating a cryptographic hash for a short code buys none of the "unbreakable 256-bit security" intuition the full hash length suggests; the effective collision-resistance is exactly the truncated output space's size, nothing more.
- **Doesn't naturally support the same long URL getting different short codes for different users or custom needs.** Two different users shortening `https://example.com/report` would deterministically get the *same* code under pure hashing — which breaks per-user link ownership (`fn-create.md` §4's `CreatedBy`/`OwnerDepartmentId` model assumes each `ShortUrl` row is a distinct, independently-owned mapping) and makes independent expiration, deactivation, or analytics per "logical" shortening impossible, since there is only one underlying row two different creation requests would both resolve to.
- **Still needs collision handling** — the "no collision" pro only holds for *identical* input URLs; two *different* URLs truncating to the same code is a real, non-eliminable collision case (previous bullet), so a retry/salt strategy is still required, undoing much of the simplicity this option was chosen for in the first place. A common mitigation — append a salt/counter and rehash on collision — converges this option back toward Option 1's retry-loop shape, but without Option 1's clean non-enumerability guarantee, since the *first* attempt for any given URL is still deterministic and thus guessable by anyone who knows (or can guess) the original URL.

---

## 4. Hashids or Similar Reversible-Encoding Libraries

Feed a sequential/auto-increment ID through a library such as Hashids, which applies a keyed, reversible transformation (bit shuffling, a custom alphabet, optional salt) to produce a string that *looks* non-sequential without a database lookup table.

### Pros
- **Turns a sequential ID into a non-obvious-looking string without a separate lookup table** — no extra column, no reverse-mapping table to maintain; decoding the ID back out of the short code is a pure function of the code and the configured salt/alphabet.
- **Some obfuscation** — casual inspection of consecutive codes (`id=1000`, `id=1001`, …) does not visually reveal the sequential relationship the way naive base62-of-auto-increment (Option 2) does.

### Cons
- **Not cryptographically secure obfuscation.** Hashids is explicitly documented by its own authors as *not* a security mechanism — it is a reversible encoding, not encryption. Given enough sample codes (a modest number, achievable by simply creating several short URLs in a row and observing the outputs, or by using publicly documented Hashids algorithm details), the sequential structure underneath can often be inferred or the transformation reverse-engineered outright, especially since the algorithm itself is public and only a salt value is secret — and salts are frequently left as defaults or checked into source control in real-world deployments. This falls meaningfully short of ANFR-08's non-enumerability bar if "non-enumerable" is read as "resistant to a motivated attacker with a handful of samples," not merely "not obviously sequential at a glance."
- **Same fundamental sequential-ID coordination problem as Option 2 underneath.** Hashids transforms the *output representation* of an ID; it does nothing about *how the ID itself is generated*. The single-auto-increment-sequence bottleneck and the sharded/multi-instance coordination problem described in Option 2 apply identically here — Hashids sits downstream of that problem, not as a solution to it.
- **Adds a dependency** — a third-party library (with its own maintenance, security-patch, and .NET-ecosystem-currency considerations) is now on the critical path for every create and every redirect, for a benefit (visual obfuscation) that Option 7's purpose-built bit-mixing step achieves more rigorously without an external dependency.

---

## 5. GUID/UUID-Based

Generate a standard 128-bit GUID/UUID (e.g., `Guid.NewGuid()`) per short URL, either used directly or encoded to a shorter representation.

### Pros
- **Virtually zero collision probability** — a v4 UUID's 122 random bits give a collision probability so small (birthday bound at even 10¹⁸ generated UUIDs is still astronomically below any practical concern) that collision handling as a designed-for code path becomes unnecessary, similar in spirit to Option 2's "trivially unique" property but without that option's enumerability cost, since UUIDs (v4 specifically) are randomly generated, not sequential.
- **No coordination needed across any number of distributed instances** — `Guid.NewGuid()` requires no shared counter, no allocator round-trip, and no per-instance identity; any number of instances can generate GUIDs independently with the same collision-free guarantee, which is the strongest coordination-free property of any option surveyed here.

### Cons
- **Far too long for a "short" URL if used directly.** A standard GUID string representation is 36 characters including hyphens (32 hex digits + 4 hyphens), or 32 characters without them — an order of magnitude longer than the 7-character codes every other option targets, and defeats the basic value proposition of a URL *shortener* (a "short" URL that is itself 32+ characters is a poor trade against the original long URL in many realistic cases).
- **Truncating a GUID to a usable short length reintroduces collision risk and gives up the exact property that made GUIDs attractive.** A GUID's near-zero collision probability is a function of its full 122 bits of randomness; truncating it to, say, the first 7 base62 characters (≈41-42 bits) collapses the effective keyspace down to roughly the same order of magnitude as Option 1's random-generation scheme (62⁷ ≈ 3.5×10¹²) — at which point the same birthday-paradox math from Section 1 applies, and the system has paid the operational cost of using GUIDs (a heavier 128-bit value, GUID-specific encoding/decoding logic) without actually retaining GUID-level collision resistance. In effect, a truncated GUID *is* Option 1's random-generation scheme wearing a different primitive, minus the benefit of using a well-understood, purpose-built random string generator directly.

---

## 6. Distributed ID Generation (Snowflake-Style: Timestamp + Machine ID + Sequence, Base62-Encoded)

Pack a 64-bit ID from a timestamp component (milliseconds since a custom epoch), a machine/worker-ID component (identifying which instance generated it), and a per-millisecond sequence counter — generated entirely locally by each instance, no shared allocator round-trip. This is the "alternative considered and not recommended as the primary mechanism" already named in `01-create-path-extreme-scalability.md` §2.2; it is covered here in full as one option among the complete survey.

### Pros
- **Coordination-free across many instances on the hot path** — once a machine ID is assigned, every ID generation is a purely local operation (read the clock, read/increment an in-process sequence counter); no network round-trip to any shared allocator is needed per request or per block, which is a stronger coordination-free property on the *generation* path than even Option 7's per-block allocator touch.
- **Roughly time-sortable** — IDs generated later have numerically larger values (timestamp is the high-order bits), which can be operationally convenient (e.g., approximate chronological ordering without a separate `CreatedAtUtc` sort) even though nothing in AF-01–AF-10 requires it.
- **No central bottleneck** — unlike Option 2's single auto-increment sequence, there is no single shared counter any instance count can contend on; this scales writer count linearly with instance count.

### Cons
- **Longer than a pure random/counter code once encoded.** A 64-bit Snowflake-style ID needs to reserve bits for all three components simultaneously (timestamp + machine ID + sequence) to guarantee uniqueness, whereas a pre-allocated dense counter (Option 7) needs only as many bits as the actual cumulative ID count requires — for the same multi-year horizon, the Snowflake ID's full 64-bit value base62-encodes to up to 11 characters versus Option 7's realistic 6-7, a real (if modest) cost against "short" in "short URL," as `01-create-path-extreme-scalability.md` §2.2 already notes.
- **Reveals creation-time-ish ordering unless deliberately obfuscated — a direct ANFR-08 tension.** The same time-sortability listed as a pro is also the central con: two codes generated a few seconds apart will, before any obfuscation, be numerically close, meaning an attacker who obtains one valid code has a rough map of where in the ID space other codes generated around the same time live — a softer version of Option 2's total-enumerability problem, but still in tension with "not trivially sequential/enumerable" unless the same kind of bit-mixing step Option 7 applies is layered on top here too (at which point this option converges toward Option 7's shape, minus the block-allocator piece).
- **Requires each instance to have a stable, unique machine/worker ID — an operational dependency this system doesn't otherwise need.** Two instances accidentally issued the same machine ID (a mis-configured deploy, a scaling event that doesn't correctly hand out fresh IDs, a restarted instance re-registering with a stale ID) silently reintroduces collision risk that the whole scheme was designed to eliminate. Solving this reliably typically means leaning on a coordination service (e.g., a distributed lock/registry for machine-ID leasing) — exactly the kind of additional infrastructure dependency `01-create-path-extreme-scalability.md` §2.2 flags as a reason to prefer the simpler single-counter allocator for this system.

---

## 7. Pre-Allocated ID Block/Range per Instance (Recommended — Already Chosen)

Each stateless API instance requests a contiguous block of integer IDs (e.g., 10,000) from a centralized, lightweight block allocator, then assigns IDs to incoming creates locally, in-process, with no per-request network round-trip and no collision check. Each raw ID passes through a reversible bit-mixing/permutation step before base62 encoding, so consecutive raw IDs do not produce consecutive-looking codes.

This is the approach `01-create-path-extreme-scalability.md` §2.2 already selected for this system's extreme-scale create path. **Full mechanism detail (block size rationale, the Feistel-network/XOR-rotate bit-mixing options, block-exhaustion/restart behavior) is not re-derived here — see that document.** This section places it in the comparison and summarizes why it wins.

### Pros (summary — see `01-create-path-extreme-scalability.md` §2.2–2.3 for the full argument)
- **Coordination-light** — each instance talks to the central allocator only once per 10,000 creates (a block refill), not once per request, a roughly four-order-of-magnitude reduction in contention against the shared counter compared to Option 1's per-request uniqueness check or Option 2's per-request sequence increment.
- **Collision-free by construction, not by statistical improbability** — blocks are disjoint by allocation, so there is no retry loop, no collision-probability math to worry about at any cumulative volume (unlike Option 1, whose math genuinely degrades at this system's 5-year scale per Section 1 above), and no `MaxGenerationAttempts` exhaustion path needed for system-generated codes at all.
- **The bit-mixing step directly solves Options 2's and 6's shared weakness** — enumerability. A dense, sequential counter is the *easiest possible* input to obfuscate well (unlike Option 6's timestamp+machine+sequence composite, which is harder to fully decorrelate from time without losing the time-sortability that was its own selling point) — mixing a single dense integer is simpler and produces a shorter final code than mixing Snowflake's wider composite value.

### Cons (named honestly, not just restated from the source document)
- **Still has a central point of coordination**, just an infrequent one — the block allocator's single "next available block" counter remains a single logical write per instance-per-10,000-creates. At a large enough instance count and creation rate, this could itself become a bottleneck (explicitly acknowledged in `01-create-path-extreme-scalability.md` §2.2, which names Snowflake-style local generation as the escalation if that happens) — this is a real, if distant, ceiling, not a coordination-free guarantee on the level of Option 5 (GUIDs) or Option 6 (Snowflake).
- **Non-enumerability depends on the bit-mixing secret staying secret** — unlike Option 1's non-enumerability, which is inherent to random generation and needs no secret to protect, this scheme's non-enumerability is only as strong as the obfuscation key/algorithm's secrecy (the same category of risk Option 4's Hashids has, though a purpose-built Feistel-based scheme with a properly managed secret is materially stronger than Hashids' default posture).
- **Gaps and abandoned block remainders are a minor operational quirk** — on instance restart/crash before a block is exhausted, the unused remainder is abandoned (harmless per AF-01/AF-04, since nothing requires dense codes, but it does mean the raw ID space is consumed somewhat faster than "one ID per code ever issued," a fact worth knowing when reasoning about how many years of ID space a given integer width buys).

---

## 8. Comparison Table

| # | Approach | Collision risk | Enumerability / security | Coordination overhead | Code length | Operational complexity |
|---|---|---|---|---|---|---|
| 1 | Random + collision retry | Low at v1 scale (~1 in 350K/attempt at 10M links); **real at 5-yr scale** (~1 in 390/attempt at ~9B links) | Strong — non-enumerable by construction | None between instances; per-request DB check only | Fixed, short (7 chars @ 62⁷) | Low |
| 2 | Auto-increment + base62 | None (DB-guaranteed) | **Fails ANFR-08** — 100% enumerable | High — single shared sequence is a write-path bottleneck at scale; per-shard-offset workaround adds complexity | Shortest of all options (grows with row count) | Low in isolation, but the ANFR-08 fix and the sharding fix both add complexity back |
| 3 | Hash of long URL, truncated | Same order as Option 1 at scale (~0.26% per-attempt at 5-yr volume) — truncation discards the full hash's collision resistance | Weak — deterministic per input URL, guessable if URL is known/guessable | None (stateless computation) | Fixed, short (truncated to match) | Low, but dedup benefit is undercut by needing retry/salt handling anyway |
| 4 | Hashids / reversible encoding | None (wraps a sequential ID) | **Weak in practice** — reversible, not cryptographic; crackable with samples | Same as Option 2 underneath | Short, similar to Option 2 | Adds a third-party dependency |
| 5 | GUID/UUID | Near-zero if used at full length; **reintroduced if truncated** | Strong at full length (random, unguessable) | None — fully local generation | Too long at full length (32-36 chars); truncation collapses to Option-1-equivalent length and risk | Low, but length problem forces a design compromise |
| 6 | Snowflake-style distributed ID | None (composite guarantees uniqueness given valid machine IDs) | Moderate — time-sortable by default, needs added obfuscation to meet ANFR-08 | Low per-request (fully local); moderate one-time cost to assign/manage machine IDs | Longer than a dense counter (up to 11 chars) | Moderate — machine-ID assignment is an added operational dependency |
| 7 | **Pre-allocated ID blocks + bit-mixing (recommended)** | **None** (collision-free by construction) | Strong, contingent on keeping the mixing secret protected | **Low** — one allocator round-trip per 10,000 creates | Short (6-7 chars, dense counter) | Low-moderate — one allocator component, one mixing-key management concern |

---

## 9. Final Recommendation

**The pre-allocated ID block/range approach (Option 7), as already selected in `01-create-path-extreme-scalability.md` §2.2, remains the right choice for this system** and is confirmed, not revised, by this broader survey. Against each alternative, specifically for this system's constraints — extreme scale (5M creates/day, ~9B cumulative links at 5 years), a horizontally-scaled and potentially-sharded multi-instance write tier (`10-database-partitioning-sharding.md`), a hard non-enumerability requirement (ANFR-08), and a project-wide preference for low operational complexity already established by v1's SQLite-first philosophy (`data-design-guidelines.md` §1) — it wins as follows: it beats **Option 1** because random-retry's collision math, negligible at v1 scale, becomes a measurable per-request tax (~1 in 390 attempts) at this review's 5-year volume, while pre-allocated blocks stay collision-free at any volume; it beats **Option 2** outright because raw auto-increment fails ANFR-08 unconditionally, a disqualifying defect no amount of scale-tuning fixes; it beats **Option 3** because truncated-hash collision risk tracks Option 1's degraded math while additionally breaking the "same URL, different owners" model this system's per-user ownership requires; it beats **Option 4** because Hashids inherits Option 2's coordination problem while offering only cosmetic, not cryptographic, obfuscation, plus an unnecessary external dependency; it beats **Option 5** because GUIDs are either too long to be a "short" URL or, once truncated to a usable length, degrade to Option 1's own risk profile without Option 1's simplicity; and it beats **Option 6** because Snowflake's machine-ID-assignment requirement is exactly the kind of added operational dependency this project's SQLite-first, complexity-averse posture has consistently avoided elsewhere, for a coordination benefit (zero allocator round-trips at all, versus one per 10,000 creates) that is not yet needed at this system's projected instance count. No alternative surveyed here beats pre-allocated blocks on more than one axis simultaneously without losing ground on another; Option 7 is the only approach that is simultaneously collision-free by construction, ANFR-08-compliant, low-coordination at this system's scale, and free of new external dependencies or operational identity-management requirements.
