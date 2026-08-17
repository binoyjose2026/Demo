# Data Design Guidelines

These are the project's standard conventions for designing and evolving relational database tables. They apply to **every table** in the database, without exception, so that any developer (or any generated/scaffolded code) can rely on a predictable, consistent shape: how a row is uniquely identified, who created/changed it and when, how a caller can tell a row has changed since it last read it, and how deletion is handled. They are written to be consistent with the project's [C# Coding Guidelines](./coding-guidelines.md) (PascalCase members, nullable reference types, EF Core-idiomatic patterns) and with mainstream, widely-used **EF Core** / relational schema design practice — nothing here is a one-off invented rule.

---

## 1. Database Technology

**Standard: SQLite, accessed through EF Core (the standard .NET ORM).**

SQLite is the project's standard embedded, file-based database engine.

- The entire database lives in a **single file** (e.g., `App.db`) that can be committed to source control alongside the codebase and shipped with the application — no separate database server to install, configure, or manage on the target machine.
- It requires **zero external server dependency**: the database engine is just a library linked into the app (via the `Microsoft.Data.Sqlite` provider), so "installing the database" is simply deploying the app.
- It is fully supported as a first-class **EF Core provider** (`Microsoft.EntityFrameworkCore.Sqlite`), so all data access should go through **EF Core** — `DbContext`, LINQ queries, and **EF Core Migrations** — rather than hand-written ADO.NET/SQL, consistent with the parameterized-query/ORM guidance in the coding guidelines.
- It's an excellent fit for this project's stated need: a portable, ship-in-the-repo database for a single-application, low-to-moderate concurrency workload.

**Trade-offs — know these before assuming SQLite scales to every scenario:**

- **Not a fit for high-concurrency, multi-writer production workloads.** SQLite uses file-level/database-level locking; it handles many concurrent readers well, but concurrent *writers* serialize against each other. It is not a replacement for a server-based RDBMS (SQL Server, PostgreSQL) in a multi-user server application with sustained concurrent writes.
- **No native network access.** The database file must be reachable on local disk (or a reliably-locking network file system) by the process using it — it is not queryable remotely like a client/server database.
- **Limited native types.** SQLite uses dynamic typing with a small set of storage classes (`INTEGER`, `REAL`, `TEXT`, `BLOB`, `NULL`); types like `DATETIME`, `DECIMAL`, `GUID`, and `BOOLEAN` are emulated by EF Core's SQLite provider on top of these, not native column types. Stick to EF Core's mapped types and avoid raw SQL that assumes SQL Server/PostgreSQL type behavior.
- **No native `ROWVERSION`/`ROWID` auto-updating column** the way SQL Server does — see Section 4 for the standard workaround used in this project.
- If the project later needs multi-user server concurrency, horizontal scale, or remote access, that is a signal to migrate to a server-based RDBMS — EF Core's provider model makes that a swappable decision, not a rewrite, as long as data access stays behind EF Core rather than SQLite-specific SQL.

---

## 2. Primary Key Convention

**Standard: every table has a single surrogate primary key column named `Id`, typed as an auto-incrementing 64-bit integer (`long` / `INTEGER PRIMARY KEY AUTOINCREMENT`).**

```csharp
public long Id { get; set; }
```

- Every table gets exactly one primary key column, named `Id` (not `TableNameId`, not `pkID`, not `PK_Id`). This is the standard, EF Core-idiomatic convention (EF Core's default key-discovery convention looks for a property literally named `Id` or `<TypeName>Id`) and reads cleanly as `Order.Id`, `Customer.Id`, etc.
- **Type: `long` (SQLite `INTEGER`), auto-incrementing**, using SQLite's native `INTEGER PRIMARY KEY AUTOINCREMENT` (EF Core generates this automatically for an integer key by convention — no extra configuration needed). This is chosen over a `Guid` primary key for this project because:
  - It's the simplest, most SQLite-idiomatic option — SQLite's `INTEGER PRIMARY KEY` column *is* the table's efficient row-lookup key (a `rowid` alias), so integer keys are smaller, faster to index/join on, and produce a smaller database file than `GUID`/`TEXT` keys.
  - It's the default EF Core produces with zero configuration, keeping every entity consistent without per-table fluent configuration.
  - This is a single-file, effectively single-writer embedded database (see Section 1), so the classic reason to prefer GUIDs — generating unique IDs client-side across multiple independent writers/machines before insert (e.g., offline-first sync, merge replication) — does not apply here. If a future table genuinely needs client-generated or globally-unique IDs (e.g., syncing rows created offline on multiple devices), use a `Guid` for that specific table's `Id` and call it out explicitly as an exception; don't mix key types silently.
- **Foreign keys** reference the primary key by the pattern **`<ReferencedTable>Id`**, matching the target table's `Id` type (`long`). Example: a `Order` table referencing `Customer` has a column named `CustomerId` of type `long`. This is both the standard relational naming convention and EF Core's default convention for discovering foreign key relationships without extra configuration.

```csharp
public class Order
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
}
```

---

## 3. Standard Audit Fields

Every table includes a fixed set of audit columns recording who created/last modified the row and when:

| Column             | Type            | Notes                                                        |
|---------------------|-----------------|---------------------------------------------------------------|
| `CreatedAtUtc`       | `DateTime`      | Set once, on insert. Never updated afterward.                 |
| `CreatedBy`          | `string`        | Identity (username, user ID, or system/service name) that created the row. |
| `LastModifiedAtUtc`  | `DateTime?`     | Null until the row is first updated; set on every subsequent update. |
| `LastModifiedBy`     | `string?`       | Identity that made the last update; null until first updated. |

- **Always store timestamps in UTC** (`DateTime.UtcNow`, never `DateTime.Now`), reflected in the `...AtUtc` naming suffix so the timezone is unambiguous at every call site — SQLite has no native timezone-aware datetime type, so consistency here is what prevents subtle bugs across machines/timezones.
- Audit fields should be set by a common mechanism (e.g., overriding `DbContext.SaveChanges`/`SaveChangesAsync`, or a `SaveChanges` interceptor) rather than by each call site remembering to set them by hand — this guarantees consistency and follows the DRY/single-responsibility spirit of the coding guidelines.

---

## 4. Change-Detection / Concurrency Guard Field

**Standard: every table includes a `RowVersion` column — a `long`, starting at `1`, incremented by the application on every update — used both for optimistic concurrency control and for delta/incremental sync.**

```csharp
public long RowVersion { get; set; }
```

Why an application-maintained integer instead of SQL Server-style `ROWVERSION`/`TIMESTAMP`: SQLite has no native auto-updating row-version type, so the standard, well-known EF Core + SQLite pattern is to model an ordinary integer column and mark it as a **concurrency token**, then increment it yourself whenever a row is saved:

```csharp
// In OnModelCreating (or via IEntityTypeConfiguration<T>)
modelBuilder.Entity<Order>()
    .Property(o => o.RowVersion)
    .IsConcurrencyToken();
```

```csharp
// In a DbContext.SaveChangesAsync override (or a SaveChanges interceptor),
// applied uniformly to every modified entity that has a RowVersion property:
public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
{
    var utcNow = DateTime.UtcNow;

    foreach (var entry in ChangeTracker.Entries<IAuditable>())
    {
        if (entry.State == EntityState.Added)
        {
            entry.Entity.CreatedAtUtc = utcNow;
            entry.Entity.RowVersion = 1;
        }
        else if (entry.State == EntityState.Modified)
        {
            entry.Entity.LastModifiedAtUtc = utcNow;
            entry.Entity.RowVersion++;
        }
    }

    return base.SaveChangesAsync(cancellationToken);
}
```

This single column does double duty:

- **Optimistic concurrency control**: because it's configured with `IsConcurrencyToken()`, EF Core automatically includes `WHERE RowVersion = @original_value` on the generated `UPDATE`, and throws `DbUpdateConcurrencyException` if another process already changed the row — exactly the same pattern SQL Server's `ROWVERSION` enables, just maintained explicitly instead of by the engine.
- **Delta/incremental sync**: a consumer (sync job, cache, API client) can record the highest `RowVersion` it has seen and later query `WHERE RowVersion > @lastSeenValue` to cheaply fetch only rows that changed — without comparing every column or relying on timestamp precision/clock-skew, which is why an incrementing integer is preferred here over relying on `LastModifiedAtUtc` alone for change detection.

`LastModifiedAtUtc` (Section 3) remains useful for human-readable "when did this change" auditing, but `RowVersion` is the field to use programmatically for "has this row changed."

---

## 5. Soft Delete Convention

**Standard: soft delete, using `IsDeleted` + `DeletedAtUtc` columns. Rows are never physically removed by normal application code.**

| Column        | Type        | Notes                                              |
|----------------|-------------|-----------------------------------------------------|
| `IsDeleted`    | `bool`      | Defaults to `false`. Set `true` to "delete" a row.  |
| `DeletedAtUtc` | `DateTime?` | Null unless `IsDeleted` is `true`; set at the same time. |

Rationale, consistent with the audit-trail spirit of this document:

- It preserves the same audit trail we just standardized (`CreatedBy`/`LastModifiedBy`/`RowVersion`) — a hard `DELETE` destroys history that audit fields exist to preserve.
- It's safer for an embedded/file-shipped database: it avoids accidental, unrecoverable data loss and plays well with the delta-detection pattern in Section 4 (a `RowVersion` bump on soft-delete is itself a detectable "change" a sync consumer can propagate as a deletion, rather than the row simply vanishing).
- Apply a **global EF Core query filter** per entity (`modelBuilder.Entity<T>().HasQueryFilter(e => !e.IsDeleted)`) so soft-deleted rows are excluded from normal queries by default, without every LINQ query needing to remember to filter them out.
- If a specific table has a genuine, deliberate need for hard deletes (e.g., purging data for compliance/retention), call that out explicitly as a documented exception rather than deviating silently.

---

## 6. Naming Conventions

Consistent with the [C# Coding Guidelines](./coding-guidelines.md)'s PascalCase rule for public members:

- **Tables**: PascalCase, singular noun (e.g., `Order`, `Customer`, `OrderLineItem`) — matching the C# entity class name 1:1, per EF Core convention.
- **Columns**: PascalCase, matching the C# property name exactly (`CreatedAtUtc`, `CustomerId`, `RowVersion`) — no `snake_case`, no Hungarian prefixes, consistent with Section 1 of the coding guidelines.
- **Primary keys**: always `Id` (Section 2).
- **Foreign keys**: always `<ReferencedTable>Id` (Section 2), e.g., `CustomerId`, `OrderId`.
- **Join/junction tables** (many-to-many): name as `<TableA><TableB>` in a consistent, alphabetical or natural-reading order (e.g., `OrderTag` joining `Order` and `Tag`), and still include the full standard column set from this document (`Id`, audit fields, `RowVersion`, soft-delete fields) — a join table is a table like any other under these conventions.
- **Indexes**: `IX_<Table>_<Column(s)>`, e.g., `IX_Order_CustomerId`, `IX_Order_RowVersion` — EF Core's default index-naming convention, kept explicit rather than overridden.
- **Foreign key constraints**: `FK_<Table>_<ReferencedTable>_<Column>`, e.g., `FK_Order_Customer_CustomerId` — again, EF Core's default; left as-is rather than customized, so migrations stay predictable.

---

## 7. Indexing Guidance

At minimum, every table should index:

- **`RowVersion`** — supports the delta/incremental-sync query pattern (`WHERE RowVersion > @lastSeenValue`) efficiently instead of a full table scan.
- **Foreign key columns** (e.g., `CustomerId` on `Order`) — EF Core creates these automatically for navigation properties, but confirm they exist for any manually-configured relationship.
- **`IsDeleted`** (or a composite index that leads with it, e.g., `IX_Order_IsDeleted_RowVersion`) when tables grow large enough that scanning past soft-deleted rows becomes measurable — not required on day one for small tables, but worth adding proactively on tables expected to accumulate many soft-deleted rows.
- Any column used in a frequent `WHERE`, `ORDER BY`, or join clause beyond the above — add indexes driven by actual query patterns, not speculatively on every column.

Configure indexes explicitly via `IEntityTypeConfiguration<T>`/`OnModelCreating` (`HasIndex(...)`) rather than relying only on implicit ones, so intent is visible in code and captured in migrations.

---

## 8. Schema Versioning

Use **EF Core Migrations** as the standard mechanism for versioning the SQLite schema over time:

- Every schema change (new table, new column, index change) is captured as a migration (`dotnet ef migrations add <Name>`) and committed to source control alongside the code change that needs it.
- `dotnet ef database update` (or `context.Database.Migrate()` on application startup) applies pending migrations to the shipped SQLite file, keeping the schema in the repo and the schema on disk in lock-step as the single source of truth.
- Never hand-edit the SQLite file's schema outside of a migration — that breaks the migration history's ability to reliably recreate the schema from scratch on a new machine or in tests.

---

## Standard Column Set — Summary

Every table in this project includes, at minimum, the following columns (illustrated here as a reusable EF Core base entity — concrete entities can inherit from it to get the standard columns automatically):

```csharp
/// <summary>
/// Base class providing the standard Id, audit, concurrency, and soft-delete
/// columns that every table in this project must have.
/// </summary>
public abstract class AuditableEntity
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public long RowVersion { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}
```

```sql
-- Equivalent standard columns in raw SQL, for reference:
Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
CreatedAtUtc         TEXT    NOT NULL,
CreatedBy            TEXT    NOT NULL,
LastModifiedAtUtc     TEXT,
LastModifiedBy        TEXT,
RowVersion           INTEGER NOT NULL DEFAULT 1,
IsDeleted            INTEGER NOT NULL DEFAULT 0,
DeletedAtUtc         TEXT
```
