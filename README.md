# URL Shortener

A URL-shortening service (create + fetch/redirect, with a documented design
trail behind it) built as a timeboxed engineering exercise: a few hours of
design work followed by a 1-hour MVP build.

If you're opening this project cold, start here:

## Start here

- **[Getting started](documentation/00-getting-started/01-start/readme.md)** — the
  full index: project intent, requirements, design docs, and the working code,
  all in one place.
- **[Engineering approach](client-deliverables/approach/developer-approach.html)** —
  a short, readable explanation of why most of the time went into design and
  the shipped code is a deliberately minimal MVP.
- **[v2 scalability design review](client-deliverables/website/index.html)** —
  a browsable site walking through ~30 documents on what this system would
  need if it had to handle extreme scale. See the note below before reading
  this as "the design" — it isn't what was built.

## The design trail: v0 -> v1 -> v2 -> v3

`documentation/` holds four successive passes over the same system. They are
not drafts superseding one another — each answers a different question, and
all four are kept for the full picture:

| Phase | What it is |
|---|---|
| **v0** | The raw client input, exactly as received, plus an AI-assisted first pass turning it into formal requirement documents. |
| **v1** | The baseline architecture design — a complete, reasonably scoped design for the core functions and standard non-functional concerns. |
| **v2** | A **hypothetical** "what would extreme scale require" exploration — millions of creates/day, hundreds of millions of fetches/day, five-year horizon. This is a reference/thinking exercise, **not what was built**. |
| **v3 (MVP)** | The **actual shipped system** — a trimmed-down design and implementation covering only create + fetch, with every deferred feature documented rather than silently dropped. |

**This distinction matters:** the v2 review is the largest single body of
documentation in this repo (~30 documents), which can make it look like the
delivered design. It isn't — v3 is what was actually built and shipped.

## The working code

The implementation lives in **`src/`** — an ASP.NET Core (.NET 9) solution,
`src/UrlShortner.sln`, following the layered structure documented in
[`documentation/02-design/v3.MVP/design/api-project-structure.md`](documentation/02-design/v3.MVP/design/api-project-structure.md).
See that folder's own `README.md` for how to run it locally.

## Repository layout

```
documentation/          Requirements and design docs (v0 -> v1 -> v2 -> v3), numbered by phase
client-deliverables/    Client-facing HTML pages (engineering approach, v2 review site)
global/                 Cross-cutting coding/design/data guidelines followed throughout
src/                    The working .NET solution (the actual shipped MVP)
```
