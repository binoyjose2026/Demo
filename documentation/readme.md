# Documentation Index

This is the top-level index for the `documentation/` tree. It is a fast-navigation
map, not a re-explanation of the content — for the fuller narrative (project intent,
time budget, every link in one place) see
[`00-getting-started/01-start/readme.md`](00-getting-started/01-start/readme.md).

## Phase structure

Documentation is organized into four numbered phases that read in order:
`00-getting-started` orients a new reader (project intent, scope, and the master
link list); `01-requirements` traces the requirements as they evolved — the raw
client brief, an AI-assisted first pass into formal requirement documents, a
product-owner gap-analysis against those documents, and the answers that locked
scope (a planned "v2-requirements" fold-back was never done and is documented as
an intentional gap); `02-design` holds three successive design passes over the
same system — `v1` the baseline architecture, `v2` a hypothetical extreme-scale
exploration, and `v3-mvp` the trimmed-down design that was actually built and
shipped; `03-wrap-up` captures post-delivery cleanup notes. The phases are not
drafts superseding one another — each is kept in full because it answers a
different question.

## 00 — Getting started

- [Full index / start here](00-getting-started/01-start/readme.md) — project intent, requirements approach, and every important link in one place.
- [In scope — v1 decisions](00-getting-started/02-in-scope/01-summary.md)
- [Out of scope — v1 decisions](00-getting-started/03-out-of-scope/01-summary.md)

## 01 — Requirements

Client brief → formal requirements → gap-analysis → answers:

- [Client brief, as received (app, functional)](01-requirements/v0-received-as-is/app/requirement.app.functional.md) · [non-functional](01-requirements/v0-received-as-is/app/requirement.app.non-functional.md)
- [Client brief, as received (project/process, functional)](01-requirements/v0-received-as-is/project/requirement.project.functional.md) · [non-functional](01-requirements/v0-received-as-is/project/requirement.project.non-functional.md)
- [Product-owner gap-analysis report (37 open questions)](01-requirements/v1-requirements/agents/review-agent/review/01-report.md)
- [Answers to the open questions (locks v1 scope)](01-requirements/v1-requirements/agents/review-agent/review/02-answer.md)
- [v2-requirements — intentional placeholder](01-requirements/v2-requirements/readme.txt) — documents a gap: folding the v2 design review's findings back into a formal requirements update was never done, out of scope for this delivery.

## 02 — Design

- [Design folder overview](02-design/readme.txt) — how v1/v2/v3-mvp relate.
- [v1 — baseline architecture](02-design/v1/design/) — a complete, reasonably scoped design for the core functions (create, fetch, analytics) and standard non-functional concerns (security, scalability, performance, resilience, reliability/availability, testing). See the [full link list](00-getting-started/01-start/readme.md#v1-design-the-baseline-architecture).
- [v2 — extreme-scale exploration](02-design/v2/design/considerations/) (~28 documents) — a hypothetical "what would this require at extreme scale" review. Reference architecture only, **not what was built**. Best read via the [browsable client site](../client-deliverables/website/index.html).
- [v3-mvp — the actual shipped design](02-design/v3-mvp/design/) — what was really built:
  - [API design (the two shipped endpoints)](02-design/v3-mvp/design/api-design.md)
  - [Database design](02-design/v3-mvp/design/db-design.md)
  - [API project structure (layered solution)](02-design/v3-mvp/design/api-project-structure.md)
  - [Exception & logging strategy](02-design/v3-mvp/design/exception-and-logging-strategy.md)

## 03 — Wrap-up

- [Cleanup pass instructions](03-wrap-up/cleanup-agent/agents/agent-prompt.md) — post-delivery notes (broken links, relative paths) from the final documentation pass.

## The working code

The implementation lives in [`../src/`](../src/) (solution file `src/UrlShortener.sln`),
following the layout documented in
[`02-design/v3-mvp/design/api-project-structure.md`](02-design/v3-mvp/design/api-project-structure.md).
