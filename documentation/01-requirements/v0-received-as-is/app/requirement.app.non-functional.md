# Non-Functional Requirements — URL Shortener Application

**Version:** v1
**Status:** Draft
**Source:** client-provided project assessment document (Scenario section) + engineer elaboration
**Scope note:** This document defines the quality attributes the URL shortener application itself must exhibit. Companion to `../project/requirement.project.non-functional.md`, which covers process-level quality/governance.

## 1. Purpose

Defines the non-functional (quality attribute) requirements of the URL shortener application.

## 2. Note on Elaboration

The source brief states only that the application must have *"reliability features"* (Scenario) — a category, not itemized attributes. The requirements below elaborate that category into concrete reliability, performance, security, and observability attributes standard for a URL-shortener service.

- **[Source]** — the category is stated directly in the brief.
- **[Elaborated]** — the specific requirement fills a gap the brief leaves open; it is engineer judgment, not verbatim source text.

## 3. Reliability & Availability — `[Source: "reliability features"]`

- **ANFR-01 [Elaborated]:** The redirect path shall be highly available, as it is the most frequently exercised operation.
- **ANFR-02 [Elaborated]:** A short code shall consistently resolve to the same original URL for the lifetime of that mapping.
- **ANFR-03 [Elaborated]:** URL mappings shall be durably persisted; no accepted mapping shall be lost due to a single component failure.
- **ANFR-04 [Elaborated]:** The service shall degrade gracefully on backend failure rather than corrupting or losing data.

## 4. Performance & Scalability

- **ANFR-05 [Elaborated]:** The redirect operation shall be low-latency, given redirect traffic is expected to significantly exceed URL-creation traffic.
- **ANFR-06 [Elaborated]:** The system shall be able to scale to handle high-volume read (redirect) throughput.

## 5. Security

- **ANFR-07 [Elaborated]:** Submitted URLs shall be validated/sanitized to reduce abuse risk (e.g., open-redirect misuse).
- **ANFR-08 [Elaborated]:** Short codes shall not be trivially sequential/enumerable.
- **ANFR-09 [Elaborated]:** The URL-creation endpoint shall be protected against abusive/excessive request volume (rate limiting).

## 6. Observability

- **ANFR-10 [Elaborated]:** Errors and latency on core operations shall be observable (logged/metriced) to support the reliability goal.

## 7. Traceability

Grounded in: Scenario — *"...and reliability features."* Category-level scope only; specific attributes (ANFR-01–ANFR-10) are engineer elaboration.
