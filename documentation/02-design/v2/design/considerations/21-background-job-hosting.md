# Consideration 21 — Background Job Hosting: Long-Running Worker Service vs. Azure Functions

**Version:** v2 (extreme-scalability review)
**Status:** Draft
**Scope:** This document answers one question — *for this system's background/async processing needs, should we host that work as a long-running .NET hosted process ("Windows background job") or as Azure Functions?* It does not re-decide *whether* any given async workload is needed (that's `20-outbox-pattern.md` and `05-kafka-comparison.md`'s job) — it assumes those workloads exist and asks only *where they should run*.

**Companion documents (not duplicated here, cross-referenced by filename):**
- `20-outbox-pattern.md` — establishes the outbox relay as a concrete background workload (if/when the exception in that document's §3 applies): a process polling `OutboxMessage` for unpublished rows and publishing them to the broker.
- `05-kafka-comparison.md` — establishes the broker consumers (analytics-indexing into Elasticsearch, cache-invalidation, and a candidate future async malicious-domain re-check) as long-lived, stateful consumer-group workloads, and recommends Azure Service Bus Topics/Subscriptions as the default broker.
- `07-redis-caching-and-invalidation.md` §5.2 — names the cache-invalidation "subscriber (any API instance, or a small dedicated worker)" as a concrete consumer of the invalidation event.
- `design-guidelines.md` (global) — defines this project's layered solution structure (`Api` / `Application` / `Domain` / `Infrastructure` / `Common`) that any hosting decision must fit into without violating the dependency-direction rules.

---

## 0. Interpreting "Windows Background Job"

The review prompt asks to compare "Windows background job" against Azure Functions. Taken literally, "Windows background job" is dated phrasing — a hint toward classic Windows Service hosting, which predates .NET's cross-platform `BackgroundService`/`IHostedService` model and this project's Azure/cloud-native framing.

This document interprets it as: **a persistently-running .NET Worker Service** — a process built on `Microsoft.Extensions.Hosting`'s `BackgroundService`/`IHostedService`, the same hosting abstraction ASP.NET Core itself is built on. That process can be deployed as:

- a **Windows Service** (literal reading, viable but not this project's target environment),
- a **Linux systemd service**, or
- — most realistically, given this v2 review's cloud-native assumptions (Azure Service Bus/Kafka, Redis, containerized API) — a **containerized long-running process/pod** (Docker image, deployed to Azure Container Apps, AKS, or an App Service container).

The mechanics (`BackgroundService.ExecuteAsync` running an infinite loop or event-driven read loop, `IHostedService.StartAsync`/`StopAsync` lifecycle hooks, graceful shutdown via `CancellationToken`) are identical across all three; only the OS/orchestration wrapper differs. **"Option A" below means this Worker Service model, containerized, unless a specific deployment target is called out.**

**Option B** is Azure Functions: serverless, event-triggered compute (e.g., a `ServiceBusTrigger`/`QueueTrigger`/`EventHubTrigger` function invoked once per message or per batch), most relevantly on the **Consumption plan** (scale-to-zero, per-execution billing) since that's the plan whose trade-offs are most distinct from an always-on worker. (The Premium/Dedicated Functions plans exist and blur some of these trade-offs — noted where relevant, but Consumption is the plan actually being compared here, since choosing Premium mostly to avoid cold starts and get always-warm instances is, cost-and-operationally, converging back toward "just run a worker.")

---

## 1. This System's Actual Background Workloads

Concretely, what needs to run outside the request pipeline at v2 scale:

| Workload | Source | Shape |
|---|---|---|
| Outbox relay (poll `OutboxMessage`, publish to broker) | `20-outbox-pattern.md` §4.3 — built only if the narrow exception applies | Continuous polling loop, or CDC-tail; not built for `UrlCreated` today per that document's recommendation, but the mechanism is designed and may be needed for a future high-consequence event |
| Broker consumer: analytics-indexing (writes to Elasticsearch) | `05-kafka-comparison.md` §1, `03-elasticsearch-vs-sql-server.md` | Long-lived consumer-group member, continuous stream, ~1,200 events/sec average / ~6,000–12,000/sec peak at 5-year scale (`05-kafka-comparison.md` §2.3) |
| Broker consumer: cache-invalidation subscriber | `07-redis-caching-and-invalidation.md` §5.2 | Long-lived consumer-group member, continuous stream, low-latency-sensitive (feeds the "seconds, not 5 minutes" staleness bound) |
| Broker consumer: async malicious-domain re-check (candidate, not yet committed) | `05-kafka-comparison.md` §6 | Long-lived consumer-group member if adopted; per-event work, not high-frequency |
| Data-retention/cleanup sweep (e.g., purging soft-deleted rows, expired `OutboxMessage` rows past `PublishedAtUtc` retention per `20-outbox-pattern.md` §5.2) | Implied by `20-outbox-pattern.md` §5.2's "needs its own retention/cleanup policy" and standard soft-delete hygiene per `data-design-guidelines.md` | Scheduled, once/day (or a few times/day), short-lived, no persistent connections needed between runs |
| Periodic reconciliation sweep (RowVersion delta-diff against the index, backstop named in `20-outbox-pattern.md` §3) | `20-outbox-pattern.md` §3, §6.3 | Scheduled, periodic (e.g., every few minutes to hourly), bounded-duration batch query + diff, no persistent state between runs |

Two clearly different workload *shapes* fall out of that table, and that distinction drives the rest of this document.

---

## 2. Workload Shape: Continuous Stream vs. Bursty/Scheduled Work

**Continuous, high-throughput, stateful stream consumption** — the broker consumers (analytics-indexing, cache-invalidation) and, if built, the outbox relay:

- They never stop running. There is no "invocation boundary" — a consumer holds a broker connection, participates in a consumer group (or, for a queue/topic model, holds a subscription session), and processes a steady arrival rate that at 5-year scale averages ~1,200 events/sec with bursts to ~6,000–12,000/sec (`05-kafka-comparison.md` §2.3).
- The natural unit of work is "keep a connection open and drain a stream," not "run once and exit."

**Bursty, infrequent, independent work** — the retention-cleanup sweep and the reconciliation job:

- These have a natural start and end: "run once, do a bounded amount of work, exit." A nightly cleanup job does not need to hold anything open between midnight and the next midnight.
- They are trivially triggerable on a schedule and don't share state across runs (each run queries fresh, does its work, and is done).

These two shapes have different *ideal* hosting models, and the honest answer is not "pick one for everything" — it's **match the hosting model to the shape**, which §5 makes concrete.

---

## 3. Cost Model — Reasoned Through This System's Actual Volume

### 3.1 Long-running worker: constant baseline cost

A containerized Worker Service (Option A) costs roughly the same whether it processes 10 events/sec or 10,000 events/sec, up to the point where the container needs to scale out (more replicas/partitions). The bill is "N container instances running 24/7," sized for sustained throughput with headroom for peak.

### 3.2 Azure Functions Consumption plan: scales to zero, per-execution billing

Functions on the Consumption plan bill per execution (invocation count) plus GB-seconds of memory × duration, and scale down to zero instances when idle. This is the right shape for a workload that's mostly idle with occasional bursts — you pay ~nothing when there's nothing to do.

### 3.3 Where the crossover happens, and why it matters here

Per-execution billing is attractive at low, spiky volume because "mostly idle, cheap when idle" beats "always running, paying a floor cost." It stops being attractive as volume climbs and *stays* high, because:

- **Per-invocation billing has a floor cost per event** (execution count + GB-seconds), and that cost is paid *every single time*, with no economy of scale from batching connections/overhead the way a long-running process gets for free (one broker connection amortized across millions of messages, vs. Functions needing to either batch aggressively or pay per-message overhead).
- At **~1,200 events/sec average, sustained continuously, 24/7** (not bursty — this is the steady-state 5-year average per `05-kafka-comparison.md` §2.2), that's roughly **~104 million invocations/day** if triggered per-message, or a much smaller number if the trigger batches (Service Bus/Event Hub triggers in Functions do support batched invocation, which meaningfully changes this math — batching 100 messages/invocation turns ~104M/day into ~1M/day). Even with realistic batching, a Consumption-plan function processing a *sustained, unbounded, 24/7* stream is functionally never idle — it never scales to zero, defeating the core cost advantage of serverless (paying only when there's work). At that point you are paying serverless per-execution pricing for what is, in effect, an always-on workload — and Consumption-plan per-GB-second pricing at 100% utilization is generally *more* expensive than a right-sized reserved container/VM running the equivalent workload continuously, because the serverless premium exists to subsidize elasticity you're no longer using.
- A **long-running worker**, by contrast, is priced once (a fixed number of container replicas sized for the sustained ~1,200/sec average with burst headroom for the ~6,000–12,000/sec peak) and that cost doesn't move with event count within that capacity band — the more throughput you push through it, the better the per-event cost gets, which is the opposite curve from per-invocation billing.

**For this system's high-throughput consumers specifically: a long-running worker is the cheaper model at sustained scale, and the reasoning is explicit, not asserted** — the workload is never idle, so it never realizes serverless's core value proposition (paying only for idle-free-ness), while paying serverless's per-execution premium the entire time.

**For the low-frequency scheduled jobs (retention cleanup, reconciliation sweep): the opposite conclusion holds.** These run for minutes, a few times a day at most. A dedicated always-on container for a job that's active <1% of the day is paying ~99% of its cost for idle time. A Consumption-plan Function (or Azure Functions Timer trigger) genuinely scales to zero between runs and bills only for the minutes it actually executes — this is the textbook case serverless billing is good at, and it is cheaper here, not just architecturally cleaner.

---

## 4. Cold Start — Does It Matter for This System's Background Work?

Azure Functions on the Consumption plan pay a cold-start penalty (container/host initialization) when scaling up from zero after an idle period — commonly hundreds of milliseconds to a few seconds depending on runtime/language and dependencies.

This matters when a function is on a **user-facing latency path** — e.g., an HTTP-triggered function backing an API a user is waiting on. It matters much less, or not at all, when the triggered work is **already off the critical request path**, which is the entire point of this system's async-decoupling design:

- The broker consumers exist specifically so that create/fetch requests never block on analytics indexing or cache invalidation (`05-kafka-comparison.md` §1: "producers... should never block on... a downstream consumer being slow"). A consumer taking an extra second to cold-start doesn't delay the HTTP response that already returned.
- The retention-cleanup and reconciliation jobs are scheduled batch work with no caller waiting synchronously on their completion.
- The one workload where cold start *could* have a visible effect is the cache-invalidation subscriber, since `07-redis-caching-and-invalidation.md` §5.2/§6 sets a "low single-digit seconds" normal-case staleness target backed by active invalidation. A cold Function instance adding a second or two of latency to an invalidation event is a real (if bounded — the 5-minute TTL backstop still holds regardless, per that document's §6) degradation of that "seconds not minutes" target — one more reason that specific subscriber is better as an always-warm, long-running consumer (§5) than as a per-invocation cold-start-prone function.

**Conclusion: cold start is largely irrelevant for this system's background work, precisely because none of it is on the synchronous request path — that's a design property already established elsewhere (`05-kafka-comparison.md` §1), not a new argument invented here.** The one workload with a latency SLA sensitive enough to notice (cache invalidation) is also, independently, the workload with the strongest architectural case for a long-running consumer (§5) — the two arguments reinforce, not compete.

---

## 5. Operational Fit With the Existing Broker Architecture

This is the strongest, most concrete argument in this document, and it's specific to what `05-kafka-comparison.md` already committed this system to.

`05-kafka-comparison.md` §4 recommends Azure Service Bus Topics/Subscriptions (with Kafka as a named upgrade path) specifically because it gives "multiple independent consumers reading the same stream" — each named consumer (analytics-indexing, cache-invalidation, future malicious-domain re-check) is a **subscription** on the topic, and if the upgrade path to Kafka is taken, each is a **consumer group** with tracked offsets.

That model is architecturally a **long-running-process model**, not a per-invocation model:

- **Consumer-group membership / subscription session state** is inherently stateful and long-lived. A Kafka consumer group tracks which broker instance owns which partition, and that ownership is negotiated and held for the *lifetime of a connected consumer* — rebalancing happens when a consumer joins or leaves the group. A Functions-per-invocation model has no natural notion of "this invocation is a member of a group that persists between invocations" — the Azure Functions Service Bus/Event Hub trigger bindings work around this by having the Functions *host* (not each invocation) manage the underlying subscription/checkpoint client, which in practice means the Functions runtime itself is running something structurally equivalent to a long-lived consumer underneath the serverless abstraction — you don't actually escape the long-running-connection model, you just hide it inside the platform, while giving up direct control over its tuning (prefetch count, batch size, checkpoint cadence).
- **Offset/checkpoint management** (Kafka offsets, or Service Bus's message-lock/complete semantics) is naturally a property of a process that's continuously pulling from the stream and committing progress as it goes. A cleanly stateless "wake up, process one message, exit" model has to re-establish that context on every invocation, which is exactly the overhead a long-running worker avoids by construction.
- **Connection pooling to Redis and the database** — every consumer in this system needs to talk to Redis (to evict/refresh cache entries, `07-redis-caching-and-invalidation.md`) and/or the primary datastore (to read/write rows for indexing, reconciliation, or outbox state). A long-running worker opens these connections once and reuses them for the life of the process — the idiomatic, efficient pattern for both `StackExchange.Redis` (which explicitly recommends one long-lived multiplexer instance) and EF Core/ADO.NET connection pooling. A per-invocation Functions model either re-establishes connections per invocation (latency and connection-churn cost, and a real risk of exhausting Redis/DB connection limits under the ~1,200/sec sustained volume this system runs at) or relies on the Functions host's own instance-reuse behavior to approximate the same thing — which, again, is the platform quietly running a long-lived process underneath a "serverless" label.

**Concretely for this system: forcing the analytics-indexing and cache-invalidation consumers into a Functions model doesn't remove the long-running-process complexity described in `05-kafka-comparison.md` — it just relocates it into the Functions host's internals, where this project has less visibility and control, while paying Consumption-plan per-execution pricing (§3) for a workload that's never actually idle.** A first-party `BackgroundService` gets the same underlying behavior (a persistent client, connection pooling, consumer-group membership) directly and transparently, using the same `Microsoft.Extensions.Hosting` primitives this project's `Api` project is already built on (`design-guidelines.md` §6's DI/hosting conventions extend naturally to a Worker Service host).

By contrast, the retention-cleanup and reconciliation jobs have **no persistent connection or group-membership state to preserve between runs** — each run opens a DB connection, does its work, and closes it, which is exactly what a Functions Timer trigger's per-invocation lifecycle is built for, with no fit penalty.

---

## 6. Comparison Table

| Dimension | Option A — Long-running Worker Service (containerized `BackgroundService`) | Option B — Azure Functions (Consumption plan, event-triggered) |
|---|---|---|
| **Ideal workload shape** | Continuous, high-throughput, stateful stream consumption | Bursty, infrequent, stateless, independently-triggerable work |
| **Cost at ~1,200 events/sec sustained (5-yr avg)** | Fixed baseline, sized once, improves per-event as throughput grows | Never scales to zero (workload isn't idle) → pays per-execution premium 24/7; more expensive than a right-sized worker at this sustained volume (§3.3) |
| **Cost at "once/day, minutes of work" (cleanup/reconciliation)** | Pays for an idle container ~99% of the day | Scales to zero between runs; pays only for execution time — cheaper here |
| **Cold start impact** | None (always warm) | Present on Consumption plan, but largely irrelevant since none of this system's background work is on the synchronous request path (§4) — except the cache-invalidation subscriber's "seconds not minutes" target, where it's a real if minor negative |
| **Consumer-group / offset / subscription-session fit** | Natural fit — a persistent client owns group membership and checkpoint state directly | Poor fit for a stateless per-invocation model; the Functions host approximates a long-running consumer internally, with less visibility/control than a first-party worker (§5) |
| **Redis/DB connection reuse** | One pooled/multiplexed connection for the process lifetime — idiomatic for `StackExchange.Redis` and EF Core | Either re-establishes connections per invocation or relies on host instance-reuse; adds risk of connection-limit exhaustion at this volume |
| **Fit with this project's layered architecture** | Fits as a new hosted-process entry point alongside `Api`, referencing `Application`/`Infrastructure` the same way `Api` does (§7) | Would need its own separate project/deployment artifact with its own trigger bindings; duplicates DI/config wiring outside the established `Api` composition-root pattern |
| **Operational surface** | One more long-lived process to deploy/monitor (container health checks, restarts) — but a single, well-understood unit | No infrastructure to patch, but a separate deployment model (Functions Core Tools/Azure Functions runtime) alongside the containerized `Api`, and less transparency into its internal consumer/connection behavior |
| **Best fit in this system** | Broker consumers (analytics-indexing, cache-invalidation), outbox relay if built | Retention-cleanup sweep, reconciliation sweep, any genuinely on-demand/sporadic future work (e.g., on-demand report generation, if added) |

---

## 7. Recommendation

**Split by workload, not a single winner** — consistent with §2's observation that this system has two genuinely different workload shapes:

1. **Long-running Worker Service (Option A) for:**
   - The analytics-indexing broker consumer.
   - The cache-invalidation subscriber (`07-redis-caching-and-invalidation.md` §5.2).
   - The candidate future async malicious-domain re-check consumer (`05-kafka-comparison.md` §6), if adopted.
   - The outbox relay (`20-outbox-pattern.md` §4.3), if/when its narrow exception applies.

   Reason: all four are continuous, high-throughput (up to ~1,200/sec sustained, bursting to ~6,000–12,000/sec at 5-year scale), stateful-connection workloads where a long-running process is both architecturally correct (§5) and cheaper at this volume (§3.3) than per-invocation billing, and where cold start is a non-issue because none of this work sits on the synchronous request path (§4).

2. **Azure Functions (Option B) for:**
   - The retention-cleanup sweep (purging expired soft-deleted rows / stale `OutboxMessage` rows).
   - The periodic reconciliation sweep (`20-outbox-pattern.md` §3's RowVersion delta-diff backstop), if it's implemented as a scheduled batch rather than folded into the same worker process.
   - Any genuinely sporadic, independently-triggerable future workload with no shared state between runs (e.g., on-demand report generation) — none is committed in this system today, but the model is a good fit if one is added.

   Reason: this work is bursty and short-lived by nature — a Timer-triggered Function scaling to zero between runs is both the cheaper (§3.3) and operationally simpler choice, with no consumer-group or persistent-connection state to preserve, and cold start is irrelevant for a job nobody is waiting on synchronously.

This is a **deliberate split, not a compromise** — picking one hosting model for everything would either force the high-throughput consumers into an ill-fitting, more expensive serverless model (§3, §5), or force the once-a-day cleanup job into an unnecessarily always-on container paying for idle time it doesn't need (§3.3).

---

## 8. Deployment Note: Fitting the Worker Service Into This Project's Architecture

The recommended Worker Service(s) are new **hosted-process entry points**, architecturally parallel to `UrlShortner.Api`, not a new layer:

- A new executable project — e.g., `UrlShortner.Worker` (or one project per consumer, e.g. `UrlShortner.Worker.AnalyticsIndexer`, `UrlShortner.Worker.CacheInvalidator`, if independent scaling/deployment per consumer is wanted) — sits at the same level as `UrlShortner.Api` in the solution, and follows the **same dependency-direction rules** from `design-guidelines.md` §1: it references `Application` and `Infrastructure` (for DI composition, exactly as `Api`'s `Program.cs` does via `AddInfrastructureServices()`), never the reverse, and contains no business logic of its own — the actual indexing/invalidation/relay logic lives in `Application`/`Infrastructure` services, consistent with `design-guidelines.md` §7's Single Responsibility principle ("a controller changes only for HTTP-shape reasons" — by the same logic, a worker's `ExecuteAsync` changes only for hosting/loop-shape reasons, not business-rule reasons).
- It uses the identical `Microsoft.Extensions.Hosting` builder (`Host.CreateApplicationBuilder`) and DI container as `Api`'s `Program.cs`, registering the same `AddInfrastructureServices()`/`AddApplicationServices()` extension methods (`design-guidelines.md` §6) plus a `BackgroundService`-derived class implementing the consume/relay/poll loop. This means no divergent configuration or service-registration pattern between the API and the worker — one composition-root convention, two hosts.
- Consistent with this v2 review's cloud-native assumptions elsewhere (Service Bus/Kafka, managed Redis), the Worker Service is packaged as its **own Docker image**, built from the same repo/solution, and deployed as a **separate container/pod** from the `Api` image — independently scalable (e.g., scale consumer replicas by partition/subscription count, independent of API instance count) and independently restartable without affecting request-serving availability. In an orchestrated environment (Azure Container Apps, AKS), this is a second `Deployment`/container-app resource with its own health/liveness probe (an `IHostedService`-level readiness signal, e.g., "connected to the broker and consuming") rather than an HTTP health endpoint, since the worker exposes no HTTP surface of its own.
- The scheduled jobs (§7.2), if built as Azure Functions rather than folded into a worker's own internal timer, are a **separate deployment artifact** again (a Functions app), which is an accepted, deliberate exception to "one hosting model" — consistent with this document's split recommendation, not a contradiction of it.
