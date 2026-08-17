# DB Design — MVP (v3)

**Status:** As-built. Documents the schema actually created by the applied EF Core migration, not an aspirational design.
**Consistent with:** `UrlShortener/engineering-standards/guidelines/data-design-guidelines.md`.
**DB file:** `src/db/urlshortner.db` (SQLite), created by `dotnet ef database update` and re-applied automatically on every app startup via `AppDbContext.Database.Migrate()`.

---

## 1. Scope

One table: `ShortUrls`. This MVP is scoped to create + fetch only (see `documentation/02-design/v3.MVP/agents/agent-prompt.md`), so no `AccessEvents`/analytics table, no `Departments`/ownership table, etc. — those are out of scope for this phase (see `documentation/02-design/v1/design/fn-analytics.md`, `fn-create.md` §4).

## 2. Schema — `ShortUrls`

```sql
CREATE TABLE "ShortUrls" (
    "Id"                INTEGER NOT NULL CONSTRAINT "PK_ShortUrls" PRIMARY KEY AUTOINCREMENT,
    "Code"              TEXT    NOT NULL,
    "OriginalUrl"       TEXT    NOT NULL,
    "CreatedAtUtc"      TEXT    NOT NULL,
    "CreatedBy"         TEXT    NOT NULL,
    "LastModifiedAtUtc" TEXT    NULL,
    "LastModifiedBy"    TEXT    NULL,
    "RowVersion"        INTEGER NOT NULL,
    "IsDeleted"         INTEGER NOT NULL,
    "DeletedAtUtc"      TEXT    NULL
);

CREATE UNIQUE INDEX "IX_ShortUrl_Code" ON "ShortUrls" ("Code");
```

| Column | Type | Notes |
|---|---|---|
| `Id` | `INTEGER` (long) | Surrogate PK, autoincrement — data-design-guidelines.md §2. |
| `Code` | `TEXT`, max 32 | The generated short code (AF-04). 7-character random base62 in practice — see §4. |
| `OriginalUrl` | `TEXT`, max 2048 | The long URL. Immutable after insert (no update path exists in this MVP) — Q7. |
| `CreatedAtUtc` | `TEXT` (DateTime) | Set once on insert by `AppDbContext.SaveChangesAsync`. |
| `CreatedBy` | `TEXT` | Standard audit field. MVP value is the literal `"system"` placeholder — real auth/identity is out of scope for this MVP (see `documentation/02-design/v1/design/fn-create.md` §4, Q1/Q2). |
| `LastModifiedAtUtc` / `LastModifiedBy` | `TEXT` / `TEXT`, nullable | Standard audit fields. Always `NULL` today — no update use case exists in this MVP. |
| `RowVersion` | `INTEGER` (long) | App-maintained optimistic-concurrency token, starts at 1, incremented on update by `AppDbContext.SaveChangesAsync` — data-design-guidelines.md §4. |
| `IsDeleted` / `DeletedAtUtc` | `INTEGER` (bool) / `TEXT`, nullable | Standard soft-delete columns. Present per the standing convention but **unused by any MVP endpoint** — deactivation (AF-07) is out of scope for this MVP. A global EF Core query filter (`!IsDeleted`) is already configured on `AppDbContext`, so the column is live and ready the moment a delete endpoint is added. |

## 3. Indexing

- `IX_ShortUrl_Code` — **unique** index on `Code`. This is both the uniqueness guarantee for AF-04's collision handling and the index that makes the AF-02 redirect lookup (`WHERE Code = @code`) an index seek rather than a table scan — the single hottest query in the system (ANFR-01, ANFR-05, ANFR-06). It is also the backstop for the narrow check-then-act race in `ShortUrlService.ResolveSystemGeneratedCodeAsync` (the existence check and the insert are two separate round trips): if two concurrent requests ever generate and pass the check for the same candidate, the second insert fails this constraint (`DbUpdateException`) rather than silently creating a duplicate row. This MVP does not retry on that exception (falls through to the generic `500` in `exception-and-logging-strategy.md` §4/§6) — negligible-probability given the 62^7 candidate space (§5), and documented as the upgrade path if real concurrent write volume ever makes it worth closing.
- No index was added on `RowVersion` or `IsDeleted` for this MVP (data-design-guidelines.md §7 calls these out as "at minimum" for tables expected to grow large / support delta-sync) — deferred as premature for a two-endpoint MVP with no sync/analytics consumer yet; trivial to add later via a new migration.

## 4. Deliberately excluded from this MVP's schema (documented, not silent)

| Not in scope | Why | Full design already exists at |
|---|---|---|
| `ExpiresAtUtc` column | Optional expiration is out of scope for this MVP | `documentation/02-design/v1/design/fn-create.md` §8, `fn-fetch.md` §7.1 |
| `OwnerDepartmentId` column | Ownership capture depends on the (out-of-scope) auth seam | `documentation/02-design/v1/design/fn-create.md` §4 |
| Access-count / analytics columns or table | Analytics (AF-08/09/10) is explicitly out of scope | `documentation/02-design/v1/design/fn-analytics.md` |
| Custom-alias-specific columns | Not needed — `Code` already covers both system-generated and (future) custom codes | `documentation/02-design/v1/design/fn-create.md` §7 |

## 5. Short-code generation approach (why the schema needs no more than `Code` + a unique index)

This MVP uses the **v1 "random base62 (7 chars) + collision retry against `IX_ShortUrl_Code`"** approach (`UrlShortener.Infrastructure.ShortUrls.RandomBase62ShortCodeGenerator` + the retry loop in `UrlShortener.Application.ShortUrls.ShortUrlService`), **not** the v2 pre-allocated-ID-block extreme-scale approach in `documentation/02-design/v2/design/considerations/01-create-path-extreme-scalability.md` — that approach would require additional schema (an ID-block allocation table) that is unwarranted at this MVP's scale. It remains the documented upgrade path if this system needs to scale past what a single-writer SQLite file + collision retry can support (data-design-guidelines.md §1's SQLite trade-offs).

## 6. Caching

No cache table/column — caching is a pure read-through seam (`IShortUrlCache`, implemented today by the no-op `NullShortUrlCache`), not a schema concern. See `documentation/02-design/v3.MVP/design/api-design.md` and `documentation/02-design/v2/design/considerations/07-redis-caching-and-invalidation.md` for the deferred real-cache design.
