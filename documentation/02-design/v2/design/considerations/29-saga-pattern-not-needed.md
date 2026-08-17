# Consideration 29 — Why the Saga Pattern Is Not Needed for URL Creation

**Version:** v2 (extreme-scalability review)
**Status:** Draft
**Scope:** This document answers exactly one question — does the create-short-URL flow need the Saga pattern to coordinate its side effects? It builds on the same "don't over-engineer the create path" reasoning already established in `20-outbox-pattern.md` (which concluded the Outbox pattern is not needed for `UrlCreated`), and applies it to a different, larger pattern: distributed-transaction coordination via Sagas. Builds on the v1 create flow in `../../v1/design/fn-create.md` and the async event pipeline in `05-kafka-comparison.md`.

---

## 1. What the Saga Pattern Actually Solves

A **Saga** coordinates a single business transaction that spans **multiple independent services, each owning its own database**, where no distributed ACID transaction is available across them (or is deliberately avoided for availability/scalability reasons). The defining shape:

- The business operation is decomposed into a sequence of **local transactions**, one per participating service (e.g., Step 1 in Service A's database, Step 2 in Service B's database, Step 3 in Service C's database).
- Each step commits independently — there is no two-phase commit holding all of them open at once.
- If a later step fails, the earlier steps that already committed are **not automatically rolled back** by any database — because they live in different databases with no shared transaction log. Instead, the Saga must run explicit **compensating actions**: application-level "undo" operations that semantically reverse each already-committed step.

The canonical example: **book a flight + book a hotel + charge a card.**

1. Flight service commits a seat reservation (its own DB, its own local transaction).
2. Hotel service commits a room reservation (its own DB, its own local transaction).
3. Payment service attempts to charge the card — and **fails** (card declined, gateway timeout).

At this point the system is in a partial-success state: a flight seat and a hotel room are held, but no payment was collected. Nothing about the flight DB or the hotel DB knows the payment failed — they are separate systems. The Saga's job is to notice the failure and explicitly invoke **compensating transactions**: "release the flight seat" and "cancel the hotel reservation" — each itself a real operation against the owning service, not a database rollback. Two implementation styles exist (orchestration — a central coordinator issues each step and each compensation; choreography — each service reacts to events from the previous one and emits its own), but both exist to solve the same problem: **keeping N independently-owned data stores consistent with each other when only one all-or-nothing outcome is acceptable, and no cross-database transaction mechanism exists to enforce it automatically.**

The pattern is specifically for **multi-service, multi-database** operations. It has no reason to exist when there is only one service and one database involved.

---

## 2. Why URL Creation in This System Doesn't Have That Shape

Walking the actual create flow, step by step, per `fn-create.md` §2 and §11:

```
validate request → check quota → validate URL → check malicious domain →
generate/validate short code → persist ONE ShortUrl row → return 201
```

Every one of those steps executes **in-process, inside a single service, against a single database** (`fn-create.md` §11's `IUnitOfWork.SaveChangesAsync` call). There is exactly one write: the `ShortUrl` row. Compare this directly against the Saga shape from §1:

| Saga's defining feature | Present in URL creation? |
|---|---|
| Multiple independent services, each owning its own database | **No.** One service, one database (`fn-create.md` §11). |
| A sequence of separately-committed local transactions | **No.** One local transaction commits the entire operation atomically — `AddAsync` + `SaveChangesAsync`, both inside the same EF Core `SaveChangesAsync` call. |
| A partial-success state where some steps committed and others didn't | **No.** Either the single `ShortUrl` insert commits, or it doesn't. There is no intermediate state where "the code was reserved in System A but the URL wasn't recorded in System B," because there is no System B in this write path. |
| Compensating actions needed to undo already-committed steps | **No.** There is nothing to compensate — nothing partially committed anywhere that a later step could invalidate. |

The only thing outside that single local transaction is the `UrlCreated` event described in `05-kafka-comparison.md` §1 — and that event is explicitly **fire-and-forget**, published for downstream consumers (analytics indexing, cache warming) that the create flow does not wait on and does not depend on succeeding. `20-outbox-pattern.md` §3 already worked through exactly what's lost if that publish fails: a missed analytics/search entry, "self-healing on first read," corrected by a periodic reconciliation sweep — explicitly **not** a correctness problem with the core mapping. Critically, a fire-and-forget side effect is not a Saga step in the first place: a Saga step is a state change that the business transaction's correctness *depends on* and that must be compensated if a later step fails. `UrlCreated` is neither depended upon nor in need of compensation if it's lost — there is no "later step" of the create operation that consumes it and could fail because of it. Calling it a Saga step would be a category error, not a scale judgment.

So the create flow has **one write, one database, one atomic commit** — the exact case Sagas are not for. There is no second service whose state must be kept consistent with the first, because there is no second service in the write path at all.

---

## 3. Addressing the "Business Critical" Framing Directly

The premise worth stating plainly: URL creation is **not** a business-critical, multi-party transaction in the sense that motivates Sagas. Contrast with the flight+hotel+payment example:

- In the flight/hotel/payment case, a **half-finished operation leaves real-world resources in an inconsistent, exploitable, or costly state**: a seat is held that should be released, a room is blocked that should be freed, or — worse — a card gets charged for a booking that didn't complete. Someone loses money or inventory if the compensation doesn't happen.
- In URL creation, a failed attempt leaves **nothing partially committed anywhere**. If the single local transaction fails — a validation check rejects the request, the database is unreachable, the collision-retry budget in `fn-create.md` §6 is exhausted — the operation simply did not happen. No code was reserved in one system while a URL sat unrecorded in another. No resource is held that needs releasing.
- The retry path is trivial and cheap: **the client just submits the request again.** There is no undo to perform first, no partial state to reconcile, no external resource to release — because nothing succeeded partway. This is precisely the condition — "failure means nothing happened, not something happened that needs undoing" — under which a Saga is solving a problem that doesn't exist, rather than acting as a safety net against one that does.

This is the sharp distinction the "business critical" framing is really pointing at: Sagas earn their complexity when failure produces an **inconsistent world that must be actively repaired**. URL creation's failure mode produces **no world change at all** — which needs no repair, compensating or otherwise.

---

## 4. What Adopting a Saga Here Would Actually Cost

Introducing a Saga for a single-database, single-write operation is pure downside — there is no correctness gap for it to close, only cost to pay:

- **An orchestrator or choreography machinery that has nothing real to coordinate.** Either a central saga orchestrator tracking step state, or a set of services reacting to each other's events — both are infrastructure built to manage multi-step, multi-service consistency that this flow doesn't have. It would be built to coordinate a single local database write, which the database's own transaction already handles for free.
- **Compensating-transaction logic for a failure mode that doesn't meaningfully exist.** Someone has to design, implement, and test "what does undoing this step look like" — and since there's no second committed state to undo (§2), that logic would either be dead code or, worse, would have to invent artificial intermediate states purely to give the Saga something to compensate, actively making the design more complex and more failure-prone than the single-transaction version it replaced.
- **More moving parts to operate, monitor, and debug.** A saga orchestrator (or the event choreography and its associated state tracking) is a new service/component: it needs its own deployment, health checks, alerting on stuck/failed sagas, and a runbook for manual intervention when a saga gets stuck mid-flight. All of that is standing operational surface area for a business operation that, today, either commits in milliseconds or doesn't happen at all.
- **No corresponding correctness benefit.** This is the decisive point: every cost above is paid in exchange for solving a consistency problem the system does not have. That is the textbook definition of solving a problem the system doesn't have — complexity added against an imagined failure mode, not an actual one, exactly the pattern this project's design conventions (see `20-outbox-pattern.md`'s own conclusion on Outbox) already argue against.

---

## 5. When a Saga WOULD Make Sense Here — Hypothetical, Not a Recommendation

For completeness: the reasoning above is specific to *this* create flow's current shape, not a blanket claim that Sagas are never appropriate anywhere in this system's future. If a **future** feature genuinely spans multiple independently-owned services/databases with a real compensation need, that feature — not today's create flow — would be a legitimate Saga candidate.

Illustrative example, explicitly hypothetical and **not proposed for building now**: imagine a future "custom domain provisioning" feature where creating a link optionally also (a) reserves a custom domain in a separate domain-management service with its own datastore, and (b) notifies a billing service to start metering that domain, with its own datastore. That flow has the actual Saga shape — three independent systems, three separately-committed local transactions, and a real partial-failure case (domain reserved, billing notification fails — now something needs to be released or the domain reservation must be compensated). That is the class of feature Sagas are for. Nothing in the current v1/v2 design introduces this; it's noted here purely so the "when would it apply" question has an honest, concrete answer rather than a hand-wave.

---

## 6. Verdict

**The Saga pattern is explicitly NOT adopted for this system's URL creation flow.**

The create operation is a single local ACID transaction against one database (`fn-create.md` §11) with one optional fire-and-forget side effect (`UrlCreated`, already established as non-critical in `20-outbox-pattern.md`). It has no second service whose state must stay consistent with the first, no partial-success state that could leave the system inconsistent, and therefore nothing a compensating action would ever need to undo. Its failure mode is "nothing happened, retry is free" — not "something happened that must be reversed" — which is precisely the condition under which Sagas are unneeded complexity rather than a safety mechanism. This is not a shortcut or an oversight: it is the correct architectural call, reached by checking the actual shape of the operation against the actual problem the pattern exists to solve, and finding no match. Revisit only if a genuinely multi-service, multi-database feature (§5) is actually built — not for the create flow as it exists or is planned today.
