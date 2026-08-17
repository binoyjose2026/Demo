# Reliability & Availability Design

**Layer:** Cross-cutting (Api, Application, Infrastructure)
**Traceability:** ANFR-01 (redirect availability), ANFR-02 (stable resolution), ANFR-03 (durability), ANFR-04 (graceful degradation) — see `requirement.app.non-functional.md`. Scope decisions from `00-getting-started/in-scope/01-summary.md` (Q7, Q35) and `00-getting-started/out-of-scope/01-summary.md` (Q35–Q37).
**Consistent with:** `UrlShortner/global/guidelines/design-guidelines.md`, `UrlShortner/global/guidelines/data-design-guidelines.md`, `UrlShortner/global/guidelines/coding-giudelines.md`.

---

## 1. Availability Standard for v1

**Decision: v1 targets a best-effort availability standard, not a formal contractual SLA.**

- `01-summary.md` (in-scope, Q35) states plainly: the service is designed to the qualitative reliability/performance targets in the non-functional requirements (low-latency redirect, high availability) "as a best-effort standard — without a formal contractual SLA."
- `01-summary.md` (out-of-scope, Q35–Q37) confirms the corollary: no numeric uptime/response-time commitment, no formal support channel, and no public status page are part of v1.
- **Consequence for this design:** every mechanism below (health checks, durability approach, optimistic concurrency) exists to make the service *behave* reliably and to make its health *observable*. None of it is offered as, or should be read as, a measured/contracted uptime number (e.g., "99.9%"). If a future version adopts a contractual SLA, that decision — and the monitoring/alerting/error-budget machinery it requires — belongs in a v2+ document, not here.
- **Exception:** ANFR-01 ("redirect path shall be highly available") is satisfied qualitatively for v1 — via the design choices in Section 2–4 — rather than via a measured SLO/error-budget. This is intentional PoC-scope narrowing, not an oversight.

---

## 2. Consistency Guarantee: Stable Short-Code Resolution (ANFR-02)

**Requirement:** "A short code shall consistently resolve to the same original URL for the lifetime of that mapping." (ANFR-02)

**Design enforcement — immutability of the original URL:**

Per the in-scope decision at Q7, the original long URL behind a short code is **immutable after creation** — there is no edit/update API for the `OriginalUrl` field. This is not just a product rule; it is the mechanism that makes ANFR-02 true by construction rather than by runtime discipline:

- If the mapping `ShortCode → OriginalUrl` can never change once written, then "consistently resolves to the same URL" is guaranteed the moment the row is durably committed — there is no code path that could produce drift, no race between a concurrent edit and a concurrent redirect, and no need for read-time reconciliation logic.
- The `Application` layer exposes no `UpdateOriginalUrlAsync`/`PATCH` operation on `ShortUrl` for the `OriginalUrl` property. The only writes permitted after creation are **lifecycle** writes (deactivation, expiry) which change *availability* of the mapping, never its *target*.

```csharp
// UrlShortner.Domain
public class ShortUrl : AuditableEntity
{
    public string Code { get; set; } = string.Empty;

    // Set once, at creation. No setter path exists anywhere in Application
    // that mutates this after the entity is first persisted — this is what
    // makes ANFR-02 a structural guarantee, not a convention developers must remember.
    public string OriginalUrl { get; private set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAtUtc { get; set; }

    // Constructor/factory is the only writer of OriginalUrl.
    public static ShortUrl Create(string code, string originalUrl, string createdBy) =>
        new()
        {
            Code = code,
            OriginalUrl = originalUrl,
            CreatedBy = createdBy,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = 1,
        };
}
```

- `IShortUrlRepository` (per `design-guidelines.md` §2) exposes no update method that touches `OriginalUrl`; `IRepository<T>.Update` is used only for the lifecycle fields (`IsActive`, `ExpiresAtUtc`) via the `Application` service, never for the immutable field.
- **Retired codes are never reused** (in-scope Q11): once a code is deactivated/removed, it is never reissued for a different URL. This closes the one remaining way ANFR-02 could be violated in spirit — a *new* mapping silently taking over an *old* code. A retired `Code` value is excluded from the short-code generator's candidate pool (enforced by the same uniqueness check used for collision handling, AF-04).
- **What ANFR-02 does *not* claim:** a deactivated/expired link stops *resolving* to a redirect (it instead serves the branded not-found/expired page per in-scope Q10) — but for the *entire lifetime the mapping is active*, it resolves to one and only one `OriginalUrl`. Availability of the mapping and stability of its target are two different guarantees; this document is careful to keep them separate.

---

## 3. Data Durability (ANFR-03)

**Requirement:** "URL mappings shall be durably persisted; no accepted mapping shall be lost due to a single component failure." (ANFR-03)

### 3.1 Baseline durability mechanism

Per `data-design-guidelines.md` §1, the project's standard database is SQLite via EF Core, and the entire database is a single file (e.g., `App.db`) on local disk.

- **Write durability within the process:** SQLite's default journal mode provides atomic, durable commits — a `SaveChangesAsync()` call that returns successfully means the write has been fsynced to the `.db` file (and its journal/WAL file) on disk, not merely buffered in memory. No additional application code is needed to get this guarantee; it is inherent to SQLite's transaction model.
- **Recommendation:** configure **WAL (Write-Ahead Logging) journal mode** (`PRAGMA journal_mode=WAL`) rather than the default rollback-journal mode. WAL improves write durability characteristics under concurrent reads and reduces the chance of partial-write corruption on abrupt process termination, at the cost of an extra `-wal`/`-shm` file alongside `App.db` that must be included in any file-level backup (Section 3.2).

### 3.2 Backup and file-level durability approach

A single-file embedded database has no built-in replication, so durability beyond "survives an application crash" is a **file-management** concern, not a database-engine concern:

- **Backup mechanism:** use SQLite's [Online Backup API](https://www.sqlite.org/backup.html) (exposed via `Microsoft.Data.Sqlite`'s `SqliteConnection.BackupDatabase()`) on a scheduled basis (e.g., a background hosted service running nightly, or on a configurable interval) to copy the live database to a backup path/volume. This is preferred over a naive file copy of `App.db` because it safely handles a database that is open and being written to concurrently — a raw `File.Copy` while WAL/journal files are active risks copying an inconsistent snapshot.
- **What to back up:** `App.db`, and — if WAL mode is enabled — the accompanying `App.db-wal` and `App.db-shm` files, unless a checkpoint (`PRAGMA wal_checkpoint(TRUNCATE)`) is run immediately before the backup to fold WAL contents back into the main file first.
- **Where to store backups:** a location outside the application's own deployment volume (e.g., a separate disk, mounted network share, or blob storage), so a single disk/VM failure cannot take out both the live file and its backups.
- **Restore path:** documented as "stop the app, replace `App.db` (and WAL/SHM files) with the backup copy, restart the app, let EF Core migrations confirm schema is current." No automated failover is implemented for v1 (see Exception below).

### 3.3 Exception — durability is file-based, not replicated

> **Exception:** This backup/durability approach is materially weaker than a replicated server-based RDBMS (e.g., SQL Server Always On, PostgreSQL streaming replication), which provides continuous, near-zero-RPO durability and automatic failover across nodes. A single SQLite file — even with WAL mode and scheduled backups — has:
> - a non-zero **Recovery Point Objective (RPO)**: any writes since the last backup are lost if the underlying disk fails,
> - no automatic failover: the whole application is unavailable if the host/disk is unavailable, until manually restored.
>
> **Rationale for accepting this in v1/PoC scope:** this is a deliberate consequence of the project's standing decision (`data-design-guidelines.md` §1) to use SQLite for its zero-server-dependency simplicity, appropriate for this project's stated single-application, low-to-moderate-concurrency, PoC scope (no formal SLA — Section 1). ANFR-03's bar is "no accepted mapping lost due to a **single component failure**" (e.g., an application-process crash), which SQLite's atomic commit already satisfies — it is not read as a guarantee against disk/host loss, which is out of scope absent a hosting/infrastructure requirement to the contrary. If the project later needs replicated, near-zero-RPO durability, `data-design-guidelines.md` §1 already names the escape hatch: EF Core's provider model makes migrating to a server-based RDBMS (SQL Server/PostgreSQL) a swappable decision rather than a rewrite, since data access stays behind `IRepository<T>`/`AppDbContext` rather than SQLite-specific SQL.

---

## 4. Health Checks (Buildable v1 Feature)

A concrete, standard ASP.NET Core health-check endpoint design supports ANFR-01 (availability of the redirect path) by making the service's health observable to any process/orchestrator that needs to act on it (load balancer, container runtime, uptime script) — while staying inside the "best-effort, no SLA" framing of Section 1: this is an observability primitive, not a contracted uptime guarantee.

### 4.1 Package and registration

Uses the built-in `Microsoft.Extensions.Diagnostics.HealthChecks` middleware (no third-party dependency), plus the EF Core-specific check package for the SQLite `DbContext`:

```csharp
// Program.cs
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(
        name: "sqlite-db",
        tags: new[] { "ready" })
    .AddCheck<ShortCodeGeneratorHealthCheck>(
        name: "short-code-generator",
        tags: new[] { "ready" });
```

### 4.2 Liveness vs. readiness — two distinct endpoints

Standard ASP.NET Core convention: **liveness** answers "is the process still running and not deadlocked" (should never depend on the database); **readiness** answers "can this instance currently serve traffic" (should depend on the database, since a redirect request cannot be served without it).

```csharp
// Program.cs — after app.MapControllers() / endpoint routing is configured

// Liveness: process-level only. No dependency checks — a DB outage must not
// make an orchestrator think the process itself is dead and restart it needlessly.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false, // run no registered checks; process responding at all == alive
});

// Readiness: gates traffic. Includes the SQLite DbContext check, since the
// redirect path (ANFR-01) cannot function without it.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteJsonAsync, // structured output, see 4.3
});
```

| Endpoint | Checks run | Used for |
|---|---|---|
| `GET /health/live` | None (200 OK if the process can respond at all) | Container/orchestrator restart decisions |
| `GET /health/ready` | `sqlite-db` (`AddDbContextCheck`), `short-code-generator` | Load balancer / traffic-routing decisions; manual/ops polling |

### 4.3 Response shape

Kept consistent with the project's `ProblemDetails`-first API design instinct (`design-guidelines.md` §3) by returning structured JSON rather than the framework's bare `Healthy`/`Unhealthy` string default:

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "sqlite-db", "status": "Healthy", "description": null },
    { "name": "short-code-generator", "status": "Healthy", "description": null }
  ]
}
```

- HTTP status: `200 OK` when all checks pass, `503 Service Unavailable` when any tagged check reports `Unhealthy` (the default `HealthCheckOptions` behavior) — the conventional signal a load balancer/orchestrator already knows how to interpret without custom logic.
- **Exception:** the response body is intentionally minimal (no exception details/stack traces), per the coding guidelines' rule that error output must be free of sensitive data (`coding-giudelines.md` §6) — a health endpoint is often unauthenticated and internet-reachable, so it must not leak internals.

### 4.4 Placement

- Registered in `UrlShortner.Api` (`Program.cs`), consistent with `design-guidelines.md` §1 — `Api` is the only executable/composition-root project.
- Not versioned under `/api/v1/...` — health endpoints are an infrastructure/ops concern, not a business API surface, so they stay at a stable, unversioned path.

---

## 5. RowVersion as a Reliability/Consistency Signal

Per `data-design-guidelines.md` §4, every entity (including `ShortUrl`) carries a `RowVersion` (`long`, application-incremented, configured as an EF Core concurrency token). This column supports reliability/consistency reasoning in two ways relevant to this document:

### 5.1 Detecting lost/conflicting writes (defends ANFR-04's "no data corruption")

- The lifecycle operations that *do* mutate a `ShortUrl` after creation — deactivate (AF-07), set/clear expiry — are concurrency-guarded: EF Core includes `WHERE RowVersion = @original_value` on the generated `UPDATE`, so if two requests race to deactivate/update the same link, the second writer gets a `DbUpdateConcurrencyException` instead of silently overwriting the first writer's change.
- This is how the design satisfies ANFR-04 ("degrade gracefully on backend failure rather than corrupting or losing data") for the *concurrent-write* failure mode specifically: a lost-update race is a correctness bug, and `RowVersion` turns it into a detectable, handleable exception rather than silent data corruption.

```csharp
// Application layer — handling the concurrency exception gracefully (ANFR-04)
public async Task DeactivateAsync(long shortUrlId, CancellationToken cancellationToken)
{
    try
    {
        var shortUrl = await _repository.GetByIdAsync(shortUrlId, cancellationToken)
            ?? throw new NotFoundException(shortUrlId);

        shortUrl.IsActive = false;
        _repository.Update(shortUrl);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateConcurrencyException ex)
    {
        // Expected control-flow outcome of a real race, not an unexpected fault —
        // surfaced as a 409 Conflict ProblemDetails response (coding-giudelines.md §6:
        // prefer a result/exception distinction between "expected failure path" and
        // "truly unexpected condition"; a concurrency conflict is the former here
        // because it is application-detectable and actionable by the caller).
        throw new ConcurrencyConflictException(shortUrlId, ex);
    }
}
```

### 5.2 A cheap, reliable "did anything change" signal for operational tooling

- Because `RowVersion` increments on every update, it doubles as the basis for lightweight reliability tooling without extra columns: a diagnostic/ops query `WHERE RowVersion > @lastSeenValue` (the same delta pattern `data-design-guidelines.md` §4 documents for sync) can be reused to answer "what changed since my last health/consistency sweep" — e.g., an internal job that periodically re-validates active `ShortUrl` rows can checkpoint on `RowVersion` rather than re-scanning the whole table, keeping that kind of consistency-auditing tooling cheap enough to actually run.
- This is a byproduct of the standard column, not a bespoke mechanism added for reliability — consistent with this document's preference for reusing existing project conventions over introducing parallel machinery.

---

## 6. Summary of Decisions

| Concern | Requirement | Decision |
|---|---|---|
| Availability target | ANFR-01 | Best-effort, no formal SLA (Q35). Health checks provide observability, not a contracted uptime number. |
| Stable resolution | ANFR-02 | Enforced structurally via `OriginalUrl` immutability (Q7) + no code reuse after retirement (Q11) — not a runtime check. |
| Durability | ANFR-03 | SQLite atomic commit (WAL recommended) + scheduled Online-Backup-API file backups. **Exception:** weaker than replicated RDBMS durability; accepted for v1/PoC scope. |
| Graceful degradation | ANFR-04 | `RowVersion` optimistic concurrency turns write races into a handleable `409 Conflict` instead of silent corruption. |
| Health observability | ANFR-01 (support) | Standard ASP.NET Core `/health/live` (process) and `/health/ready` (DB-backed) endpoints, unversioned, minimal-detail JSON response. |
