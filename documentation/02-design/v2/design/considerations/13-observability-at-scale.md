# Observability at Extreme Scale

**Scope:** v2 scalability review — one of the numbered considerations produced against `documentation/02-design/v2/agents/prompt@review-desig.md`.
**Builds on (does not replace):** `v1/design/nfr-reliability-and-availability.md` Section 4, which already defines `/health/live` and `/health/ready` — those endpoints remain as-is; this document adds the request-level, cross-service observability v1's single-instance, single-process shape never needed.
**Related v2 documents (by filename, not duplicated here):** `05-kafka-comaporison.md` and `20-outbox-pattern.md` (the message broker and reliable-publish mechanics this document instruments, not re-describes), `07-redis-caching-and-invalidation.md` (the distributed cache whose hit rate this document turns into a metric), `03-elasticsearch-vs-sql-server.md` / `04-elasticsearch-vs-mongodb.md` (the analytics store; this document's observability backend — Grafana, Loki, and OpenTelemetry — is separate infrastructure, not a reuse of that Elasticsearch cluster), `01-create-path-extreme-scalability.md` (the horizontally-scaled API instances this document exists to make debuggable as a fleet, not one box at a time).

---

## 1. Why v1's Observability Model Breaks Down at v2 Scale

`nfr-reliability-and-availability.md` Section 4 gives v1 exactly what a single-process, single-instance, SQLite-backed service needs: a liveness endpoint and a readiness endpoint. That is sufficient because in v1 there is only one place a request can be — inside one ASP.NET Core process, talking to one local database file. If a redirect is slow, there is exactly one log stream to look at and exactly one process to profile.

v2 removes every one of those simplifying assumptions:

- **Multiple API instances.** `01-create-path-extreme-scalability.md` establishes the API runs as N horizontally-scaled instances behind a load balancer. A single logical request can land on any instance, and a retry or a downstream fan-out can touch a different instance than the one that received it.
- **A distributed cache.** `07-redis-caching-and-invalidation.md` puts a shared Redis tier in the read path. A slow redirect might be a Redis network hop, a Redis eviction under memory pressure, or a cache miss cascading to the database — three different root causes that look identical from the outside ("the redirect was slow") without instrumentation that distinguishes them.
- **An async event pipeline.** `05-kafka-comaporison.md` / `20-outbox-pattern.md` move click recording, cache invalidation, and enrichment off the request path and onto a message broker. The HTTP response now returns *before* several of the effects of that request have finished — a failure in a downstream consumer (an analytics-indexing job, a cache-invalidation subscriber) happens seconds later, in a different process, with no HTTP request in scope to attach a log line to.
- **A separate analytics store.** `03-elasticsearch-vs-sql-server.md` / `04-elasticsearch-vs-mongodb.md` land click events in Elasticsearch via a consumer that is itself a separate deployable, with its own failure modes (indexing lag, mapping errors, cluster pressure).

The practical consequence: a single user-visible symptom — "this redirect was slow" or "this click didn't show up in analytics" — now has a *causal path* that spans an API instance, a cache node, a broker, a consumer process, and a search cluster. Grepping one instance's console log, the only debugging tool v1 needed, cannot answer "which of these five components is actually the bottleneck for this one request," because nothing ties the log lines from those five components together as belonging to the same request. Without a shared identifier threaded through every hop, on-call debugging degrades from "read the log" to "guess which instance handled it, then guess which downstream step failed" — a search problem that gets strictly worse as instance count and consumer count grow, exactly when it matters most (100M events/day means the failure that mattered is one of many).

Observability at this scale is therefore not "more logging" — it is **making the request path traceable as a graph, not a line**, and doing so without recreating the per-instance blind spots v1 accepted deliberately for its scope (`nfr-reliability-and-availability.md` Section 1's "best-effort, no SLA" framing still applies to *availability*; this document is about being able to *diagnose* what happened, which is a prerequisite for improving availability, not a contradiction of that framing).

---

## 2. Distributed Tracing

### 2.1 Standard: OpenTelemetry, W3C Trace Context

**Decision: instrument with the OpenTelemetry .NET SDK, propagating the W3C Trace Context standard (`traceparent`/`tracestate` headers) end to end.**

- OpenTelemetry is the .NET-ecosystem-standard choice: it is vendor-neutral (the same instrumentation exports to Elastic APM, Application Insights, Grafana Tempo, or any OTLP-compatible backend without a rewrite), it is built on top of the `System.Diagnostics.Activity` API already present in .NET's base class library (no bespoke tracing primitive to maintain), and ASP.NET Core, `HttpClient`, and `Microsoft.Data.SqlClient`/EF Core all ship first-party OpenTelemetry instrumentation packages that auto-generate spans for inbound requests, outbound HTTP calls, and database commands with no manual span-creation code at most call sites.
- **W3C Trace Context** (the `traceparent` header) is the propagation format: a single trace ID identifies "everything that happened because of this one originating request," and each hop (API → cache → DB, API → broker → consumer) creates a child span under that same trace ID. This is what turns "five separate log streams" back into one navigable timeline per request.

### 2.2 Propagation across the synchronous path

For the ordinary in-request path — API instance → Redis → SQL Server/Elasticsearch — OpenTelemetry's ASP.NET Core and `HttpClient`/Redis/EF Core instrumentation packages propagate the trace context automatically once registered in `Program.cs`; this requires configuration (`AddOpenTelemetry().WithTracing(...)`), not manual header plumbing, for every hop that stays inside a single logical request/response.

### 2.3 Propagation across the async boundary — the part v1 never had to solve

The harder case, and the one that actually matters for this document's scope, is the message broker. A `UrlClicked` event published to the broker (`05-kafka-comaporison.md`) is consumed *later*, by a *different process*, outside the original HTTP request's lifetime — there is no ambient `HttpContext` for auto-instrumentation to piggyback on.

- **The producer must attach the current trace context to the message as it publishes it.** Concretely: when the API instance publishes `UrlClicked` (or `ShortUrlInvalidated`, per `07-redis-caching-and-invalidation.md` Section 5.2), it serializes the current `Activity`'s `traceparent` value into the message's headers/metadata (most brokers — Kafka, Azure Service Bus, SQS/SNS via message attributes — support arbitrary key-value headers alongside the payload; this is exactly what they're for).
- **The consumer must extract that context and start its processing span as a child of it, not as a new root trace.** OpenTelemetry's messaging instrumentation conventions (`ActivityContext.Extract` from the message headers, then `StartActivity` with that context as parent) make this a few lines at the consumer's message-handling entry point, not a bespoke correlation scheme.
- **Result:** a single trace ID connects "API instance received the redirect request" → "cache lookup" → "click event published to the broker" → "consumer picked it up 400ms later on a different machine" → "document indexed into Elasticsearch." An engineer investigating "why is this click missing from analytics" searches one trace ID and sees the entire path, including exactly which hop stalled or failed — this is the direct fix for the failure mode described in Section 1.
- This is additive to, not a replacement for, the Outbox pattern's own delivery guarantees (`20-outbox-pattern.md`) — tracing tells you *where* a message's processing went wrong; the outbox/broker's at-least-once delivery is what ensures the message itself was not silently dropped.

---

## 3. Metrics and Cardinality

### 3.1 What to measure

At the volumes this review targets (up to 100M events/day, ~1,200 events/sec average, bursting to 6,000–12,000/sec per `05-kafka-comaporison.md` Section 2), metrics — not traces — are the primary signal for "is the system healthy right now," because traces at this volume are necessarily sampled while aggregate metrics are cheap to compute on every request. Minimum metric set:

- **Request rate** — requests/sec per endpoint (create, redirect, metadata), tagged by HTTP status class (2xx/4xx/5xx), not raw status code.
- **Latency percentiles per endpoint** — p50/p95/p99, not just an average (an average hides the tail that `nfr-performance.md`'s targets are actually about). Recorded as a histogram, not a pre-aggregated average, so percentiles can be computed correctly after the fact rather than averaged-of-averages (a well-known statistical error at aggregation time).
- **Cache hit rate** — hits vs. misses against the Redis tier (`07-redis-caching-and-invalidation.md`), as a ratio over a rolling window. This is the earliest leading indicator of a cache-tier problem (a cold cache after a deploy, an eviction storm under `maxmemory` pressure) before it shows up as elevated database load or redirect latency.
- **Queue depth / consumer lag** — how far behind the broker's consumers are (e.g., Kafka consumer group lag, or the equivalent "approximate messages visible/in-flight" for a queue-based broker per `05-kafka-comaporison.md`'s options). This is the leading indicator for the async pipeline the way cache hit rate is the leading indicator for the read path — a growing lag means click-indexing/cache-invalidation is falling behind real time, before any single request fails outright.
- **Error rate** — failed requests/sec and failed message-processing attempts/sec, both as rates (not raw counts), so they're comparable across traffic levels and support the alerting thresholds in Section 5.

### 3.2 Cardinality — the explicit warning

**Never tag a metric with a raw, unbounded, per-entity value.** This is the single most common way a metrics pipeline is destroyed at this event volume, and it is worth stating as a hard rule rather than a suggestion:

- **Do not** tag redirect-latency or request-count metrics by raw short code, user ID, session ID, IP address, or any other value with effectively unbounded cardinality. At up to 100M events/day against a growing corpus of short codes, a metric tagged by short code creates a new time series *per code* — millions of them — which is exactly the failure mode metrics backends call a "cardinality explosion": ingestion cost, storage cost, and query latency on the metrics backend all scale with the number of *distinct label combinations*, not the number of events, and an unbounded label turns a cheap aggregate metric into an accidental per-entity database no monitoring backend is built to be.
- **Do** aggregate by low-cardinality dimensions instead: endpoint name, HTTP status class, cache hit/miss (boolean), consumer/topic name, deployment region/instance pool. These are the dimensions Section 3.1's metrics are already scoped to, and each has a small, bounded, known set of values.
- **If per-entity investigation is genuinely needed** (e.g., "why is this specific short code's cache entry always missing"), that is exactly what distributed tracing (Section 2) and structured logs (Section 4) are for — traces and logs are designed to carry high-cardinality, per-request detail; metrics are designed to be cheap aggregates. Routing high-cardinality questions to the trace/log tier instead of the metrics tier is the load-bearing design decision here, not an afterthought.

---

## 4. Logging Strategy

- **Structured logging, not raw text.** Every log entry is emitted as a structured event (JSON, or an equivalent key-value structure via `ILogger`'s structured logging / message templates, e.g., `_logger.LogInformation("Redirect resolved for {ShortCode} in {ElapsedMs}ms", code, elapsed)`), never as a formatted string built with interpolation. This is what makes logs queryable at volume — "show me every redirect over 200ms" is a structured field filter, not a regex over free text — and it is consistent with `coding-giudelines.md` Section 4's own preference for string interpolation *for human-facing messages* while keeping logging calls on the structured/templated overload so the underlying sink still receives discrete fields.
- **Every log entry includes the correlation/trace ID from Section 2.** This is what makes "grep all logs for this one request across every instance and every consumer" possible — the trace ID is the join key between the tracing system and the logging system, so an engineer can pivot from "I see a slow trace" to "show me every log line with that trace ID" (or the reverse) without a separate correlation mechanism. In ASP.NET Core, this is largely automatic once OpenTelemetry logging integration is enabled — the current `Activity`'s trace ID is attached to the logging scope for every request, and consumers attach it explicitly from the propagated header (Section 2.3) at the start of message processing.
- **What must NOT be logged.** `nfr-security.md` Section 6 already establishes the project's PII-safe logging decision for v1 — raw IP addresses and other identifying click data are never persisted, even in the analytics store itself. That decision extends unchanged to this document's scope: it applies with equal force to trace spans, span attributes, and log lines, not just the `AccessEvent`/click-record table Section 6 was originally written about. Concretely, no trace attribute, log field, or metric tag introduced by this document may carry a raw IP address, full user-agent string, or other directly-identifying value — the same coarse/aggregated substitutes `nfr-security.md` Section 6 already defines for the analytics record apply to the observability pipeline. This is not a new decision; it is the existing one, applied consistently across every place data about a request now flows.

---

## 5. Alerting

Alerts are defined against the actual targets already established in `nfr-performance.md`, not new numbers invented for this document — the point of instrumenting Sections 2–4 is to make those existing targets actionable, not to set a second, competing set of thresholds. Mechanically, all five alerts below are implemented as Grafana Alerting rules — Grafana's native alerting engine queries the metrics store (Section 3) and Loki (Section 4) directly and evaluates thresholds/sustained-window conditions against them, so no separate alerting product is needed on top of the stack recommended in Section 6.

1. **Redirect latency SLO burn** — alert when redirect p99 latency (Section 3.1's histogram) exceeds `nfr-performance.md` Section 2's p99 target for a sustained window (e.g., 5 minutes), not on a single breach — a single slow request is noise; a sustained breach means the target this system was designed to hold is actually being missed.
2. **Redirect latency p95 warning** — a lower-severity, earlier warning at the `nfr-performance.md` Section 2 p95 target, so on-call gets a heads-up before the harder p99 alert fires, giving time to investigate (e.g., a cache hit-rate drop, per alert 3) before it becomes a customer-visible SLO breach.
3. **Cache hit-rate drop** — alert when the Redis hit rate (Section 3.1) falls below an established baseline (set from observed steady-state, per `07-redis-caching-and-invalidation.md` Section 3.2's own note that sizing/thresholds come from observed behavior, not a guess) for a sustained window. A falling hit rate is the earliest signal of a redirect-latency SLO breach about to happen (more traffic falling through to the database), so this alert exists to catch the cause before alert 1 catches the effect.
4. **Consumer lag growth** — alert when broker consumer lag (Section 3.1) grows monotonically over a sustained window rather than draining, meaning the async pipeline (click indexing, cache invalidation per `07-redis-caching-and-invalidation.md` Section 5) is falling behind real time. Left unaddressed, this directly threatens the "seconds, normal case" invalidation-staleness guarantee that same document commits to in its Section 5.4, degrading it toward the 5-minute worst case.
5. **Error rate spike** — alert when the error rate (Section 3.1) for the redirect or create path exceeds a fixed threshold (e.g., >1% of requests over a 5-minute window) — a symptom broad enough to catch failures Sections 1–4 above don't individually name (a downstream dependency outage, a bad deploy), acting as the general-purpose backstop alert alongside the four SLO-specific ones above.

---

## 6. Tooling Recommendation

**Recommendation: OpenTelemetry .NET SDK for instrumentation (traces, metrics, and logs) exporting via OTLP to a Grafana observability stack — Grafana Loki for log aggregation, Grafana Tempo (or an OTLP-compatible trace backend) for trace storage, and Grafana for dashboards, trace visualization, and alerting. Metrics land in a Prometheus-compatible store (e.g., Grafana Mimir) queried by the same Grafana instance — the standard open-source "LGTM" pattern (Loki, Grafana, Tempo, Mimir).**

Justification:

- **OpenTelemetry as the instrumentation layer is unchanged and is not a close call.** It is the current .NET/CNCF-standard approach, ships first-party auto-instrumentation for ASP.NET Core/`HttpClient`/EF Core, and — critically — decouples *instrumentation* from *backend choice*. Picking OpenTelemetry now does not lock this system into any one observability vendor; the export target can change later without touching a single `_logger.LogInformation` call or span in application code. This part of the recommendation carries over directly from the instrumentation rationale in Section 2.1.
- **The Grafana stack (Loki + Tempo/OTLP + Grafana) is separate infrastructure from the Elasticsearch cluster used for analytics** (`03-elasticsearch-vs-sql-server.md` / `04-elasticsearch-vs-mongodb.md`) — this is a deliberate trade-off, stated honestly rather than glossed over. Unlike a recommendation that reuses an already-justified Elasticsearch cluster, this means standing up, operating, and scaling an *additional* infrastructure component purely for telemetry: Loki for logs, a metrics store, and (if trace storage is needed beyond ad hoc sampling) Tempo, all sitting alongside SQL Server/Elasticsearch and Redis as yet another platform to run. That operational surface is the honest cost of this choice, and it is larger than "reuse what you already have."
- **The counter-benefit is why this is still the recommendation now that the stack has been specified explicitly:** Grafana Loki/Tempo/Mimir is a best-of-breed, purpose-built open-source observability stack rather than a search engine repurposed for telemetry — Loki's log-storage model (index the labels, not the full text) is materially cheaper at this log volume than a general-purpose search index, Grafana's dashboarding and native alerting are widely considered best-in-class for this exact use case, and the stack has a strong, well-trodden fit with Kubernetes/cloud-native deployments (official Helm charts, Prometheus-compatible scraping, sidecar/operator patterns), which matters if `01-create-path-extreme-scalability.md`'s horizontally-scaled instances run on Kubernetes or a similar orchestrator. Cost profile is also favorable: Loki's label-only indexing keeps storage costs down relative to full-text-indexed logs at 100M-events/day scale, and every component in the stack is open-source with no per-seat or per-GB vendor licensing.
- **Named alternatives, and when they'd actually be the better call:** Elastic Observability (APM + Logs on the same Elasticsearch cluster already operated for analytics) is the stronger choice specifically if minimizing the number of distinct infrastructure platforms is the priority over having a purpose-built observability stack — it avoids the "additional component" cost noted above at the price of a less log-volume-efficient storage model and a less Kubernetes-native operational story. Application Insights (Azure Monitor) is the stronger choice if this system is deployed primarily on Azure and the team wants a fully managed, zero-infrastructure-to-run backend — it also speaks OpenTelemetry natively via OTLP, so switching to it later is an exporter-configuration change, not a re-instrumentation effort. Either alternative is legitimate if its specific precondition (a hard preference for minimizing platform count, or Azure-first managed hosting) applies; absent that, the Grafana/Loki/OTEL stack specified here is the recommendation.

---

## 7. Summary of Decisions

| Concern | Decision | Traces to |
|---|---|---|
| Why v1's model breaks down | Single-instance log-grepping cannot follow a request across N API instances, a distributed cache, a broker, and a separate analytics store | Section 1 |
| Tracing standard | OpenTelemetry .NET SDK; W3C Trace Context (`traceparent`) propagation | `nfr-reliability-and-availability.md` §4 (extends, does not replace) |
| Async propagation | Producer attaches `traceparent` to broker message headers; consumer extracts it and starts a child span, not a new root | `05-kafka-comaporison.md`, `20-outbox-pattern.md` |
| Metrics | Request rate, per-endpoint latency percentiles, cache hit rate, queue depth/consumer lag, error rate — all as low-cardinality aggregates | `07-redis-caching-and-invalidation.md` |
| Cardinality rule | Never tag metrics by raw short code/user ID/IP; use bounded dimensions only; route per-entity questions to traces/logs instead | Section 3.2 |
| Logging | Structured (not text) logs; every entry carries the trace ID; PII exclusion unchanged from v1 | `nfr-security.md` §6 (cross-referenced, not duplicated) |
| Alerting | 5 SLO-driven alerts: redirect p99 burn, redirect p95 warning, cache hit-rate drop, consumer lag growth, error rate spike — implemented as Grafana Alerting rules | `nfr-performance.md` §2 |
| Tooling | OpenTelemetry SDK (instrumentation) + Grafana stack (Loki for logs, Tempo/OTLP for traces, Prometheus-compatible store for metrics, Grafana for dashboards/alerting) — separate infrastructure from the analytics Elasticsearch cluster | `03-elasticsearch-vs-sql-server.md`, `04-elasticsearch-vs-mongodb.md` |
