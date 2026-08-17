# v1 → v2 Requirements — Open Questions & Answers

**Source:** Open questions from `01-report.md` (Business/Product Owner review of `requirement.app.functional.md` / `requirement.app.non-functional.md`).
**How to use this document:** Each question includes a recommended answer (engineer's suggestion, scoped for a prototype delivered in 2-3 days) and a blank "Your answer" line. Fill in "Your answer" to confirm, override, or replace the recommendation — whatever you leave blank will default to the recommendation when v2 requirements are written.

---

## A. Users & Ownership

**1.** Who is the intended user — general public (anonymous), authenticated end users, internal employees only, or a mix?
- **Recommended:** A mix — anonymous shortening for the core flow, with optional accounts for users who want to manage/view their own links. Matches how most real-world shorteners behave and lets both flows be demonstrated.
- **Binoy Jose: only authenticated user can create. If the document is public, anyone can access :**

**2.** Should the product support user accounts/sign-in at all, or is it intentionally anonymous-only?
- **Your answer:** Yes the application supports user athentication. However the authentication and authorization is out of scope for this project

**3.** Should a short URL have an "owner," and if so, who can retrieve/remove it?
- **Your answer: Yes the Url will have a a group of onwers based on the department. However athorization is out of scope**

**4.** Should "my links"/"my analytics" be restricted to the owner, or open to anyone with the short code?
- **Your answer: The deperatment and super admin roles can see the analytics. However it is out of scope for the PoC**

**5.** Is team/organization-level ownership needed, or is ownership strictly individual?
- **Your answer: Yes. However out of scope**

**6.** Are there different user privilege classes (e.g., admin who can moderate/remove any link)?
 - **Binoy's answer: Role based: Out of scope for initial release**

## B. Link Lifecycle

**7.** Can the original long URL behind an existing short code be edited after creation?
- **Binoy's answer:** No — a short code stays permanently bound to the URL it was created with. Preserves trust/predictability; a new destination gets a new link.
- **Your answer:**

**8.** Do short URLs expire by default, or only when explicitly set?
- **Binoy's answer:** No expiration by default; expiration is opt-in at creation time.
- **Your answer:**

**9.** If default expiration exists, what should the default duration be?
- **Binoy's answer:** N/A under the above default (no default expiry). If an expiry is set, allow up to a defined maximum (e.g., 1 year) — exact ceiling is a placeholder pending confirmation.
- **Your answer:**

**10.** What should a visitor see when a link is expired, deactivated, or removed?
- **Binoy's answer:** A simple, branded "this link has expired or was removed" page rather than a raw error/404 — small UX cost, real trust benefit.

**11.** Once removed/deactivated, can a short code be reused for a different URL?
- **Binoy's answer:** No — retire it permanently. Reuse risks sending old traffic to unintended content.

**12.** Is a "soft delete"/restore window required, or is deactivation final?
- **Binoy's answer:** Out of scope for v1 — deactivation is final. Note as a possible v2 feature.

## C. Limits & Abuse Policy

**13.** Should there be a max number of short URLs per user/anonymous source?
- **Binoy's answer:** Yes — some ceiling should exist (e.g., anonymous limited by IP rate, authenticated given a daily cap). Exact numbers are placeholders pending real usage data; the policy itself (a ceiling exists) should be confirmed now. 

**14.** Are additional input restrictions needed (max URL length, disallowed schemes)?
- **Binoy's answer:** Yes — restrict to `http`/`https` only (block `javascript:`, `file:`, `data:`, etc. for security) and cap URL length at a practical limit (e.g., 2048 characters).

**15.** What request volume should rate limiting (ANFR-09) actually allow?
- **Binoy's answer:** Treat as an engineering/operational default, not a hard business commitment — revisit if real abuse is observed.

**16.** What should a user experience when they exceed a limit?
- **Binoy's answer:** An explicit error explaining the limit and reason — not silent throttling. Transparency matters more than smoothing it over.

**17.** Is content moderation required (block malicious/phishing/illegal-content URLs)?
- **Binoy's answer:** Yes, at a minimal level — an automated malicious/phishing domain check at creation time. Full manual moderation is out of scope for v1.

**18.** Who owns the moderation policy and its enforcement?
- **Binoy's answer:** An automated blocklist/reputation-service check is sufficient for v1; no manual review team. Formalize ownership if usage grows.

**19.** Should the public be able to report abusive/malicious links?
- **Binoy's answer:** Out of scope for v1; note as a future enhancement.

## D. Custom Aliases & Branding

**20.** Should users be able to choose a custom "vanity" short code?
- **Binoy's answer:** Yes — support an optional custom alias alongside system-generated codes. High value, low complexity.

**21.** If custom aliases are supported, what naming rules should apply?
- **Binoy's answer:** Yes, enforce rules — min/max length, an allowed character set (alphanumeric + hyphen), and a basic reserved-word/profanity blocklist.

**22.** Are custom/branded domains required?
- **Binoy's answer:** Out of scope for v1 — a single shared domain is sufficient.

**23.** Are QR codes or link previews required?
- **Binoy's answer:** QR code generation is a low-effort addition worth including if time allows. Link preview (title/thumbnail) is out of scope for v1 — meaningfully higher complexity.

## E. Analytics & Reporting

**24.** Does the business need richer analytics beyond total clicks (trend over time, referrer, device, geography)?
- **Binoy's answer:** Out of scope for v1 — keep only total click count (AF-09/AF-10 as-is). Richer analytics (trend, referrer, device, geography) documented here as a future/v2 enhancement, not built now.

**25.** Who is the actual consumer of analytics data?
- **Binoy's answer:** The link creator only, via the same API/dashboard used to manage links — no separate internal BI/marketing consumer for v1.
 

**26.** Is exporting analytics data (e.g., CSV) required?
- **Binoy's answer:** Out of scope for v1 — on-screen/API access only. Reasonable v2 addition.
- **Your answer:**

**27.** What data retention policy should apply to click/access event records?
- **Recommended:** Retain raw click events for a bounded period (e.g., 90 days), then aggregate or purge — balances usefulness against storage/compliance cost. Exact window is a placeholder for confirmation.

## F. Business Model & Product Surface

**28.** Is this product free, or is a pricing/tiering model expected?
- **Binoy's answer:** Free/unmetered for v1 — this is a prototype, not a commercial launch. Any usage caps are abuse-prevention limits, not tier gates.
- **Your answer:**

**29.** Is this an internal tool, an external customer-facing product, or both?
- **Binoy's answer:** Treat as a general-purpose, public-facing utility — the most demonstrative and realistic scope for this exercise. This is the single highest-impact answer in the whole list; please confirm or override deliberately.
- **Your answer:**

**30.** Does the business require a user-facing web UI, or is this a pure API product?
- **Binoy's answer:** Out of scope for v1 — API-only. No web UI.

**31.** If a web UI is required, is a public API with its own access control (API keys) also required?
- **Binoy's answer:** No / out of scope for v1 — no separate API-key access scheme. (Consistent with Q30: no external-facing surface beyond the core endpoints for v1.)

## G. Compliance & Privacy

**32.** Does the business have compliance obligations (GDPR, CCPA, etc.) given click tracking?
- **Binoy's answer:** Out of formal scope for v1 (no regulatory certification target), but apply privacy-conscious defaults regardless (see Q33).
- **Your answer:**

**33.** Should PII be avoided in click/access logs?
- **Binoy's answer:** Yes — avoid storing raw IP/PII by default; capture only non-identifying, aggregable data (timestamp, coarse region if needed, referrer, device type).
- **Your answer:**

**34.** Is a privacy policy or consent mechanism required for end users who click short links?
- **Binoy's answer:** Out of scope for v1, provided no PII/tracking cookies are collected per Q33. Revisit if that changes.
- **Your answer:**

## H. Service Level Expectations

**35.** What specific uptime/response-time targets does the business require?
- **Binoy's answer:** No formal contractual SLA for v1; design to the qualitative targets already stated in `requirement.app.non-functional.md` (e.g., low-latency redirect, high availability) as a best-effort standard.
- **Your answer:**

**36.** Is a formal support channel/response-time expectation required?
- **Binoy's answer:** Out of scope for v1 — note as a future/production consideration.
- **Your answer:**

**37.** Is a public status page or uptime communication required?
- **Binoy's answer:** Out of scope for v1.
- **Your answer:**

---

## Notes

- Recommendations are scoped for a prototype delivered over 2-3 days (per the project's own timeline constraint), favoring the simplest option that still produces a realistic, demonstrable product — not necessarily what a fully funded commercial launch would choose.
- Any "Your answer" left blank is treated as accepting the recommendation when v2 requirement documents are written.
- Question numbers (1-37) and categories (A-H) match `01-report.md` exactly for traceability.
