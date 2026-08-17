# In Scope — v1 Requirement Decisions

**Source:** `01-requirements/v0-received-as-is/app/requirement.app.functional.md` & `requirement.app.non-functional.md` (baseline), plus confirmed answers in `01-requirements/v1-requirements/agents/review-agent/review/02-answer.md`.
**Purpose:** Quick-reference summary of what is confirmed **in scope for v1**. Each item cites the requirement ID(s) and/or question number (Q#) it comes from. Companion to `../out-of-scope/01-summary.md`.

## Baseline (from requirement.app.functional.md / requirement.app.non-functional.md)

- Core APIs: create short URL from a long URL, redirect short URL to original, validate/reject invalid input, generate a unique short code with collision handling, retrieve link metadata, return a defined not-found/expired response, remove/deactivate a link. *(AF-01–AF-07)*
- Analytics: record an access event per redirect, track total click count, expose an API to retrieve analytics for a link. *(AF-08–AF-10)*
- Reliability, performance/scalability, security, and observability attributes as documented. *(ANFR-01–ANFR-10)*

## A. Users & Ownership

- Link creation requires an authenticated user context. The authentication mechanism itself is out of scope/mocked for this PoC (see out-of-scope summary), but the requirement that a creator be "logged in" is in scope. *(Q1, Q2)*
- Links are conceptually owned by a department-level group. Enforcing that ownership (authorization) is out of scope, but the ownership model itself is in scope. *(Q3)*
- A link, once created, is accessible to anyone who has it (i.e., access to *use*/follow a link is not itself gated). *(Q1)*

## B. Link Lifecycle

- The original long URL behind a short code is immutable — no editing after creation. *(Q7)*
- Expiration is optional (opt-in at creation); there is no default expiry. If an expiry is set, it is capped at a placeholder maximum (e.g., 1 year, pending confirmation of the exact figure). *(Q8, Q9)*
- Expired, deactivated, or removed links show a simple branded message page rather than a raw error. *(Q10)*
- A deactivated/removed short code is retired permanently and never reused for a different URL. *(Q11)*

## C. Limits & Abuse Policy

- A usage ceiling exists on URL creation (different for anonymous vs. authenticated sources); exact numeric limits are still placeholders pending real usage data. *(Q13)*
- Input validation: only `http`/`https` schemes accepted; practical max URL length (~2048 characters). *(Q14)*
- When a limit is exceeded, the user receives an explicit error explaining the limit and reason — no silent throttling. *(Q16)*
- Minimal automated content-moderation check (malicious/phishing domain check) at link-creation time. *(Q17, Q18)*

## D. Custom Aliases & Branding

- Optional custom/vanity alias support at creation, alongside system-generated codes. *(Q20)*
- Alias naming rules are enforced: length limits, an allowed character set, and a basic reserved-word/profanity blocklist. *(Q21)*

## E. Analytics & Reporting

- Analytics scope is limited to total click count (per the baseline AF-09/AF-10); the consumer of that data is the link creator only. *(Q24, Q25)*

## F. Business Model & Product Surface

- v1 is free/unmetered — no billing or payment flow. *(Q28)*
- The product is a general-purpose, public-facing utility (not restricted to internal-only use). *(Q29)*

## G. Compliance & Privacy

- Privacy-conscious default: raw IP/PII is not stored in click logs; only non-identifying, aggregable data is captured (timestamp, coarse region if needed, referrer, device type). *(Q33)*

## H. Service Level Expectations

- The service is designed to the qualitative reliability/performance targets already stated in `requirement.app.non-functional.md` (e.g., low-latency redirect, high availability) as a best-effort standard — without a formal contractual SLA. *(Q35)*
