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
`src/UrlShortener.sln`, following the layered structure documented in
[`documentation/02-design/v3-mvp/design/api-project-structure.md`](documentation/02-design/v3-mvp/design/api-project-structure.md).
See that folder's own `README.md` for how to run it locally.

## Repository layout

```
documentation/          Requirements and design docs (v0 -> v1 -> v2 -> v3), numbered by phase
client-deliverables/    Client-facing HTML pages (engineering approach, v2 review site)
engineering-standards/  Cross-cutting coding/design/data guidelines followed throughout
src/                    The working .NET solution (the actual shipped MVP)
```

## A few reflections on how this project was approached

Beyond the code and the design trail itself, a few notes on the thinking behind
how this project was structured and delivered, offered in the spirit of full
disclosure rather than as a sales pitch.

**On simplicity versus over-engineering.** The [v2 "extreme scale" review](client-deliverables/website/index.html)
deliberately explores the opposite extreme from v1's simplicity — but it's worth
pointing out that several of its own conclusions demonstrate restraint, not just
exploration for its own sake. Two of the largest, most "enterprise" pieces of
infrastructure it considers — Kafka and the [Outbox pattern](documentation/02-design/v2/design/considerations/20-outbox-pattern.md)
— are both explicitly evaluated and *rejected* as unnecessary complexity for a
system with this shape. The [Kafka comparison](documentation/02-design/v2/design/considerations/05-kafka-comparison.md)
walks through the actual projected throughput, finds it comfortably within reach
of far simpler managed queues, and concludes Kafka only earns its keep once
there's a genuine need for event replay or a growing set of independent
long-lived consumers — not by default. The Outbox analysis reaches a similarly
grounded conclusion: the one write that must never be lost (the short URL
mapping itself) is already a single atomic database write with no dual-write
exposure, so the pattern is deferred until a future event actually carries
consequences serious enough to justify it. A simple URL shortener isn't a
multi-service distributed-transaction problem unless other independent systems
are genuinely working in tandem with it — and neither document pretends
otherwise just because the exercise was framed around extreme scale. That's the
design principle I tried to hold throughout: keep things as simple as possible,
but as complicated as necessary — no simpler, and no more complicated, than the
problem actually demands.

**The v1 → v2 → v3 arc was a deliberate methodology, not scope creep.** v1 is
intentionally scoped simply, matched to the known and fairly narrow requirements
in front of it. v2 then deliberately pushes to the opposite extreme — a
comprehensive "what would this require at extreme scale" exploration — not
because that scale was ever the actual target, but so the trade-offs involved
could be reasoned through explicitly, with real numbers, rather than guessed at
later under pressure. v3 (the MVP that actually shipped, in `src/`) is where
those two passes get reconciled: it tallies the v2 exploration against the
real, stated requirements and normalizes the result back down to a genuine
minimum viable solution. That's not the same as ignoring what v2 found — it's
consciously choosing not to build what isn't yet needed, while leaving honest,
documented seams behind: commented-out code, placeholder attributes, and
cross-references back to the fuller designs, so the thinking is deferred rather
than lost.

**On methodology — AI-assisted versus AI-agentic.** I approached this as more
than a coding exercise; fundamentally, it's a demonstration of using AI
effectively in software engineering, and the [engineering approach](client-deliverables/approach/developer-approach.html)
write-up goes into that in more depth. Given that framing, I deliberately chose
an **AI-assisted** approach — a human directing, reviewing, and correcting AI
output at every step — over a fully autonomous **AI-agentic** approach, where an
agent operates end-to-end with minimal human touchpoints (the "Agentic SDLC"
pattern, with automated multi-agent review and deployment, is the clearest
example of that direction). In a more stable, less time-constrained setting, a
more autonomous agentic pipeline would be the natural next evolution of this
same workflow. The choice here was driven by the time available for this
exercise, not by any belief that agentic approaches are the wrong tool.

**Honest disclosure of what's deliberately left out of the MVP.** The shipped
code in [`src/`](src/) is deliberately minimal by design, not because further
refinements weren't considered — and I'm glad to walk through any of them in a
follow-up discussion. On the smaller end, `ValidationAppException.FieldName`
is currently a bare `string?` rather than a proper enum or strongly-typed
identifier for the small, fixed set of fields it can represent — an easy
follow-up refinement. The MVP's authentication is also a named, in-code
placeholder (`MvpPlaceholderAuthenticationHandler`) that always authenticates
callers as a single fixed identity so the `[Authorize]` policy shape is real
even though no actual credential validation happens yet — a real scheme (JWT
bearer, API key, or similar) is meant to slot in behind that same abstraction
without touching the controller. On the larger end, error handling today is a
single exception-type switch in `GlobalExceptionHandler` mapping known
exception types to `ProblemDetails` — functional and consistent, but short of
a more comprehensive, structured error-handling framework (a proper error-code
catalog, richer client-facing error taxonomy) that a longer-lived system would
eventually want.
