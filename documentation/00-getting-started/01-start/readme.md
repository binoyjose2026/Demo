# Getting Started

**Author:** Binoy Jose
**Purpose:** Create design specs and document the gaps
**Time allocated:** 3-4 hours

## Project Intent
Not a full enterprise-grade, scalable build (that could take days/months) — goal is a basic app plus documented design considerations.

## Requirements Approach
Client provided basic requirements. Instead of iterating through clarification channels, the approach is to list open questions and answer them via documented assumptions.

- Requirements & their evolution: `C:\BinoyJoseOfficial\InterviewDemo\UrlShortner\documentation\01-requirements`
- In scope: `C:\BinoyJoseOfficial\InterviewDemo\UrlShortner\documentation\00-getting-started\in-scope`
- Out of scope: `C:\BinoyJoseOfficial\InterviewDemo\UrlShortner\documentation\00-getting-started\out-of-scope`

## Important Links

### Start here
- [Engineering approach — why design got most of the time, and MVP shipped minimal](../../docs/approach/developer-approach.html)
- [v2 extreme-scale design review (28 documents, browsable site)](../../docs/website/index.html)

### Global guidelines
- [Coding guidelines (C#/.NET)](../../global/guidelines/coding-giudelines.md)
- [Data design guidelines (SQLite/EF Core, audit fields, RowVersion)](../../global/guidelines/data-design-guidelines.md)
- [Architecture/design guidelines (layered solution, repository pattern, DI, SOLID)](../../global/guidelines/design-guidelines.md)

### Requirements
- [Functional requirements — app](../01-requirements/v0-received-as-is/app/requirement.app.functional.md)
- [Non-functional requirements — app](../01-requirements/v0-received-as-is/app/requirement.app.non-functional.md)
- [Functional requirements — project/process](../01-requirements/v0-received-as-is/project/requirement.project.functional.md)
- [Non-functional requirements — project/process](../01-requirements/v0-received-as-is/project/requirement.project.non-functional.md)
- [Product-owner gap-analysis report (37 open questions)](../01-requirements/v1-requirements/agents/review@agent/review/01-report.md)
- [Answers to the open questions (locks v1 scope)](../01-requirements/v1-requirements/agents/review@agent/review/02-answer.md)

### Scope
- [In scope — v1 decisions](./in-scope/01-summary.md)
- [Out of scope — v1 decisions](./out-of-scope/01-summary.md)

### v1 design (the baseline architecture)
- [Security](../02-design/v1/design/nfr-security.md) · [Scalability](../02-design/v1/design/nfr-scalability.md) · [Performance](../02-design/v1/design/nfr-performance.md) · [Resilience](../02-design/v1/design/nfr-resilience.md) · [Reliability & availability](../02-design/v1/design/nfr-reliability-and-availability.md)
- [Unit testing](../02-design/v1/design/nfr-unit-testing.md) · [Integration testing](../02-design/v1/design/nfr-integration-testing.md)
- [Create flow](../02-design/v1/design/fn-create.md) · [Fetch/redirect flow](../02-design/v1/design/fn-fetch.md) · [Analytics flow](../02-design/v1/design/fn-analytics.md)

### v2 design — "what would extreme scale require" (28 documents)
Best read via the [browsable site](../../docs/website/index.html) (key-decisions summary + full detail per topic); raw source lives in [`../02-design/v2/design/considerations/`](../02-design/v2/design/considerations/).

### v3 MVP — what was actually built and shipped
- [Database design](../02-design/v3.MVP/design/db-design.md)
- [API project structure (layered solution)](../02-design/v3.MVP/design/api-project-structure.md)
- [API design (the two shipped endpoints)](../02-design/v3.MVP/design/api-design.md)
- [Exception & logging strategy (Serilog, exception hierarchy)](../02-design/v3.MVP/design/exception-and-logging-strategy.md)
- [Working code](../../src/) — solution file: `../../src/UrlShortner.sln`
- [Test run logs (build/test/run output captured here)](../../src/test/logs/)

### Wrap-up
- [Cleanup pass instructions (broken links, relative paths)](../../wrap-up/cleanup-agent/cleanup@agent.md)
