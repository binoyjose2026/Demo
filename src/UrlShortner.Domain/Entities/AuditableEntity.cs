namespace UrlShortner.Domain.Entities;

/// <summary>
/// Base class providing the standard Id, audit, concurrency, and soft-delete columns
/// that every table in this project must have.
/// See: global/guidelines/data-design-guidelines.md, "Standard Column Set".
/// </summary>
public abstract class AuditableEntity
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? LastModifiedAtUtc { get; set; }

    public string? LastModifiedBy { get; set; }

    /// <summary>
    /// App-maintained concurrency token, incremented on every update. SQLite has no
    /// native auto-updating ROWVERSION, so this is maintained explicitly by AppDbContext.
    /// See: data-design-guidelines.md §4.
    /// </summary>
    public long RowVersion { get; set; }

    /// <summary>
    /// Soft-delete flag. No MVP endpoint sets this today (delete/deactivate is out of
    /// scope for the create/fetch-only MVP), but the column exists per the standing
    /// convention so the schema does not need to change when AF-07 (deactivation) is
    /// implemented. See: data-design-guidelines.md §5, documentation/02-design/v1/design/fn-fetch.md §7.2.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
}
