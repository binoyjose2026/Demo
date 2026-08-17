# Out of Scope — v1 Requirement Decisions

**Source:** `01-requirements/v1-requirements/agents/review@agent/review/02-answer.md` (confirmed product-owner answers to the gap-analysis report).
**Purpose:** Quick-reference summary of what has been explicitly decided as **out of scope for v1**. Each item cites the question number it was decided in (Q#) and, where applicable, the requirement ID(s) it constrains.

## A. Users & Ownership

- Full authentication/authorization implementation (login system, role/permission enforcement) — not built for this PoC; an authenticated user context is assumed. *(Q2, Q3)*
- Department/super-admin role-based access to analytics. *(Q4)*
- Team/organization-level (multi-owner) link ownership. *(Q5)*
- Role-based privilege classes / admin moderation roles. *(Q6)*

## B. Link Lifecycle

- Soft delete / restore window for deactivated links — deactivation is final. *(Q12)*

## C. Limits & Abuse Policy

- Manual content moderation / human review team — only an automated malicious/phishing check is in scope. *(Q17, Q18)*
- Public abuse-reporting flow. *(Q19)*

## D. Custom Aliases & Branding

- Custom/branded domains — a single shared domain only. *(Q22)*
- Link preview (title/thumbnail) generation. *(Q23)*
- QR code generation — optional/stretch only, not a committed v1 deliverable. *(Q23)*

## E. Analytics & Reporting

- Richer analytics: click trends over time, referrer, device, geography — total click count only for v1 (AF-09/AF-10 as-is). *(Q24)*
- Analytics export (CSV/report download). *(Q26)*

## F. Business Model & Product Surface

- Pricing/tiering/billing system — v1 is free/unmetered. *(Q28)*
- User-facing web UI — API-only for v1. *(Q30)*
- Public API with API-key access control for third-party/developer use. *(Q31)*

## G. Compliance & Privacy

- Formal regulatory compliance program (e.g., GDPR/CCPA certification). *(Q32)*
- Published privacy policy / consent (cookie/tracking) mechanism. *(Q34)*

## H. Service Level Expectations

- Formal contractual SLA (specific uptime/response-time commitment). *(Q35)*
- Formal customer/user support channel. *(Q36)*
- Public status page / uptime communication. *(Q37)*

## Pending / Not Yet Confirmed

- **Data retention policy for click/access event records** — still shown as "Recommended" (not confirmed) in `02-answer.md` Q27. Not treated as decided either way.
