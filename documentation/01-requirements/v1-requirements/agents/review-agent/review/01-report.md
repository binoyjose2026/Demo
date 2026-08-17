# Product/Business Owner Review — URL Shortener v1 Requirements

**Reviewer role:** Business Owner / Product Owner
**Reviewed documents:**
- `requirement.app.functional.md` (v1, Draft) — AF-01 through AF-10
- `requirement.app.non-functional.md` (v1, Draft) — ANFR-01 through ANFR-10

**Purpose of this review:** These v1 documents were elaborated by an engineer from a category-level brief ("core APIs, analytics, and reliability features"). That means most of what's in them are reasonable *engineering* defaults, not decisions a business/product owner actually made. This review does not evaluate technical correctness (a technical review is planned separately and is out of scope here). It only surfaces the product-level questions that must be answered — by the business, not by engineering judgment — before a v2 requirements set can be considered complete.

Every item below is phrased as a question for the business/product owner. No solutions or technical approaches are proposed.

---

## A. Users & Ownership

1. Who is the intended user of this product — the general public (anonymous), authenticated end users, internal employees only, or a mix? None of AF-01–AF-10 specify a user/account model at all.
2. Should the product support user accounts/sign-in at all, or is it intentionally anonymous-only (like a bare-bones public shortening tool)?
3. If accounts exist, should a short URL have an "owner"? Today AF-05 (metadata retrieval) and AF-07 (removal/deactivation) don't say who is allowed to retrieve or remove a link — is that open to anyone who knows the short code, or restricted to the creator?
4. If ownership exists, should users be able to see a list of "my links" and "my analytics" (AF-10), or is analytics retrieval (AF-10) intended to be open to anyone with the short code?
5. Is there a need for team/organization-level ownership (e.g., multiple people managing the same set of links), or is ownership strictly individual?
6. Are there different classes of users with different privileges (e.g., regular user vs. administrator who can moderate/remove any link)? AF-07 mentions removal/deactivation but not by whom.

## B. Link Lifecycle

7. Can the original long URL behind an existing short code ever be edited after creation, or is a short code permanently bound to the URL it was created with (per ANFR-02, "shall consistently resolve to the same original URL")? If editing should be possible, does that conflict with ANFR-02 and need a business decision on which wins?
8. Do short URLs expire by default, or only when the creator explicitly sets an expiration? AF-06 mentions an "expired" response but no document defines whether expiration exists as a feature, who sets it, or what the default lifetime is.
9. If default expiration exists, what should that default duration be, and can it vary (e.g., different defaults for anonymous vs. authenticated users)?
10. When a link is expired, deactivated (AF-07), or removed, what should the end user (the person who clicked the short link) actually see — is a specific branded landing/error message required, or is a generic error acceptable? This is a user-experience/brand decision, not just an API response code.
11. Once removed/deactivated (AF-07), can a short code ever be reused for a different long URL, or is it retired permanently? Reuse has business implications (old links pointing to new, unintended content).
12. Is there a "soft delete" business requirement (e.g., link owner can restore a deactivated link within some window), or is deactivation final?

## C. Limits & Abuse Policy

13. Should there be a maximum number of short URLs a single user (or anonymous source) can create, and if so, what is that limit — is it the same for all users or tiered?
14. AF-03 requires rejecting "malformed or invalid" URLs — does the business want any additional input restrictions, such as a maximum original-URL length, or restrictions on certain URL schemes/protocols?
15. ANFR-09 requires rate limiting on URL creation, but does not state the actual limit. What request volume is acceptable per user/IP, and is that a number the business needs to set (e.g., for cost control or fair use), or is it purely an engineering/operational default?
16. When a user exceeds a limit (creation limit or rate limit), what should they experience — silent throttling, an explicit error with an explanation, an upgrade prompt, or something else? This is a product/UX decision, not just a status code.
17. Is there a requirement to prevent shortening of URLs pointing to malicious, illegal, adult, or otherwise disallowed content (e.g., malware, phishing, spam)? None of AF-01–AF-10 mention content moderation.
18. If content moderation/abuse policy is required, who owns that policy and its enforcement (e.g., automated blocklist, manual review, third-party reputation service, user reporting)? This is a business governance decision.
19. Is there a requirement to allow the public to report abusive/malicious short links, and what is the business's expected response process?

## D. Custom Aliases & Branding

20. Should users be able to choose a custom "vanity" short code (e.g., `short.ly/my-brand`) instead of only receiving a system-generated one? AF-01/AF-04 only describe system-generated codes.
21. If custom aliases are supported, are there naming rules the business wants enforced (minimum/maximum length, disallowed characters, reserved words, profanity filtering, trademark/impersonation concerns)?
22. Is there a requirement for custom/branded domains (e.g., a company's own domain instead of a shared shortener domain), or is a single shared domain acceptable for all users?
23. Is there a requirement for QR code generation or link-preview (title/thumbnail) features associated with a short link? These are common product differentiators not mentioned anywhere in v1.

## E. Analytics & Reporting

24. AF-09/AF-10 define only total click count and last-accessed time. Does the business need richer analytics — e.g., clicks over time (trend chart), referrer source, device/browser type, geographic breakdown, or unique vs. repeat visitors?
25. Who is the actual consumer of analytics data — the link creator, an internal business/marketing team, or both? This affects what "exposing an API" (AF-10) needs to support (e.g., a dashboard UI vs. raw API access).
26. Is there a requirement to export analytics data (e.g., CSV/report download) for external reporting, or is on-screen/API access sufficient?
27. Is there a data retention policy the business wants for click/access event records (AF-08) — kept forever, or purged/aggregated after some period? This is a business decision with cost and compliance implications, not purely a technical storage question.

## F. Business Model & Product Surface

28. Is this product free to use, or is a pricing/tiering model expected (e.g., free tier with limits, paid tier with higher limits or extra features)? Nothing in v1 addresses monetization, and it directly affects the limits discussed in section C.
29. Is this an internal tool for the business's own use, a product offered to external customers, or both? This changes the priority of almost every other open question (accounts, abuse policy, SLAs, branding).
30. Does the business require a user-facing web interface (a page where a person can paste a URL and get a short link, view their links, see analytics), or is this intended to be a pure API product consumed by other systems/developers? Neither document addresses whether a UI is in scope at all.
31. If a web UI is required, is a public API (for third-party/developer integration) also required, and does it need its own access control (e.g., API keys)?

## G. Compliance & Privacy

32. Does the business have any compliance obligations to satisfy (e.g., GDPR, CCPA, or other regional privacy regulation) given that click events (AF-08) may capture visitor data such as IP address or location?
33. Is there a requirement to avoid storing personally identifiable information (PII) in click/access logs, or is PII collection acceptable/desired for analytics purposes? This is a policy decision that determines what AF-08/AF-09/AF-10 are even allowed to capture.
34. Does the business need a published privacy policy or user consent mechanism (e.g., cookie/tracking consent) for end users who click short links, given click tracking is a core feature?

## H. Service Level Expectations

35. ANFR-01/ANFR-05/ANFR-06 describe "highly available," "low-latency," and "scale to high volume" in qualitative terms only. Does the business have a specific uptime target (e.g., 99.9%) or response-time expectation it needs met, or is a general best-effort standard acceptable? Setting the actual target is a business/customer-commitment decision, not an engineering default.
36. Is there a business expectation for customer/user support (e.g., support channel, response time) if something goes wrong with a link, or is this out of scope for v1?
37. Is there any business requirement for a public status page or uptime communication to users?

---

## Summary

**Total open questions identified: 37**, across 8 categories (Users & Ownership, Link Lifecycle, Limits & Abuse Policy, Custom Aliases & Branding, Analytics & Reporting, Business Model & Product Surface, Compliance & Privacy, Service Level Expectations).

## Top Priority Questions (must be resolved before v2)

These are the questions that most affect the shape of every other requirement, so resolving them first will unblock the rest:

1. **(A1/A2)** Who are the users — anonymous public, authenticated accounts, internal-only, or a mix — and is a login/account model in scope at all?
2. **(F29)** Is this product for internal business use, an external customer-facing product, or both? This single answer reframes accounts, abuse policy, branding, and SLAs.
3. **(F28)** Is there a pricing/tiering model, or is the service entirely free? This directly determines what the usage limits (C13/C15) should be.
4. **(B7/B8)** Can the original URL behind a short code be edited after creation, and does expiration exist by default or only when explicitly requested — and if so, what's the default duration?
5. **(C17/C18)** Does the business require a content moderation/abuse policy for what URLs may be shortened, and who owns enforcing it?
6. **(F30)** Does the business require a user-facing web UI, or is this strictly an API product?
7. **(G32/G33)** Are there compliance/privacy obligations (e.g., PII handling, GDPR) governing what click-tracking data (AF-08) may be collected and retained?
8. **(H35)** What specific uptime/response-time targets does the business require, versus the qualitative "highly available / low-latency" language currently used?
