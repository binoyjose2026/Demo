# Functional Requirements — URL Shortener Application

**Version:** v1
**Status:** Draft
**Source:** client-provided project assessment document (Scenario section) + engineer elaboration
**Scope note:** This document defines what the URL shortener application itself must do. Companion to `../project/requirement.project.functional.md`, which covers the engineering process used to build it.

## 1. Purpose

Defines the functional (behavioral) requirements of the URL shortener application.

## 2. Note on Elaboration

The source brief specifies the application only at category level: *"a URL shortener service from scratch with core APIs, analytics, and reliability features"* (Scenario). It does not enumerate individual application features. The requirements below elaborate each category into concrete, industry-standard URL-shortener functionality, consistent with the brief's own Core Requirement 1 ("Requirement Understanding: interpret intent... normalize into a clear engineering problem").

- **[Source]** — the category is stated directly in the brief.
- **[Elaborated]** — the specific requirement fills a gap the brief leaves open; it is engineer judgment, not verbatim source text.

## 3. Core APIs — `[Source: "core APIs"]`

- **AF-01 [Elaborated]:** Accept a long/original URL and generate a corresponding short URL.
- **AF-02 [Elaborated]:** Redirect requests for a valid short URL to its original long URL.
- **AF-03 [Elaborated]:** Validate submitted URLs and reject malformed or invalid input.
- **AF-04 [Elaborated]:** Generate a unique short code for each shortened URL and handle code collisions.
- **AF-05 [Elaborated]:** Allow retrieval of metadata for a given short URL (e.g., original URL, creation date, status).
- **AF-06 [Elaborated]:** Return a defined not-found/expired response when a short code does not exist or is no longer valid.
- **AF-07 [Elaborated]:** Support removal/deactivation of a previously created short URL.

## 4. Analytics — `[Source: "analytics"]`

- **AF-08 [Elaborated]:** Record an access event each time a short URL is resolved.
- **AF-09 [Elaborated]:** Track the total access/click count for each short URL.
- **AF-10 [Elaborated]:** Expose an API to retrieve analytics for a given short URL (e.g., click count, last accessed).

## 5. Traceability

Grounded in: Scenario — *"core APIs, analytics, and reliability features."* Category-level scope (Core APIs, Analytics) is from source; feature-level detail (AF-01–AF-10) is engineer elaboration, not verbatim source text.
