# Consideration 30 — Localization & Custom Error Messages: Out of Scope for This Review

**Version:** v2 (scalability review)
**Status:** Scope declaration — not a design document
**Traceability:** `agent-prompt.md` (review scope: Scalability)

---

## 1. What Is Excluded, and Why

`agent-prompt.md` states its own scope explicitly: *"Scope of the review: Scalability."*

Two related but distinct feature areas are **explicitly out of scope for this v2 review**:

- **Internationalization / localization (i18n/l10n)** — multi-language UI text, translated API error messages, locale detection, and per-language resource files.
- **Custom / configurable error messages** — allowing the message text returned to end users to be edited, branded, or made tenant-specific (e.g., a business-configurable error-message catalog or per-tenant custom messaging) rather than fixed in code.

Both are product/UX concerns about *what text a human reads*, not about *how the system scales under load*. They are orthogonal to throughput, latency, availability, and capacity — the axes this review is scoped to — the same reasoning `27-devops-out-of-scope.md` used to exclude DevOps as a separate concern from diluting a scalability-focused review. Neither is designed here or elsewhere in this document set.

## 2. What Would Fall Under a Future Review (Not Designed Here)

If this is picked up later, it would cover items such as:

- Resource-file (`.resx`) organization and translation workflow per supported language
- Locale detection/negotiation (Accept-Language header, user preference, etc.)
- Locale-aware formatting (dates, numbers, timezones)
- A business-configurable error-message catalog (who can edit messages, where they're stored, versioning)
- Per-tenant custom/branded messaging

None of these are analyzed, designed, or recommended in this document. This is a bounded exclusion list, not a preview of the answers.

## 3. Current State (for Context Only)

The API today returns fixed, English-only `ProblemDetails` error responses — the message text is hard-coded, not localized and not configurable. If localization is ever required, ASP.NET Core's built-in `IStringLocalizer` / `.resx` resource-file mechanism is the standard extension point it would plug into; that is noted here only as a forward-pointer, not as a design.

## 4. Recommendation

Treat localization and custom/configurable error messaging as their own separate, scoped review if and when they become an actual product requirement, rather than folding them into this scalability review, consistent with `agent-prompt.md`'s own pattern of limiting each review pass to a bounded set of items.
