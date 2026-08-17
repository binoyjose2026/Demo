namespace UrlShortener.Domain.Entities;

/// <summary>
/// A single short-code -> original-URL mapping.
/// AF-01/AF-02: the core entity behind create and fetch/redirect.
/// </summary>
public class ShortUrl : AuditableEntity
{
    /// <summary>The generated (or, in a future custom-alias feature, caller-supplied) short code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The original/long URL this code resolves to. Immutable after creation (Q7 in fn-create.md/fn-fetch.md).</summary>
    public string OriginalUrl { get; set; } = string.Empty;

    // Deliberately NOT included in this MVP entity (each is a documented, out-of-scope
    // seam rather than a silent omission):
    //   - ExpiresAtUtc (opt-in expiration)      -> documentation/02-design/v1/design/fn-create.md §8, fn-fetch.md §7.1
    //   - OwnerDepartmentId / CreatedBy identity -> documentation/02-design/v1/design/fn-create.md §4 (Q1-Q3, auth is mocked/out of scope)
    //   - Access/click count, LastAccessedAtUtc  -> documentation/02-design/v1/design/fn-analytics.md (AF-08/AF-09/AF-10)
    // IsDeleted/DeletedAtUtc are still present (inherited from AuditableEntity) for the
    // standard soft-delete convention, but no MVP code path sets them since AF-07
    // (deactivation) is out of scope for this MVP.
}
