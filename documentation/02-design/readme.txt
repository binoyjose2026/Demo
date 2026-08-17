This is the readme for the "02-design" folder.

It contains three successive design passes over the same URL-shortener system,
each with a different purpose. They are not drafts of one another — each one
is kept in full because it answers a different question:

- v1/       The baseline architecture. A complete, reasonably scoped design
            covering the core create/fetch/analytics functions and the usual
            non-functional concerns (security, scalability, performance,
            resilience, reliability/availability, testing).

- v2/       A hypothetical "what would extreme scale require" exploration —
            28 documents produced by reviewing the v1 design against
            a deliberately aggressive scale target (millions of creates/day,
            tens to hundreds of millions of fetches/day, five-year horizon).
            This is a design/thinking exercise, NOT what was built. Treat it
            as a reference architecture for "if this had to become a
            hyperscale product," not as the delivered system.

- v3-mvp/   The actual shipped MVP — a trimmed-down design covering only
            what was realistically buildable in the time available (create +
            fetch), with every deferred v1/v2 feature called out explicitly
            rather than silently dropped. This is what was actually built.

If you only have time to read one of the three, read v3-mvp — it describes
the real, running system. v1 and v2 exist to show the design thinking behind
it and how far the architecture could scale if it ever needed to.

See also: `../00-getting-started/01-start/readme.md` for the full set of
links across all three phases.
