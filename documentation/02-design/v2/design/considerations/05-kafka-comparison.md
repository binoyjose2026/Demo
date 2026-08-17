# Design Consideration 05 — Kafka Comparison: Is a Message Broker (and Specifically Kafka) Suitable Here?

**Version:** v2 (scalability exploration)
**Status:** Draft
**Scope:** This document answers one narrow question — *should this system introduce Kafka as its message broker, and if so, under what conditions?* — as part of the extreme-scale review requested for `create` and `fetch/analytics` scalability.

**Companion documents (not duplicated here, cross-referenced by filename):**
- `20-outbox-pattern.md` — covers *how* events get published reliably from the write path (the outbox table, relay process, at-least-once delivery guarantee). This document assumes that problem is solved and asks a different question: once an event is ready to publish, what publishes it?
- `03-elasticsearch-vs-sql-server.md` and `04-elasticsearch-vs-mongodb.md` — cover where analytics events *land* after a consumer reads them off the broker. Not addressed here.
- `fn-create.md` and `fn-analytics.md` (v1) — describe the current, single-process, no-external-broker design this document builds on. v1's malicious-domain check (`ILinkSafetyChecker`, `fn-create.md` §9) is synchronous today; v1's click recording (`fn-analytics.md` §4) is already fire-and-forget via an in-process `Channel<T>`, not an external queue.

---

## 1. What Kafka Would Actually Be Used For

At v2 scale, the create and fetch paths would stop doing synchronous, in-process side work and instead publish domain events to a broker, decoupling the request path from everything that doesn't need to happen before the HTTP response returns:

| Event | Published from | Candidate consumers |
|---|---|---|
| `UrlCreated` | `fn-create.md` create flow, after commit | Async malicious/phishing domain re-check or enrichment (moving `ILinkSafetyChecker` off the request path — see §6), cache warm/seed listener |
| `UrlClicked` | `fn-fetch.md` redirect flow, after a successful resolve (today: `fn-analytics.md`'s in-process `IAccessEventRecorder`) | Analytics-indexing service feeding Elasticsearch (`03`/`04`), cache-invalidation listener (evicting/refreshing the redirect cache entry on link mutation-adjacent events), potentially a future fraud/bot-detection consumer |

The shape of the problem is the same regardless of broker: producers (API instances handling create/fetch) should never block on, or fail because of, a downstream consumer being slow or unavailable. That's true whether the broker is Kafka, Azure Service Bus, SQS, or RabbitMQ — it is a "decouple producer from consumer" requirement, not inherently a Kafka requirement. The question this document actually answers is *which broker*, not *whether to decouple*.

---

## 2. Does the Stated Volume Justify Kafka? — The Math

### 2.1 Event rate at today's scale

- Creates: 1,000,000/day → 1,000,000 / 86,400 s ≈ **11.6 events/sec average**
- Fetches (→ `UrlClicked`): 10,000,000/day → 10,000,000 / 86,400 s ≈ **115.7 events/sec average**
- Combined average today: ≈ **127 events/sec**

### 2.2 Event rate at the 5-year projection

- Creates: 5,000,000/day → 5,000,000 / 86,400 s ≈ **57.9 events/sec average**
- Fetches (→ `UrlClicked`): 100,000,000/day → 100,000,000 / 86,400 s ≈ **1,157 events/sec average** (this is the ~1,150/sec figure in the scale assumption)
- Combined average in 5 years: 57.9 + 1,157 ≈ **1,215 events/sec average**

### 2.3 Peak, not just average

The stated peak-to-average ratio is 5–10x during traffic spikes:

- Low end: 1,215/sec × 5 ≈ **~6,100 events/sec peak**
- High end: 1,215/sec × 10 ≈ **~12,150 events/sec peak**

So the number to design against is **roughly 1,200 events/sec sustained, bursting to ~6,000–12,000/sec** — five years out, at the stated growth ceiling.

### 2.4 What that number means in context

- Kafka's design point is **millions of messages/sec** on modest, well-tuned hardware (single broker, single partition throughput in the tens-of-thousands/sec to low-hundreds-of-thousands/sec range depending on message size and batching; a small cluster scales that linearly with partitions). ~12,000 events/sec peak is roughly **0.1–1%** of what a single reasonably-configured Kafka broker handles before needing to scale out.
- Managed non-Kafka queues comfortably clear this bar too:
  - **AWS SQS standard queues** — no meaningful hard throughput ceiling for this volume (nearly unlimited TPS at the API level, unbounded consumer parallelism).
  - **AWS SNS → fan-out to multiple SQS queues** — gives independent-consumer fan-out (one topic, N queues, N consumer groups) at the same throughput ceiling as SQS.
  - **Azure Service Bus (Premium tier, Topics/Subscriptions)** — rated in the low thousands of msg/sec per messaging unit and scales by adding messaging units; ~1,200/sec average with ~12,000/sec bursts is within a small number of messaging units, and Topics/Subscriptions give the same "multiple independent consumers reading the same stream" shape Kafka is normally reached for.
  - **RabbitMQ** — tens of thousands of msg/sec on a single modest node with normal (non-quorum, non-durable-per-message) configuration; this volume is a non-event for it.

**Plain statement: the stated volume, including the stated worst-case burst, does not by itself require Kafka.** Every option in the comparison set — including options with materially less operational overhead — handles ~1,200 events/sec average / ~12,000/sec peak without strain. Throughput headroom is not the deciding factor here.

---

## 3. What Would Actually Justify Kafka

Kafka's real differentiators versus a managed queue are not raw throughput at this volume — they are:

1. **Long-retention, replayable log.** A Kafka topic can retain events for days/weeks/indefinitely and be re-read from any offset. A queue (SQS/Service Bus/RabbitMQ) is fundamentally a *delivery* mechanism — once a message is consumed and acked, it's gone (short-lived, time-boxed retention for retry purposes only, not designed as a durable historical log).
2. **Independent consumer groups at independent paces, replayed from history.** Kafka's pull-based, offset-tracked consumer model lets a *new* consumer (e.g., a fraud-detection service added 18 months from now) start reading from the beginning of the retained log and reconstruct history it was never live for. Pub/sub fan-out (SNS→SQS, Service Bus Topics) gives you independent consumers *going forward from when they subscribe* — it does not give you retroactive replay of events that happened before the consumer existed.
3. **Very high sustained throughput ceiling as a long-term platform bet**, useful if this event stream is expected to become the backbone for many future systems beyond the three named here, not just this one product.

None of these are volume arguments — they are **replay** and **multi-consumer-over-time** arguments. So the real question is not "how many events/sec" but: **does this system need to reprocess history, and does it expect to keep adding independent consumers of the same stream indefinitely?**

Concrete cases where the answer would be yes, for this specific system:
- Rebuilding the Elasticsearch index from scratch after a mapping/schema change (`03-elasticsearch-vs-sql-server.md` / `04-elasticsearch-vs-mongodb.md`) without re-deriving events from the OLTP store.
- Backfilling a newly-added consumer (e.g., a bot-detection or geo-enrichment service added later) against months of historical click data it wasn't around to consume live.
- Deliberately decoupling the malicious-domain re-check (§6 below) so it can be reprocessed against an updated threat-intel feed without replaying from the primary datastore.

If none of these are near-term requirements, they are speculative justifications, not present ones — and building for a capability you don't have a concrete plan to use is exactly the kind of scope creep this project's own conventions (see `fn-analytics.md` §1.1's treatment of "no speculative building") already push back on.

---

## 4. Recommendation

**Not suitable as a default choice at the stated volume alone. Suitable, and worth adopting deliberately, if replay or multi-consumer-over-time needs are real.**

Stated as a conditional, per the request:

> Kafka is **suitable** if the system wants replay capability (reprocessing analytics history, rebuilding a search index, backfilling a consumer added after the fact) or expects to keep adding independent consumer groups to the same event stream over time.
> Kafka is **overkill** if the near-term need is "three known consumers drain a queue reliably" — in which case a simpler managed queue (Azure Service Bus Topics, or AWS SNS+SQS) is the pragmatic choice, sized comfortably for the 5-year, ~1,200 events/sec / ~12,000/sec-peak projection.

Given this system is a .NET 9 / Azure-shaped stack already (per the v1 design guidelines), **Azure Service Bus (Topics/Subscriptions)** is the natural default candidate for "reliable async queue with independent-consumer fan-out": it directly satisfies the three named consumers (analytics-indexing, cache-invalidation, moderation) as three separate subscriptions on one topic, at a fraction of Kafka's operational surface, and at a throughput tier this volume doesn't come close to saturating.

Kafka should be an **explicit, named upgrade path**, not the starting point: revisit this decision if (a) replay/reprocessing becomes a concrete, scheduled need, (b) the number of independent consumers grows materially beyond the three named here, or (c) actual measured throughput trends toward an order of magnitude beyond the stated 5-year projection (i.e., tens of thousands of events/sec sustained, not just burst).

---

## 5. Operational Cost and Complexity — Stated Honestly

This is a real trade-off, not a footnote:

- **Self-managed Kafka** (VMs/Kubernetes, running Kafka + ZooKeeper or KRaft) requires: partition and replication-factor planning, broker sizing and disk/IO tuning, consumer-lag and ISR (in-sync replica) monitoring, upgrade/patch cadence, and genuine Kafka operational expertise on the team. This is a multi-person, ongoing responsibility, not a one-time setup — an underestimated Kafka deployment is a common source of production incidents (under-provisioned partitions, consumer-group rebalance storms, disk exhaustion from retention misconfiguration).
- **Managed Kafka** (Confluent Cloud, AWS MSK, or Azure Event Hubs' Kafka-compatible endpoint) removes broker/OS-level operations but still requires topic/partition design, schema management (schema registry or equivalent, e.g. via Event Hubs Schema Registry), consumer-group design, and ongoing per-throughput-unit/per-broker cost — and it is priced and billed as a dedicated streaming platform, not a lightweight queue.
- **Azure Service Bus / SQS+SNS / RabbitMQ (managed, e.g. Azure Service Bus or Amazon MQ)**, by contrast, are close to zero-ops from the application team's perspective: no partition planning, no consumer-lag tuning, no broker capacity planning beyond a tier/SKU choice, and a materially smaller conceptual surface for the team to own.

At this system's current team size and stated scale, choosing Kafka is choosing to take on a standing platform-operations cost in exchange for replay and multi-consumer capabilities that are not yet a confirmed requirement. That is a legitimate choice if the replay/backfill scenarios in §3 are real and near-term — but it should be made as a deliberate trade against that operational cost, not adopted by default because Kafka is the well-known name for "event streaming."

---

## 6. Interaction with v1's Synchronous Malicious-Domain Check

Worth flagging as a concrete "would this actually use Kafka's differentiators" test case: v1's `ILinkSafetyChecker`/`IMaliciousUrlChecker` (`fn-create.md` §9, `nfr-security.md` §4) runs **synchronously**, in the request path, before a link is persisted. Moving to an async `UrlCreated`-event-triggered re-check (e.g., a slower, more thorough reputation lookup that runs after creation and can retroactively flag/deactivate a link) is exactly the kind of "add a new consumer later, want it to have seen everything since day one if it's ever rebuilt" scenario from §3 — a real, if modest, argument in Kafka's favor if this async-moderation direction is pursued. Until it is, it remains a candidate future consumer, not a present requirement.

---

## 7. Relationship to the Outbox Pattern

Whatever broker is chosen — Kafka or a simpler managed queue — the problem of atomically committing the database write (the new `ShortUrl` row, or the access-event row) together with the guarantee that its corresponding event actually gets published is a separate concern, covered in full in `20-outbox-pattern.md`. This document decides *what* the events get published to; `20-outbox-pattern.md` decides *how* they get published reliably out of the write path in the first place. Neither decision depends on the other: the outbox pattern is broker-agnostic, and works identically whether the relay process ultimately writes to Kafka or to a Service Bus topic.

---

## 8. Summary

| Question | Answer |
|---|---|
| Does the stated 5-year volume (~1,215 events/sec avg, ~6,000–12,000/sec peak) require Kafka on throughput grounds alone? | **No** — it's within reach of any mainstream managed queue. |
| What would justify Kafka here? | Replay/reprocessing needs (index rebuilds, backfilling new consumers) and/or a growing number of independent, long-lived consumer groups over the same stream. |
| Is Kafka *suitable* for this system? | Conditionally — suitable as a deliberate choice if the above needs are real or clearly imminent; not suitable as the default starting point. |
| What's the pragmatic v2 default? | A managed queue/topic service (Azure Service Bus Topics, or SNS+SQS) sized for ~1,200 events/sec average with burst headroom, with Kafka retained as an explicit, named upgrade path. |
| What's the honest cost of choosing Kafka anyway? | Meaningful standing operational complexity (self-managed) or ongoing platform cost (managed), independent of whether the volume needs it. |
