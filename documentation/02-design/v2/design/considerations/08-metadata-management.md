# v2 Design Consideration — Metadata Management at Scale

**Status:** v2 scalability exploration (not yet adopted into the shipped v1 design).
**Traceability:** `prompt@review-desig.md` (review scope item: *"explain how the metadata of the files can be managed. This can be a separate document"*).
**Companion documents:** `03-elasticsearch-vs-sql-server.md` (the relational/ES split this document applies, not re-litigates), `06-output-caching-bff-cdn.md` (CDN delivery — one-sentence cross-reference only, see Section 3), `07-redis-caching-and-invalidation.md` (hot-path caching of the mapping itself), `../../v1/design/fn-create.md` (v1 `ShortUrl` entity shape), `requirement.app.functional.md` (AF-05 metadata retrieval, AF-23 area — QR-code stretch), `01-summary.md` (QR code confirmed optional/stretch, not committed v1), `UrlShortner/global/guidelines/data-design-guidelines.md` (soft-delete convention this document confirms still holds).

---

## 0. Interpretation of "Metadata of the Files" — Stated Up Front

The review prompt's phrasing is ambiguous: "the metadata of the files" could mean (a) the structured data fields associated with each link, or (b) actual binary files, or metadata *about* files. This document deliberately covers **both**, because both are real at this project's scale:

1. **Structured link metadata** — the record-of-truth fields per link (original URL, short code, creator, timestamps, active/expiry status, custom alias) that AF-05 requires be retrievable. This is data, not a file, but it is the metadata a caller most obviously means by "a link's metadata."
2. **Binary/file-style artifacts** — the one place this project has an actual *file* in play: the optional QR-code image per link (AF-23 area, confirmed optional/stretch in `01-summary.md`, not a committed v1 deliverable). If this feature is ever built, the generated image is a file, and *its* storage/addressing is a distinct architectural question from (1).

Section 1 covers (1), Section 2 covers (2), Section 3 covers lifecycle for both, Section 4 is a labeled future-consideration note on metadata search. This split is intentional — collapsing them into one story would either force structured data through a file-storage system it doesn't need, or force a binary image through a relational column, and both are wrong.

---

## 1. Structured Link Metadata (AF-05) at 5M+ New Records/Day

**This is not a new decision — it is the application of the split already made in `03-elasticsearch-vs-sql-server.md`.** That document draws a hard line between the `ShortUrl` mapping table and the click/analytics event store. The fields AF-05 asks for (original URL, short code, creator, created/expiry timestamps, active status, custom alias) are **all columns on `ShortUrl` itself**, not derived analytics — so they inherit the "keep it relational" side of that split, not the Elasticsearch side.

- **Storage:** relational (SQL Server at v2 scale, per `data-design-guidelines.md`'s migration path off SQLite). Same table `fn-create.md` already defines — `Code`, `OriginalUrl`, `CreatedAtUtc`, `CreatedBy`, `ExpiresAtUtc`, `IsDeleted`, `OwnerDepartmentId`, plus the standard audit/`RowVersion` columns from `data-design-guidelines.md`. AF-05 is answered by reading this one row; there is no separate "metadata store" to build.
- **Why relational still holds at 5M creates/day:** `03-elasticsearch-vs-sql-server.md` Section 2 makes the workload-shape argument for the *event* store (100M writes/day, aggregation-heavy, loss-tolerant). The `ShortUrl` table's shape is the opposite: 5M new rows/day is meaningfully lower write volume than the click-event stream one order of magnitude below it, each row needs strong consistency (a redirect must always resolve correctly — ANFR-02), and the access pattern is point-lookup-by-code, which is exactly what a relational primary-key/unique-index lookup is best at. Nothing about 5x growth changes that shape; it just means more rows in the same well-indexed table.
- **Indexing at scale:** the unique index on `Code` (already established in `fn-create.md` Section 11) remains the only index this access pattern needs. `data-design-guidelines.md` Section 7's standard `RowVersion`/`IsDeleted` indexes cover the rest. No new indexing strategy is required by the 5M/day projection — the table simply grows; it does not change shape.
- **Serving at scale:** the AF-05 metadata read sits behind the same caching layer as the redirect lookup (`07-redis-caching-and-invalidation.md`) — a metadata fetch and a redirect fetch are both "look up by `Code`," so they share the cache-aside `CachingShortUrlRepository` decorator rather than needing a second caching mechanism.
- **What this section deliberately does not re-argue:** whether Elasticsearch is a better fit for this table. It is not — `03-elasticsearch-vs-sql-server.md` Section 7 already scopes Elasticsearch narrowly to the event store and explicitly states "it does not migrate the core mapping table to Elasticsearch." This document defers to that decision rather than repeating its reasoning.

---

## 2. Binary/File-Style Artifacts — QR Codes (If Built)

Unlike Section 1, this is genuinely a *file* storage question — and it only exists **if** the optional QR-code feature (AF-23 area) is ever promoted out of stretch-goal status. `01-summary.md` is explicit that QR code generation is optional/stretch, not committed for v1; nothing here commits it for v2 either. This section designs the storage shape **so the decision is ready if/when that feature is greenlit**, not to declare the feature in scope now.

### 2.1 Why a generated QR image does not belong in the relational database

- A QR code is a small (typically a few KB, PNG or SVG) binary image **derived** from the short code — it can always be regenerated from `Code` alone, so it is a cache/artifact, not a system of record. Storing it as a `BLOB` column on `ShortUrl` (or a child table) would bloat the OLTP table's row size, degrade buffer-pool efficiency for the high-frequency `Code` lookups Section 1 depends on, and complicate backup/restore of the primary database with data that isn't actually authoritative.
- At the stated scale (up to 5M new links/day), if even a modest fraction opt into a QR code, that is millions of new small binary objects per day — a workload that maps directly onto object storage, not onto rows in a transactional table.

### 2.2 Recommendation: object storage, not the primary database

- **Store generated QR images in object storage** — Azure Blob Storage (or an S3-compatible equivalent) — never as a BLOB in SQL Server. Object storage is purpose-built for exactly this shape: large numbers of small-to-medium immutable binary objects, cheap at scale, natively durable/replicated, and decoupled from the OLTP database's capacity and backup story.
- **Naming/addressing scheme tied to the short code:** use the short code itself as (or as the deterministic prefix of) the object key, e.g. `qr/{Code}.png` (optionally sharded by a hash prefix for very large containers, e.g. `qr/{Code[0..1]}/{Code}.png`, if a single container's object count ever becomes an operational concern). This keeps addressing trivial and collision-free for the same reason `Code` is already unique in the relational table (Section 1) — no second ID scheme is invented for the same link.
- **Generation strategy:** generate-on-first-request-then-cache-in-object-storage (lazy), rather than generating a QR image for every link at creation time — most links likely never have their QR code requested, and generating 5M images/day unconditionally would be pure waste. The object's existence check (`HEAD qr/{Code}.png`) doubles as the cache check.
- **Delivery:** front the object storage container with a CDN for actual serving — this is the same output-caching/CDN mechanism already designed in `06-output-caching-bff-cdn.md`; that document is not duplicated here, only referenced, since QR images are immutable-once-generated and are exactly the kind of content that document's CDN layer is built to serve well.

### 2.3 What this section does not do

It does not commit the QR-code feature to v2 scope — it only answers "if built, where do the files live," per the review prompt's explicit ask. If the feature is never built, this section is inert.

---

## 3. Metadata Lifecycle at Scale — Delete/Deactivate/Expire

This is a confirmation, not a new pattern: `data-design-guidelines.md` Section 5 already establishes soft delete (`IsDeleted` + `DeletedAtUtc`) as the standard for every table, and `fn-create.md`/AF-07 already model deactivation this way for `ShortUrl`. The question here is only whether that convention still holds once the table has 5M+ new rows/day flowing into it over years.

- **Structured metadata (Section 1):** deletion/deactivation/expiry all remain soft-delete/status-flag operations on the existing `ShortUrl` row — `IsDeleted`/`DeletedAtUtc` for explicit removal (AF-07), `ExpiresAtUtc` plus the existing "expired" check (`fn-fetch.md`) for time-based lapsing. Nothing about scale changes this: soft-delete's cost is one boolean/timestamp per row, and the global EF Core query filter that excludes soft-deleted rows (`data-design-guidelines.md` Section 5) continues to work at 5M+ rows/day the same way it does at v1 scale — it is a `WHERE` predicate on an indexed column, not an operation whose cost grows qualitatively with table size. The one thing worth flagging for a future operational pass (not this document's scope): at multi-year scale, soft-deleted/expired rows accumulate indefinitely since v1 explicitly decided against a restore window or purge job (`01-summary.md` Section B — "deactivation is final"); if the table's total row count becomes an operational concern, an archival/cold-storage job for old soft-deleted rows is the natural next step, but that is a storage-housekeeping decision, not a change to the soft-delete pattern itself.
- **Binary artifacts (Section 2, if built):** when a link is deactivated/deleted, its QR object in blob storage becomes an orphan — it is addressed by `Code`, and `Code` no longer resolves to an active link. Two options, consistent with the "no hard delete" spirit of the soft-delete convention: (a) leave the object in place (cheap, harmless — a stale QR image pointing at a dead short code just 404s or redirects to the "expired" response like any other request for that code) and let a periodic lifecycle-management job (e.g., Azure Blob lifecycle policy) age out objects whose backing `ShortUrl` is soft-deleted past a retention window, mirroring the same "no immediate hard purge" instinct as the relational soft-delete convention; or (b) eagerly delete the object on deactivation if storage cost ever justifies it. Given QR images are cheap, small, and regenerable, (a) — passive lifecycle expiry, not eager deletion — is the better default and keeps the artifact's lifecycle policy symmetric with the relational soft-delete philosophy rather than introducing a second, inconsistent deletion model.

---

## 4. Indexing/Search Over Metadata Itself — Future Consideration Only

**Labeled explicitly as a future consideration, not a v2 commitment.** The review prompt raises the question; v1 scope does not include it, and this document does not propose building it.

- A plausible future ask: a creator wants to search/filter their own links by metadata — e.g., "show me all my links created last month," "find the link I made with alias `promo-2026`," "show my active vs. expired links." None of AF-01–AF-10 asks for this today, and `01-summary.md` does not list it as an out-of-scope item either — it is simply undiscussed, which is why it belongs here as a flagged gap rather than a designed feature.
- **If this is ever built:** it is a query over `ShortUrl` columns (`CreatedBy`, `CreatedAtUtc`, `Code`/alias, `IsDeleted`, `ExpiresAtUtc`) scoped to one creator's own rows — a small, filtered, low-cardinality-per-user result set. That shape does **not** obviously need Elasticsearch the way the click-event store does (Section 1 above, `03-elasticsearch-vs-sql-server.md`): a per-creator link list is nowhere near the 100M-events/day aggregation workload that justified ES there. A composite relational index on `(CreatedBy, CreatedAtUtc)` would likely satisfy this without introducing a second search system, unless free-text search over URLs/aliases becomes a real requirement, at which point revisiting Elasticsearch (or SQL Server full-text search) for this specific case would be a fresh, separate decision.
- This paragraph is intentionally brief and speculative — it exists so a future reviewer sees the gap was considered and consciously deferred, not missed.

---

## 5. Summary

| Metadata type | Storage | Key design point |
|---|---|---|
| Structured link metadata (AF-05 fields) | Relational (`ShortUrl` table) — same store as the mapping itself, per `03-elasticsearch-vs-sql-server.md` split | No new store needed; scales by row count, not by architecture change; served through the existing `Code`-keyed cache |
| QR-code image (optional, AF-23 area, if built) | Object storage (Azure Blob Storage/S3-compatible), keyed by short code, fronted by CDN (`06-output-caching-bff-cdn.md`) | Never a DB `BLOB`; lazily generated; passively lifecycle-expired alongside the link's soft-delete, not eagerly purged |
| Lifecycle (delete/deactivate/expire) | Soft-delete flags on `ShortUrl` (structured); passive object-lifecycle policy (binary) | Confirms `data-design-guidelines.md` Section 5 holds unchanged at 5M+ rows/day; no purge job exists today per v1's "deactivation is final" decision |
| Metadata search/filter by creator | Not built | Future consideration only — likely a relational composite index if ever built, not an ES commitment |
