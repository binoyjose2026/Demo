# Idempotency-Key Support for Create Short URL

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/agent-prompt.md`.
**Builds on (does not replace):** `v1/design/fn-create.md` (the create-link flow this document adds idempotency to — validation pipeline, short-code generation, persistence sequence, DTOs). This document does not restate any of that flow; it adds one new guard clause in front of it.
**Related v2 documents (by filename, not duplicated here):** `01-create-path-extreme-scalability.md` (the multi-instance write path and the 1M-5M creates/day target this document assumes), `09-resilience-patterns.md` (client-side retry-with-backoff-and-jitter — the pattern that makes duplicate requests routine at this scale; not restated here, only its consequence is), `07-redis-caching-and-invalidation.md` (the existing Redis tier this document's storage recommendation reuses — key-naming and TTL-as-backstop conventions carried forward, not re-derived).
**Cross-references `UrlShortener/engineering-standards/guidelines/data-design-guidelines.md`** for the `Id`/`RowVersion`/audit-field conventions and explains where the idempotency record does and does not follow them (Section 3).

---

## 1. The Problem, Concretely

`fn-create.md` §2 defines `POST /api/short-urls` as a single synchronous request: validate → resolve short code → persist → `201 Created`. That flow says nothing about what happens when the *client* doesn't reliably observe the outcome of that request — which, at this project's v2 target scale, is no longer an edge case.

**Why this becomes a real problem at 1M-5M creates/day and not before:**

- **`09-resilience-patterns.md` establishes client-side retries with exponential backoff and jitter** as the standard resilience pattern this v2 review adopts for calls against the API. That pattern exists precisely because, at scale, transient failures (a load-balancer draining an instance mid-deploy, a brief network partition, a slow GC pause pushing one request past the client's timeout) are a *statistical certainty*, not a hypothetical. A client that times out waiting for a response and then retries is doing exactly what it is designed to do.
- **The failure mode that matters here is not "the request failed" — it's "the request succeeded, but the client doesn't know that."** `fn-create.md` §11's persistence sequence commits the `ShortUrl` row and returns `201 Created` in that order. If the response is lost after the commit (client-side timeout fires a few hundred milliseconds before the response arrives, a proxy drops the connection after the upstream already wrote the row, the client process itself is killed mid-response-read), the server has durably done the work but the caller has no way to distinguish that from "the request never arrived at all."
- **A retry of a "maybe it worked, maybe it didn't" request, with no idempotency protection, re-runs the entire create flow from scratch** — a *second* short-code is resolved (`fn-create.md` §6/§7), a *second* `ShortUrl` row is persisted, and the caller now owns two live short links for what was, from their point of view, one logical "shorten this URL" intent. There is no natural dedup here: two different system-generated codes (or a `409 Conflict` on a second identical custom-alias attempt, which at least fails loudly) both point at the same `OriginalUrl`, silently.
- **At volume, this compounds from "rare annoyance" to "measurable data-quality problem."** Even a conservative 0.1-0.5% retry rate (realistic for a fleet running rolling deploys and autoscaling events under `01-create-path-extreme-scalability.md`'s horizontally-scaled topology) against 1M-5M creates/day is 1,000-25,000 duplicate-intent creates *per day* with no protection in place — polluting analytics, wasting short-code space, and confusing any caller who expects "create" to be a stable, replayable operation when retried per the resilience pattern they were told to use.

The fix is not "make retries less likely" (impossible) or "make the create endpoint faster" (helps, doesn't eliminate the race) — it's making a retried request **safe to send again**: idempotency-key support.

---

## 2. The Mechanism

### 2.1 Client contract

The client generates one GUID per **logical** create attempt (not per HTTP attempt) and sends it as a header:

```
POST /api/short-urls
Idempotency-Key: 5f8c1e2a-9b3d-4e7f-9a1c-2d6b7e4f0a11
Content-Type: application/json

{ "originalUrl": "https://example.com/some/long/path" }
```

- **Generated once, reused across every retry of the same attempt.** If the client's resilience wrapper (`09-resilience-patterns.md`) retries a request three times with backoff, all three carry the *same* `Idempotency-Key` — that's what makes them recognizable as "the same logical thing" rather than three independent creates.
- **Header is optional at the protocol level, but the design's whole point is defeated if a retrying client omits it.** Rather than making it a hard `400` requirement (which forces every caller, including simple non-retrying scripts/tests, to generate a GUID they don't need), this document recommends: **honor it when present, apply no dedup protection when absent.** A client that opts into the resilience pattern's retry behavior is expected, by the same policy, to opt into sending this header — that pairing should be enforced at the client-library level (the same wrapper that adds retry-with-backoff adds the header), not by rejecting headerless requests server-side.

### 2.2 Server-side detection and short-circuit

On every create request that carries the header, before `fn-create.md`'s validation pipeline runs:

1. Compute `RequestBodyHash` = SHA-256 of the canonicalized request body (stable key ordering, no incidental whitespace differences — otherwise two byte-for-byte-different-but-semantically-identical JSON payloads would falsely appear to be a key-reuse conflict).
2. Look up `(CreatorId, IdempotencyKey)` in the idempotency store (Section 3).
3. **No record found** → this is the first attempt. Reserve the key (see the in-flight race in Section 6), let the request proceed through the normal create flow, and on a successful response, store the result before returning it to the caller.
4. **Record found, `RequestBodyHash` matches** → this is a retry of the same logical attempt. **Do not re-run the create flow.** Return the stored response (same status code, same body) verbatim. No new `ShortUrl` row is created, no short code is consumed a second time.
5. **Record found, `RequestBodyHash` differs** → the caller reused an `Idempotency-Key` for a *different* request body. Reject with `409 Conflict`.

### 2.3 Why `409 Conflict`, not `422 Unprocessable Entity`, on a body mismatch

`fn-create.md` §9 already reserves `422` for a specific, distinct meaning in this codebase: "syntactically valid input, rejected on policy grounds" (the malicious/phishing domain check). Reusing `422` for key-reuse-with-different-body would overload that meaning with an unrelated failure class. `409 Conflict` is both the more precise HTTP semantic (RFC 9110: the request conflicts with the current state of the target resource — here, the target resource is "whatever is identified by this idempotency key," and its current state doesn't match what was just sent) and **consistent with an existing project convention**: `fn-create.md` §7 already uses `409` for "this identifier is already taken by something else" (a custom alias collision). An idempotency-key body mismatch is the same shape of conflict — same identifier, different underlying content — so `409` is the decision made here, for consistency with a pattern this codebase already established rather than inventing a new one.

### 2.4 DTO / filter sketch

```csharp
namespace UrlShortener.Application.Common;

/// <summary>
/// Idempotency record for a single (CreatorId, IdempotencyKey) pair. Write-once —
/// created on first successful attempt, read on every retry, never updated in place.
/// Storage is Redis (Section 3), but the shape deliberately mirrors the audit-style
/// fields in data-design-guidelines.md §3 where they still apply to an ephemeral,
/// TTL-bound record (see Section 3's compatibility note).
/// </summary>
public sealed class IdempotencyRecord
{
    /// <summary>SHA-256 hash of the canonicalized request body — detects key reuse with a different payload.</summary>
    public string RequestBodyHash { get; set; } = string.Empty;

    /// <summary>HTTP status code of the original response (e.g. 201), replayed verbatim on a duplicate.</summary>
    public int ResponseStatusCode { get; set; }

    /// <summary>Serialized original response body (the ShortUrlResponse JSON), replayed verbatim on a duplicate.</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>UTC instant the original attempt was first recorded — mirrors CreatedAtUtc's role/naming (data-design-guidelines.md §3), even though this record lives in Redis, not a relational table.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
```

```csharp
namespace UrlShortener.Api.Filters;

/// <summary>
/// Detects and short-circuits duplicate create requests carrying an Idempotency-Key header.
/// See Section 5 for why this is an action filter, not middleware.
/// </summary>
public sealed class IdempotencyKeyFilter : IAsyncActionFilter
{
    private readonly IIdempotencyStore _store;       // Infrastructure: Redis-backed, Section 3
    private readonly ICurrentUserContext _currentUser; // Application seam, fn-create.md §4

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("Idempotency-Key", out var raw)
            || !Guid.TryParse(raw, out var idempotencyKey))
        {
            await next(); // no header (or malformed) → no idempotency protection, per Section 2.1
            return;
        }

        var storeKey = $"idem:{_currentUser.UserId}:{idempotencyKey}";
        var requestHash = IdempotencyHasher.Hash(context.ActionArguments["request"]);

        var existing = await _store.TryGetAsync(storeKey, context.HttpContext.RequestAborted);
        if (existing is not null)
        {
            context.Result = existing.RequestBodyHash == requestHash
                ? new ObjectResult(existing.ResponseBody) { StatusCode = existing.ResponseStatusCode } // §2.2 step 4 — replay
                : new ConflictObjectResult(ProblemDetailsFactory.KeyReuseConflict());                  // §2.2 step 5 — 409
            return;
        }

        if (!await _store.TryReserveAsync(storeKey, context.HttpContext.RequestAborted)) // §6 in-flight race guard
        {
            context.Result = new ConflictObjectResult(ProblemDetailsFactory.RequestInFlight()); // 409 — a concurrent retry is already being processed
            return;
        }

        var executed = await next();

        if (executed.Result is ObjectResult { StatusCode: >= 200 and < 300 } ok)
        {
            await _store.SaveAsync(storeKey, requestHash, ok, IdempotencyOptions.Ttl, context.HttpContext.RequestAborted);
        }
        else
        {
            await _store.ReleaseReservationAsync(storeKey, context.HttpContext.RequestAborted); // don't cache failures — a failed create is safe to genuinely retry
        }
    }
}
```

---

## 3. Storage Choice: Redis, TTL-Bound

**Recommendation: the same managed Redis tier proposed in `07-redis-caching-and-invalidation.md`** (a separate logical keyspace within it — e.g. `idem:{creatorId}:{idempotencyKey}` — not a separate cluster), rather than a table in the primary relational store.

Justification:

- **This is dedup-window data, not durable business data.** Once the TTL (Section 4) lapses, a stale idempotency record has zero value — there is nothing to audit, report on, or recover; its entire job is "prevent a duplicate for a bounded window after the fact." That is precisely the profile `07-redis-caching-and-invalidation.md` already argues for TTL-bound, evictable Redis data over a relational table: no query patterns beyond point lookups by key, no joins, no reporting need.
- **Write volume matches create volume, not a subset of it.** Unlike the read-path cache (`07-redis-caching-and-invalidation.md`, which only needs to hold the *hot* subset of reads), an idempotency record must be written on **every** successful create that carries the header — there's no "cold" idempotency key to skip caching. Putting that on the relational primary would add a write to the already-write-constrained datastore (`01-create-path-extreme-scalability.md` §1.1/§4) for data that has no long-term value there. Redis absorbs it without competing for the primary's write capacity.
- **Atomic check-and-reserve is a native Redis primitive.** The in-flight race (Section 6) needs an atomic "set this key only if it doesn't already exist" operation (`SET key value NX`) to correctly handle two near-simultaneous retries of the same key — this is a first-class Redis operation, not something that needs to be built on top of relational row-locking semantics.

### 3.1 Compatibility with `data-design-guidelines.md`'s `Id`/`RowVersion` conventions

`data-design-guidelines.md` fixes `Id`/`RowVersion`/audit-field conventions **for tables** — rows in the SQLite-today/server-RDBMS-at-v2-scale relational store. An idempotency record is deliberately **not** a table row, so most of that convention doesn't apply, and this is called out explicitly rather than silently deviated from:

| Convention | Applies here? | Why / why not |
|---|---|---|
| `Id` surrogate key | No | The natural key *is* `(CreatorId, IdempotencyKey)` — a Redis key string, not an auto-incrementing integer. Introducing a surrogate `Id` would add a lookup indirection Redis's key-value model doesn't need. |
| `CreatedAtUtc` audit field | Yes, in spirit | Carried forward as a plain field on `IdempotencyRecord` (Section 2.4) — useful for diagnosing "how old is this record" without needing Redis's own key-metadata inspection. |
| `RowVersion` (optimistic concurrency / delta sync) | No | The record is **write-once**: created on first success, read on every replay, never updated in place, and never subject to concurrent-writer races on the *same* already-written record (Section 6's reservation step is what prevents two writers from racing to create it in the first place). There is nothing to detect a conflicting update *against*, so a concurrency token has no job to do here. |
| Soft delete (`IsDeleted`/`DeletedAtUtc`) | No | TTL expiry *is* the deletion mechanism (Section 4) — a hard, automatic Redis expiry, not an application-level soft-delete flag. Preserving a deleted idempotency record for audit trail (the reason `data-design-guidelines.md` §5 gives for soft delete on real tables) has no value here; the record's only purpose expires with its TTL. |

**If a future requirement ever needs idempotency records to be durable/queryable** (e.g., a compliance need to audit "was this create ever retried"), that would be a deliberate, separate decision to *also* persist a relational record following the full `AuditableEntity` shape — not a reason to change this document's primary recommendation, which is scoped to the dedup mechanism itself.

*(Cross-reference note: `07-redis-caching-and-invalidation.md` already exists at the time of writing, so the recommendation above is checked against its actual topology/HA guidance in Section 4 there, rather than being a forward-looking placeholder.)*

---

## 4. TTL Choice: 24 Hours

**Recommendation: 24 hours from the original successful response.**

- **Long enough to cover realistic retry windows, including ones beyond the client library's own backoff schedule.** `09-resilience-patterns.md`'s exponential-backoff-with-jitter retries are bounded to a handful of attempts over seconds-to-low-minutes — comfortably covered by any TTL measured in hours. The reason to go to 24 hours rather than, say, 15 minutes is the *outer* retry loop this project's clients realistically have: a batch/bulk-import caller whose job fails partway and re-runs on its next scheduled window, or a mobile/offline client that queues a create locally and re-sends once connectivity returns hours later. Both are legitimate "this is still the same logical attempt" retries that a short TTL would fail to protect.
- **Short enough not to bloat storage.** At the 5-year target of 5M creates/day (`01-create-path-extreme-scalability.md` §0), and assuming every create carries the header (worst case — the header is optional, so real volume is likely lower), a 24-hour TTL means at most ~5M idempotency records resident at once in steady state. Each record is small — key (~50 bytes) + hash (64 bytes) + a compact serialized response body (~150-250 bytes) + Redis's own per-key overhead — call it ~350-400 bytes fully loaded. **5M × ~400 bytes ≈ 2 GB** at peak, a small fraction of the caching tier's own memory budget (`07-redis-caching-and-invalidation.md` §3.1 illustrates a 4 GB budget for the *read* cache alone) and easily accommodated as a second keyspace on the same managed Redis tier.
- **Why not longer (e.g., 7 or 30 days, "to be safe"):** the storage math above scales roughly linearly with TTL — a 30-day window would carry ~30x the resident record count (tens of GB), turning a negligible cost into a real capacity-planning line item, for a marginal benefit (protecting against retries so delayed they're arguably no longer "the same attempt" in any meaningful sense; a caller re-submitting a create a month later should reasonably be treated as a new attempt). 24 hours is the standard figure used by comparable idempotency-key designs in production APIs (Stripe, for one, documents the same 24-hour window) for exactly this trade-off, and this document adopts it for the same reasoning rather than inventing a different number without evidence.
- Expiry is enforced natively by Redis's key TTL (`EXPIRE`/`SET ... EX`), consistent with `07-redis-caching-and-invalidation.md`'s existing pattern of using Redis TTL as the backstop mechanism rather than an application-level sweep job.

---

## 5. Where This Fits in the Request Pipeline

**This is an MVC action filter, not middleware** — a concrete implementation of the **Action filter** placeholder already fixed in `engineering-standards/guidelines/design-guidelines.md` §5 (the same category `ValidateModelStateFilter` is the existing placeholder example for).

Why an action filter and not middleware:

- **It needs the model-bound, strongly-typed request object, not the raw request stream.** Computing `RequestBodyHash` (Section 2.2) against a canonicalized, deserialized `CreateShortUrlRequest` guarantees two semantically-identical requests hash identically regardless of incidental JSON formatting (key order, whitespace) the client happens to send. Middleware runs before MVC model binding, so it would have to either re-implement binding/canonicalization itself or hash the raw byte stream (fragile — byte-identical-required is a much weaker, more surprising guarantee for callers than semantically-identical). Action filters run *after* model binding, so `context.ActionArguments` already hands over the bound DTO for free.
- **It's endpoint-specific, not global pipeline behavior.** Idempotency-key handling only applies to the create action (and, in principle, any other future non-idempotent-by-default write action) — not to every request the way the global exception-handling or correlation-ID middleware in `design-guidelines.md` §4 does. Filters are the mechanism this project already uses for exactly that granularity (applied per-controller/action, `design-guidelines.md` §5), whereas middleware in this project's fixed pipeline shape is deliberately global.
- **It needs to short-circuit *and* observe the result**, both squarely action-filter jobs: on a cache hit, it must set `context.Result` and skip the action entirely (the same short-circuit mechanism `ValidateModelStateFilter` already uses); on a cache miss, it must run `next()` and then inspect the produced result to decide whether to persist it (Section 2.4's `OnActionExecutionAsync` pattern, using `IAsyncActionFilter` rather than the synchronous `IActionFilter` `ValidateModelStateFilter` uses, specifically because it needs to await both the store lookup/reservation *and* the downstream action). This dual role means it structurally overlaps a little with what `design-guidelines.md` §5 separately lists as the **Result filter**'s job ("post-processing of the action result before it's written to the response") — worth being explicit about rather than pretending the categories are perfectly disjoint. This document places it as an action filter because the dominant, request-shaping behavior (deciding whether the action runs at all) is an action filter's job by definition; the response-capture half is a secondary, minority responsibility riding along in the same filter instance rather than justifying a second filter and a second round of context-sharing between them.
- **Ordering:** registered to run **before** `ValidateModelStateFilter` in the action-filter pipeline. A replayed duplicate should return the original (already-validated, already-successful) response without re-running validation at all — validation only matters on the first attempt, which falls through to `next()` and hits `ValidateModelStateFilter` normally on its way to the action.

---

## 6. Costs — Stated Honestly

This is not a free correctness improvement; it has real, ongoing costs that should be weighed against the duplicate-creation problem it solves:

- **Extra Redis round-trip on every create request that carries the header, not just duplicates.** The lookup-then-reserve-then-save sequence (Section 2.2/2.4) adds at least one, typically two, Redis round trips (a few milliseconds each, but on the write path's critical section) to a flow that, at 5M creates/day peak, is already contending for throughput (`01-create-path-extreme-scalability.md` §1.1). This is a real latency cost on *every* protected create, paid to prevent a much smaller number of *actual* duplicates.
- **Storage cost scales with total create volume, not duplicate volume** (Section 4's ~2 GB at peak) — a cost paid up front for a benefit (avoided duplicates) that's only realized for the small fraction of requests that are ever actually retried.
- **The in-flight race is a real gap if handled naively.** Two near-simultaneous retries of the same key, both arriving before the *first* attempt has finished and saved its result, would both see "no record found" under a naive check-then-act sequence and both proceed to create a duplicate anyway — defeating the entire mechanism at exactly the moment it matters most (concurrent retries under load, the scenario this document exists for). The `TryReserveAsync` atomic reservation step (Section 2.4/3) is required, not optional, to close this — it adds another documented failure mode (`409` "request in flight," Section 2.4) that callers now need to know how to handle (typically: back off and retry once more, same as any other transient `409`).
- **Response-replay adds real complexity, not just a lookup.** The filter must serialize and durably store the *entire* original response body and status code, not just a boolean "this happened before" — which means every successful create pays a serialization cost, and the stored payload must stay forward-compatible if `ShortUrlResponse` (`fn-create.md` §12) ever changes shape (an old cached response replayed after a schema change could look inconsistent with what the endpoint would produce today; this is an accepted, bounded risk given the 24-hour TTL, not a solved problem).
- **This does not replace or simplify short-code collision handling.** `fn-create.md` §6 / `01-create-path-extreme-scalability.md` §2's uniqueness mechanism (retry-on-collision in v1, pre-allocated blocks in v2) is an orthogonal concern — idempotency prevents the *same logical request* from creating two rows; it does nothing about two *different* logical requests independently needing distinct, non-colliding codes. Both mechanisms are needed together; neither subsumes the other.
- **A caller that never sends the header gets no protection**, by this design's own choice (Section 2.1) — that is a deliberate trade-off (avoiding a hard requirement on every caller), but it does mean the duplicate-creation problem in Section 1 is only actually solved for callers that adopt the header, not solved unconditionally at the server. This should be paired with client-library-level enforcement (the same wrapper that adds retry-with-backoff should add the header automatically) so the two policies don't silently drift apart.

---

## 7. Summary of Decisions

| Concern | Decision | Justification (see section) |
|---|---|---|
| Client contract | `Idempotency-Key` header, client-generated GUID, one per logical attempt, optional but paired with the retry-with-backoff client wrapper | §2.1 |
| Duplicate detection | `(CreatorId, IdempotencyKey)` lookup before the create flow runs; match on `RequestBodyHash` | §2.2 |
| Same key, same body | Replay the original stored response verbatim; no new `ShortUrl` row | §2.2 step 4 |
| Same key, different body | `409 Conflict` (not `422` — that's reserved for the malicious-URL policy rejection in `fn-create.md` §9) | §2.3 |
| Concurrent retries racing the first attempt | Atomic reserve-before-execute (`SET NX`-style); second racer gets `409` "in flight" | §6 |
| Storage | Redis, same managed tier as `07-redis-caching-and-invalidation.md`, separate keyspace (`idem:{creatorId}:{key}`) | §3 |
| `Id`/`RowVersion` compatibility | Not a table row — natural key replaces `Id`; write-once design means `RowVersion` has no job; TTL replaces soft delete | §3.1 |
| TTL | 24 hours | §4 |
| Pipeline placement | MVC action filter (`IdempotencyKeyFilter : IAsyncActionFilter`), concretizing the Action filter placeholder in `design-guidelines.md` §5, registered ahead of `ValidateModelStateFilter` | §5 |
| Cost accepted | Extra Redis round-trip per protected create, storage proportional to total (not just duplicate) volume, added in-flight-race and response-replay complexity, no protection for callers that omit the header | §6 |
