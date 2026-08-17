# Consideration 25 — Elasticsearch Bulk API for Click/Analytics Event Indexing

**Version:** v2 (scalability exploration)
**Status:** Draft — architectural consideration, not yet a committed decision
**Scope:** This document answers one narrow question — *given that click/analytics events already have a store (`03-elasticsearch-vs-sql-server.md`) and a delivery mechanism (`05-kafka-comparison.md`), how should the consumer that reads those events actually write them into Elasticsearch?* It does not re-argue the store choice or the broker choice — both are assumed settled — and it does not redesign the resilience patterns already defined for the Elasticsearch call path.
**Traceability:** `agent-prompt.md` (review scope item: "Batching: can you add batching logic for url fetches. Multi inserts into Elastic Search").

**Companion documents (not duplicated here, cross-referenced by filename):**
- `03-elasticsearch-vs-sql-server.md` — establishes Elasticsearch as the analytics/click-event store at up to ~100M events/day (~1,160/sec sustained average per that document's own math), append-only, aggregation-heavy, consistency-relaxed. This document assumes that decision and asks only how documents get written in.
- `05-kafka-comparison.md` — establishes the broker (Azure Service Bus Topics by default, Kafka as a named upgrade path) that `UrlClicked` events flow through before reaching the analytics-indexing consumer, and gives the event-rate math (~1,215 events/sec average, ~6,100–12,150/sec peak at 5-year scale) this document's throughput section reuses directly.
- `21-background-job-hosting.md` — establishes that the analytics-indexing broker consumer is hosted as a long-running, containerized `BackgroundService` (Worker Service), not Azure Functions, specifically because it is a continuous, stateful stream consumer. The batching/flush logic described here lives inside that worker's consume loop.
- `18-circuit-breaker.md` — the circuit breaker already defined for outbound Elasticsearch calls. This document's bulk-flush call is one more thing that goes through that breaker; the breaker's states, trip conditions, and fallback design are not repeated or redesigned here.
- `19-bulkhead.md` — the concurrency bulkhead already sized for the Elasticsearch indexing dependency (20 concurrent calls, Section 2 of that document). The bulk-flush call is what actually occupies those 20 slots; this document does not resize that ceiling.

---

## 1. Why Single-Document Indexing Doesn't Scale Here

The naive implementation of "the consumer reads an event, indexes it" is one HTTP `PUT`/`POST` to Elasticsearch's single-document index API per event. At this system's stated volume, that naive approach is a well-known Elasticsearch anti-pattern, not a matter of taste:

- **Per-request overhead is paid once per document instead of once per batch.** Every single-document index call is a full HTTP request/response round-trip: TCP/TLS (or connection-pool checkout), HTTP header parsing, JSON body parsing, routing to the correct shard, a translog write, and a response serialized back to the caller. None of that overhead is shared across documents when each document gets its own call — it is paid in full, every time, for every event.
- **Refresh/translog behavior amplifies the cost.** Elasticsearch's near-real-time visibility model (the ~1s default refresh interval already noted in `03-elasticsearch-vs-sql-server.md` Section 5) and its translog durability model are both tuned around the assumption that documents arrive in batches. Indexing one document at a time doesn't make any individual document "more durable" or "more visible sooner" in a way that matters for an analytics event — `fn-analytics.md`'s already-accepted tolerance for a few seconds of staleness (Section 4, referenced in `03-elasticsearch-vs-sql-server.md`) means there is no latency benefit being purchased by single-doc indexing, only overhead.
- **Connection overhead compounds under concurrency.** At sustained volume, single-doc indexing means the consumer either opens a very large number of concurrent HTTP connections to Elasticsearch (fighting the very connection ceiling `19-bulkhead.md` Section 2 deliberately caps at 20 for this dependency) or serializes requests one-at-a-time and falls behind the arrival rate. Neither is workable.
- **The unrealistic-call-volume argument, stated directly:** at ~1,150 events/sec sustained (per `03-elasticsearch-vs-sql-server.md`/`05-kafka-comparison.md`), single-document indexing means **~1,150 individual HTTP calls/sec to Elasticsearch from this one workload alone**, climbing to **~6,000–12,000 calls/sec at the stated 5-year peak** (`05-kafka-comparison.md` Section 2.3). That is not a small number of extra requests — it is asking a 20-slot concurrency bulkhead (`19-bulkhead.md`) to somehow sustain over a thousand sequential round-trips per second, which is arithmetically impossible without either massively widening the bulkhead (defeating its purpose as a shared-resource protector) or falling permanently behind the broker's arrival rate (an ever-growing consumer lag).
- **It badly underutilizes the cluster's real indexing throughput capacity.** Elasticsearch's actual indexing throughput ceiling — the number Section 4 of `03-elasticsearch-vs-sql-server.md` cites as Elasticsearch's core strength ("native strength — built for continuous document ingest") — is realized through bulk ingestion, not single-document calls. A cluster sized for high-throughput bulk indexing, fed one document per HTTP request, spends most of its and the caller's time on per-request bookkeeping rather than on the actual Lucene segment writes it is good at. The gap between "what this cluster could index" and "what single-doc indexing lets it index" widens as volume grows — which is exactly the direction this system is headed (Section 5 below quantifies this).

**In one sentence:** single-document indexing turns a workload Elasticsearch is architecturally built to absorb efficiently (Section 4 of `03-elasticsearch-vs-sql-server.md`) into a workload dominated by request overhead instead of actual indexing work, at a call volume that doesn't fit through the concurrency budget already allocated to this dependency.

---

## 2. The Bulk API Mechanism

Elasticsearch's `_bulk` endpoint accepts a single HTTP request containing many index (or update/delete) operations, each described by an action-and-metadata line followed by the document source line (newline-delimited JSON). The cluster processes all operations in that request together, still writing each document as its own entry in the target index (bulk indexing does not merge documents into one — it merges the *transport and per-request overhead* of many documents into one call), and returns a single response describing the per-item outcome of every operation in the batch.

For this system, the shape is:

- **The consumer is the analytics-indexing Worker Service** described in `21-background-job-hosting.md` Section 1 — a long-running, containerized `BackgroundService` holding a persistent subscription/consumer-group connection to the broker (`05-kafka-comparison.md`), continuously reading `UrlClicked` events.
- **Instead of indexing each event as it's read, the consumer accumulates events in an in-memory buffer** as it pulls them off the broker.
- **The buffer is flushed to Elasticsearch's `_bulk` endpoint as one multi-document request** once a flush trigger fires (Section 3), rather than one `_bulk` call per document (which would just relocate the Section 1 problem into a differently-shaped API call) or one single-doc call per event.
- **The broker offset/checkpoint is only committed after a successful (or successfully handled — see Section 4) bulk flush**, consistent with `21-background-job-hosting.md` Section 5's point that offset/checkpoint management is a natural property of a long-running consumer that pulls, buffers, and commits progress as it goes — not something a per-invocation model does cleanly.

This is purely a change in *how many events are sent per Elasticsearch call*, not a change to the store (`03-elasticsearch-vs-sql-server.md`), the broker (`05-kafka-comparison.md`), or the hosting model (`21-background-job-hosting.md`) — all three of those decisions are inputs to this document, not outputs of it.

---

## 3. Batching Trigger — Size AND Time, Whichever Comes First

A single-condition trigger fails in one direction or the other depending on load, so the flush condition is **size OR time, whichever is reached first**:

- **Size trigger: flush at 1,000 accumulated documents.**
- **Time trigger: flush at 1 second since the buffer was last flushed (or since the first event in the current buffer arrived), even if the size trigger hasn't been reached.**

### Why size-only fails

A size-only trigger (e.g., "flush every 1,000 documents, however long that takes") risks **unbounded latency during low-traffic periods**. At low volume — off-peak hours, or simply a quieter day — events may accumulate toward the 1,000-document threshold slowly. If traffic drops to, say, 10 events/sec, a pure size trigger would leave events sitting unflushed in memory for 100 seconds before the batch fills, which both delays their visibility in Elasticsearch far beyond the "seconds of staleness" tolerance `fn-analytics.md` accepts, and holds an unbounded amount of unflushed data in the worker's memory for an unbounded time if traffic never resumes before a restart.

### Why time-only fails

A time-only trigger (e.g., "flush every 1 second, however many documents that is") risks **inefficiently small batches during traffic spikes**. At the stated peak (~6,000–12,000 events/sec, `05-kafka-comparison.md` Section 2.3), a 1-second-only trigger would still flush every second, but by then the buffer could hold 6,000–12,000 documents — an oversized single bulk request that risks hitting Elasticsearch's `http.max_content_length` limit, causing a large, all-or-nothing request that's slower to process and riskier to retry as a unit than several well-sized batches would be. Conversely, at genuinely low traffic, a time-only trigger flushes on schedule regardless of how few documents accumulated — which is fine for latency but means many bulk calls carry only a handful of documents, forfeiting most of the batching benefit for that stretch (not a correctness problem, just an efficiency one, and one the size trigger's OR-condition doesn't cost anything to also guard against).

### Why size AND time (OR-triggered) bounds both problems

Combining both triggers means:

- **Under sustained high load** (at or above ~1,000 events/sec), the size trigger fires first — batches are consistently well-sized (1,000 documents) and flush frequently, keeping both memory use and per-batch request size bounded and predictable.
- **Under low or bursty load**, the time trigger guarantees no event waits more than ~1 second past the last flush before being sent, regardless of how slowly the buffer is filling — bounding worst-case latency and preventing the "events sit unflushed indefinitely" failure mode.
- **Neither trigger alone has to be tuned to cover the other's failure case** — the OR-condition means the size threshold can be picked purely for batch efficiency/request-size reasons, and the time threshold can be picked purely for latency-bound reasons, without compromise.

**Concrete numbers used here: flush at 1,000 documents or 1 second, whichever comes first** (the 500–1,000-document / 1–2-second range named in the task is real design space — 1,000 docs and a 1-second ceiling are the specific values this document commits to, chosen because they land the size trigger just under the average sustained event rate at 5-year scale, per the math in Section 5, meaning the size trigger — not a stall on the time trigger — governs behavior in the common case).

---

## 4. Backpressure and Failure Handling

### 4.1 Backpressure — bound the buffer, don't grow memory unboundedly

If the consumer accumulates events from the broker faster than it can flush them to Elasticsearch (e.g., during an Elasticsearch slowdown, or a burst well above the peak this system is sized for), the in-memory buffer must be **bounded**, not allowed to grow without limit:

- The buffer has a hard capacity ceiling (sized as a small multiple of the flush batch size — e.g., 5,000–10,000 events, a few flush-cycles' worth of headroom, not an arbitrary large number).
- **Once the buffer is at capacity, the consumer stops pulling new messages off the broker** rather than continuing to read and letting the buffer (and process memory) grow past its bound. This is backpressure applied at the correct layer: the broker is the durable, already-designed-for-this buffer (Service Bus/Kafka retains unconsumed messages per `05-kafka-comparison.md`), so pausing consumption is safe — messages wait on the broker, not in this process's memory, and nothing is lost.
- This composes directly with `19-bulkhead.md`'s existing 20-concurrency ceiling on the Elasticsearch indexing dependency (Section 2 of that document): if flush calls themselves start queuing or being rejected because the bulkhead is saturated (e.g., Elasticsearch is slow and flushes are taking longer than the 1-second cadence), the buffer fills, backpressure engages, and the consumer's broker-read rate drops to match what Elasticsearch can actually absorb — exactly the graceful-degradation behavior `19-bulkhead.md` Section 5 describes generally, applied here to this specific consumer.
- **What this deliberately does not do:** grow the buffer unboundedly "just in case," or spawn unbounded concurrent flush calls to drain the buffer faster — both would reintroduce the unbounded-resource-growth failure mode `19-bulkhead.md` Section 1 already identifies as the core problem bulkheads exist to prevent, just relocated into this worker's own memory/concurrency instead of the shared Elasticsearch client pool.

### 4.2 Partial bulk failure — detect and dead-letter the failed subset, not the whole batch

The `_bulk` API's response is per-item: a single `_bulk` call can have some documents succeed and others fail (e.g., a mapping conflict on one malformed event, a transient shard-unavailable error on the specific shard one document routed to, a rejected document exceeding a field-length constraint) while the rest of the batch indexes successfully. Treating the batch as all-or-nothing — retrying or failing the entire batch because *any* item failed — is wrong on two counts: it re-indexes documents that already succeeded (harmless for idempotent, deterministically-ID'd events, but wasteful), and it does not actually isolate or examine *why* the failed subset failed, which a blanket retry will not fix if the cause is deterministic (e.g., a malformed document that will fail identically on every retry).

The consumer's flush logic therefore:

1. **Parses the bulk response's per-item results** (`items[].index.status` / `items[].index.error`), not just the top-level HTTP status of the `_bulk` call itself — a `200 OK` from `_bulk` only means the request was accepted and processed, not that every item succeeded (the response body's `errors: true/false` flag and per-item statuses carry that information).
2. **Commits the broker offset/checkpoint only after this parse step**, covering the documents that succeeded — per `21-background-job-hosting.md` Section 5's point that checkpoint commits are a property of the consumer's own progress tracking, this consumer advances its checkpoint based on what Elasticsearch actually accepted, not merely on having sent the request.
3. **For the failed subset:** classify each failure as retryable (e.g., a transient `es_rejected_execution_exception` from a momentarily overloaded shard) or non-retryable (e.g., a mapping/parsing error that will recur identically). Retryable failures are re-queued into the next flush cycle's buffer (bounded — Section 4.1's cap still applies, so a persistently-failing dependency doesn't let retries accumulate unboundedly either). Non-retryable failures are **dead-lettered** — written to a separate dead-letter destination (a dedicated broker topic/queue, or a lightweight persisted table, per this system's existing conventions) rather than blocking or endlessly retrying the rest of the stream, and the checkpoint still advances past them (an analytics event that will never successfully index is not worth stalling the entire consumer over, consistent with `fn-analytics.md`'s already-accepted tolerance for losing an occasional event under extreme conditions).
4. **Only the subset that fails, not the whole batch, is retried or dead-lettered** — the documents that succeeded in the same `_bulk` call are done, and are never resubmitted.

### 4.3 Composition with Circuit Breaker and Bulkhead — cross-referenced, not redesigned

The bulk-flush call is, from the resilience pipeline's point of view, just one more call to Elasticsearch — it goes through the same layered pipeline `18-circuit-breaker.md` and `19-bulkhead.md` already define for this dependency, unmodified by anything in this document:

- **Bulkhead (`19-bulkhead.md` Section 2):** the Elasticsearch indexing dependency's 20-concurrency ceiling still governs how many bulk-flush calls may be in flight at once. Because each call now carries up to 1,000 documents instead of one, the same 20-slot ceiling represents dramatically more indexing throughput per occupied slot than it would under single-doc indexing — bulk batching is, among other things, what makes a 20-concurrency bulkhead sufficient at this event volume at all (Section 5 makes this concrete).
- **Circuit breaker (`18-circuit-breaker.md`):** if Elasticsearch degrades or becomes unreachable, the breaker trips per its existing Section 3 trip conditions (this document does not define separate trip conditions for bulk calls — the same breaker instance guarding the Elasticsearch indexing dependency covers this call path). While open, the consumer's flush attempts fail fast (`BrokenCircuitException`, per `18-circuit-breaker.md` Section 6) rather than blocking on a doomed bulk request — and because the consumer cannot successfully flush while the breaker is open, the buffer fills and backpressure (Section 4.1) engages, pausing broker consumption until the breaker's half-open probe (`18-circuit-breaker.md` Section 5) confirms Elasticsearch has recovered. This is the same "fail fast, don't fail the whole request" mechanism `18-circuit-breaker.md` Section 4 describes for the read-side analytics query, applied here on the write side: the breaker protects the consumer from hammering a down cluster with bulk requests, and the bulkhead's backpressure (Section 4.1) is what translates "stop calling Elasticsearch" into "stop pulling from the broker," which is the correct response for this consumer specifically (unlike the read-path fallback in `18-circuit-breaker.md`, there is no "serve stale data" equivalent for a write path — pausing consumption and letting the broker hold events durably is the fallback).
- **Nothing here resizes either pattern's thresholds.** The 20-call bulkhead ceiling and the breaker's 50%-over-30s trip condition (as defined for this dependency in their respective documents) are unchanged; this document only establishes what fills those 20 slots (1,000-document bulk requests, not single-document ones) and what the consumer does while the breaker is open (buffer, backpressure, no data loss for events still on the broker).

---

## 5. Throughput Math

Using `05-kafka-comparison.md`'s own event-rate figures (Section 2.2–2.3 of that document) and the 1,000-document / 1-second flush trigger from Section 3:

### 5.1 Single-document indexing baseline (what this replaces)

| Load | Events/sec | Individual index calls/sec required |
|---|---|---|
| 5-year sustained average | ~1,215/sec (1,157/sec fetches alone, per `05-kafka-comparison.md` Section 2.2) | **~1,150–1,215 calls/sec** |
| 5-year peak (5–10x burst) | ~6,100–12,150/sec | **~6,100–12,150 calls/sec** |

### 5.2 With 1,000-document bulk batching, size-or-time trigger

At **~1,150 events/sec sustained average**:

- Time to accumulate 1,000 documents at 1,150 events/sec = 1,000 ÷ 1,150 ≈ **0.87 seconds** — under the 1-second time-trigger ceiling, so the **size trigger governs**: the buffer fills to 1,000 before the 1-second clock runs out.
- Flush frequency = 1,150 events/sec ÷ 1,000 events/batch ≈ **1.15 bulk requests/sec.**
- **This is roughly 1–2 bulk requests/sec, replacing ~1,150 individual index requests/sec** — call volume to Elasticsearch drops by a factor of roughly **1,000x** for this workload at sustained average load.

At **peak load (~6,100–12,150 events/sec)**:

- Time to accumulate 1,000 documents at 6,100 events/sec = 1,000 ÷ 6,100 ≈ **0.16 seconds**; at 12,150 events/sec ≈ **0.08 seconds** — well under the 1-second ceiling, so the size trigger governs throughout the peak window too.
- Flush frequency = events/sec ÷ 1,000 ≈ **6.1–12.2 bulk requests/sec** at the low/high end of the stated peak range.
- **This replaces ~6,100–12,150 individual index requests/sec with ~6–12 bulk requests/sec** — the same roughly 1,000x reduction in call volume holds at peak, because both numerator and denominator scale together (the batch size, not the event rate, is what's fixed).

At **low/off-peak load** (illustrative — e.g., ~50 events/sec during a quiet period):

- Time to accumulate 1,000 documents at 50 events/sec = 1,000 ÷ 50 = **20 seconds** — this exceeds the 1-second time trigger, so the **time trigger governs**: the buffer flushes every ~1 second regardless of fill level.
- Flush frequency ≈ **1 bulk request/sec** (as designed), each carrying roughly 50 documents rather than 1,000 — a smaller, less request-count-efficient batch, but this is the deliberate, bounded cost of keeping worst-case latency low during quiet periods (Section 3's stated trade-off), not a flaw.

### 5.3 Summary

| Metric | Single-doc indexing | 1,000-doc bulk, 1s-or-size trigger |
|---|---|---|
| Requests/sec at ~1,150 events/sec avg | ~1,150–1,215 | **~1–2** |
| Requests/sec at ~6,100–12,150 events/sec peak | ~6,100–12,150 | **~6–12** |
| Approximate call-volume reduction | — | **~1,000x** (bounded below by the 1-second latency ceiling at low traffic) |
| Worst-case staleness added by batching | — | ≤ ~1 second (bounded by the time trigger) |

The reduction is not a marginal optimization — it is the difference between a call volume the 20-concurrency Elasticsearch bulkhead (`19-bulkhead.md` Section 2) cannot physically sustain (thousands of sequential single-doc round-trips/sec through 20 slots) and a call volume it comfortably absorbs with room to spare (single-digit-to-low-double-digit bulk requests/sec).

---

## 6. Implementation Note: `Elastic.Clients.Elasticsearch` Bulk Support

The official Elastic .NET client, `Elastic.Clients.Elasticsearch`, has first-party support for the Bulk API via `ElasticsearchClient.BulkAsync` (and a fluent `BulkRequestDescriptor`), so the consumer does not need to hand-build `_bulk` NDJSON payloads. The sketch below shows the batching/flush-trigger logic living inside the analytics-indexing `BackgroundService` (`21-background-job-hosting.md` Section 8), with the actual bulk call going through the existing resilience pipeline (`18-circuit-breaker.md` / `19-bulkhead.md`) unchanged:

```csharp
// Worker/AnalyticsIndexingWorker.cs
public sealed class AnalyticsIndexingWorker : BackgroundService
{
    private const int FlushSize = 1_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly IBrokerConsumer<UrlClickedEvent> _consumer; // per 05-kafka-comparison.md
    private readonly ElasticsearchClient _esClient;               // Elastic.Clients.Elasticsearch
    private readonly IDeadLetterWriter _deadLetter;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var buffer = new List<(UrlClickedEvent Event, object Offset)>(FlushSize);
        using var flushTimer = new PeriodicTimer(FlushInterval);

        await foreach (var (evt, offset) in _consumer.ConsumeAsync(ct))
        {
            buffer.Add((evt, offset));

            // Size trigger.
            if (buffer.Count >= FlushSize)
            {
                await FlushAsync(buffer, ct);
                buffer.Clear();
                continue;
            }

            // Time trigger — non-blocking check; a real implementation runs
            // the timer concurrently (e.g., via Task.WhenAny) rather than
            // inline per-event. Simplified here for clarity.
            if (await flushTimer.WaitForNextTickAsync(ct) && buffer.Count > 0)
            {
                await FlushAsync(buffer, ct);
                buffer.Clear();
            }
        }
    }

    private async Task FlushAsync(
        List<(UrlClickedEvent Event, object Offset)> buffer, CancellationToken ct)
    {
        // Goes through the existing bulkhead (19-bulkhead.md, 20-slot ceiling)
        // and circuit breaker (18-circuit-breaker.md) already wired on the
        // Elasticsearch client pipeline — not configured here.
        var response = await _esClient.BulkAsync(b => b
            .Index("clicks")
            .IndexMany(buffer.Select(x => x.Event)), ct);

        if (!response.IsValidResponse || response.Errors)
        {
            foreach (var item in response.Items.Where(i => i.Error is not null))
            {
                var failed = buffer[item.ItemIndex ?? -1];
                if (IsRetryable(item.Error))
                    _consumer.Requeue(failed.Offset);   // bounded — Section 4.1
                else
                    await _deadLetter.WriteAsync(failed.Event, item.Error, ct); // Section 4.2
            }
        }

        // Commit offsets only for successfully indexed (or dead-lettered) items.
        await _consumer.CommitAsync(buffer
            .Where((_, idx) => response.Items[idx].Error is null || !IsRetryable(response.Items[idx].Error))
            .Select(x => x.Offset), ct);
    }

    private static bool IsRetryable(ErrorCause? error) =>
        error?.Type is "es_rejected_execution_exception" or "circuit_breaking_exception";
}
```

This is illustrative, not production-ready code — the real implementation needs concurrent (not sequential) size/time trigger evaluation, the buffer-capacity backpressure gate from Section 4.1, and wiring the `_esClient` through the resilience-handler pipeline already established for this dependency. The load-bearing point is structural: **one `BulkAsync` call per flush, sized-or-timed, with per-item response inspection driving offset commit and dead-lettering** — not one client call per event.

---

## 7. Summary

| Concern | Decision |
|---|---|
| Why single-doc indexing doesn't scale | At ~1,150–12,150 events/sec, single-doc indexing requires an equal number of individual HTTP calls/sec, exceeding the 20-concurrency Elasticsearch bulkhead (`19-bulkhead.md`) and paying per-request overhead (HTTP, translog, refresh) on every event instead of amortizing it. |
| Mechanism | The analytics-indexing Worker Service (`21-background-job-hosting.md`) accumulates events read off the broker (`05-kafka-comparison.md`) in memory and flushes them via `Elastic.Clients.Elasticsearch`'s `_bulk` support as one multi-document request per flush. |
| Flush trigger | **1,000 documents OR 1 second, whichever comes first** — size trigger governs at/above ~1,150 events/sec (the common case at 5-year scale); time trigger bounds worst-case latency during low-traffic periods. |
| Backpressure | In-memory buffer capped (a small multiple of flush size); once full, the consumer stops pulling from the broker — the broker (not process memory) absorbs the backlog, composing with the existing Elasticsearch bulkhead ceiling. |
| Partial bulk failure | Per-item response inspection; retryable failures re-queued (bounded), non-retryable failures dead-lettered; broker offset commits only for items Elasticsearch actually accepted (or definitively dead-lettered) — never the whole batch retried for one bad document. |
| Composition with existing resilience patterns | The bulk-flush call is one more call through the existing `18-circuit-breaker.md`/`19-bulkhead.md` pipeline for this dependency — thresholds and fallback design unchanged; this document only changes what fills the bulkhead's slots. |
| Throughput impact | ~1,150 events/sec avg → **~1–2 bulk requests/sec** (vs. ~1,150 single-doc requests/sec); ~6,100–12,150 events/sec peak → **~6–12 bulk requests/sec** (vs. ~6,100–12,150) — roughly a 1,000x reduction in Elasticsearch call volume. |
