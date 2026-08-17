# 26 — Infrastructure & Deployment Design: Edge to Data Tier, and the AKS Elastic-Scaling Model

**Version:** v2 (extreme-scalability review)
**Status:** Draft — architectural consideration, not a committed decision
**Scope:** This document does **not** re-decide any component choice already made elsewhere in this series. Its job is to assemble the individually-decided pieces — CDN, Redis, Elasticsearch, the message broker, Worker Service consumers, database sharding — into **one coherent infrastructure/deployment topology**, and to answer the review prompt's specific ask: name a concrete Firewall/WAF service, a concrete Load Balancer layer, and a concrete Kubernetes elastic/auto-scaling model so that "server capacity increases automatically." Every component below is cross-referenced by filename to the document that made the actual decision — this document does not repeat those decisions' reasoning, only their place in the topology.
**Traceability:** `prompt@review-desig.md` ("Use CDN, Firewall, Load Balancer and worker computers and use Kubernetis elastic model from azure so server capacity will increase automatically. Create an infrastructure design for this").
**Companion documents (not duplicated here, cross-referenced by filename):**
- `06-output-caching-bff-cdn.md` — Cloudflare CDN choice, BFF split, edge-cache TTLs.
- `07-redis-caching-and-invalidation.md` — Redis tier (managed, clustered), cache-aside pattern, invalidation.
- `03-elasticsearch-vs-sql-server.md` — Elasticsearch for the click/analytics event store; SQL Server stays the relational system of record.
- `05-kafka-comaporison.md` — message-broker recommendation (Azure Service Bus Topics/Subscriptions as default; Kafka as a named upgrade path).
- `21-background-job-hosting.md` — Worker Service (containerized `BackgroundService`) for continuous broker consumers; Azure Functions for bursty/scheduled jobs.
- `10-database-partitioning-sharding.md` — SQL Server sharding strategy and the threshold at which it becomes necessary.
- `12-distributed-rate-limiting.md`, `22-security-reputation-hacking-authorization.md` — firewall/DDoS/WAF-relevant context this document turns into a concrete Azure service placement.

---

## 1. Why This Document Exists

Every prior v2 document in this series answers a **component-level** question — which cache, which search engine, which broker, which hosting model. None of them, individually, answers "how do these run together, on what infrastructure, edge to database, and what makes the compute tier grow and shrink automatically as load changes." That is a distinct question — topology and elasticity, not component selection — and it is what the review prompt asks for under "Infrastructure design." This document draws the whole picture once, so a reader can see the full request path and the full scaling model in one place instead of reconstructing it from eight separate documents.

---

## 2. Layer-by-Layer Topology, Edge to Data Tier

### 2.1 Layer 1 — Cloudflare CDN (edge cache + volumetric DDoS absorption)

**Already decided in `06-output-caching-bff-cdn.md`.** Every public request — overwhelmingly the redirect path, per that document's traffic-shape argument — hits a Cloudflare edge PoP first. A cache hit (bounded 30s TTL, per-`shortCode`) never leaves the edge. A cache miss proceeds inbound. `22-security-reputation-hacking-authorization.md` §2.3 names this same layer explicitly as the system's **primary DDoS-absorption layer** — Cloudflare's DDoS-protection product tier (not just its cache-rule configuration) should be enabled, since it is the only layer in this topology geographically distributed enough to absorb a volumetric flood before it consumes any Azure compute or bandwidth at all. This document adds nothing new here; it fixes Cloudflare as "Layer 1" in the assembled picture.

### 2.2 Layer 2 — Firewall / WAF: Azure Web Application Firewall on Azure Front Door

**New decision made by this document: Azure Web Application Firewall (WAF), deployed on Azure Front Door Premium.**

- **Why Front Door, not Application Gateway, as the WAF's host:** Azure offers WAF on two services — Front Door (a global, edge-anycast Layer 7 entry point) and Application Gateway (a regional Layer 7 load balancer/reverse proxy). Because Cloudflare already sits in front of everything as the true global edge (§2.1), the Azure-side entry point does not need to *be* the global edge a second time — it needs to be the point where traffic that has left Cloudflare and is inbound to this system's Azure footprint gets inspected against OWASP Core Rule Set managed rules (SQL injection, XSS, request-smuggling patterns) and custom rules (e.g., blocking known-bad ASN/IP ranges surfaced by the anomaly-detection gap `22-security-reputation-hacking-authorization.md` §2.1 names as unsolved) before it reaches any compute. Front Door Premium's WAF is chosen over Application Gateway's WAF specifically because Front Door is Azure's own global-anycast/CDN-adjacent product — it terminates TLS close to the Azure backbone edge nearest the client, which keeps the "Cloudflare → Azure" hop short, and it integrates natively with Front Door's own routing/health-probe model in front of AKS.
- **What it does, concretely:** inspects every request that reaches it against managed and custom WAF rule sets, blocks/logs/rate-limits at this layer *before* the request is forwarded to the load balancer (§2.3) or the AKS cluster (§2.4). This is the concrete Azure service answer to the review prompt's "Firewall" ask — not a generic "a firewall," but Azure WAF specifically, hosted on Front Door.
- **Relationship to Cloudflare:** this is deliberate defense-in-depth, not redundant duplication. Cloudflare's DDoS/edge layer and Azure WAF operate at different points and catch different things — Cloudflare absorbs volumetric/network-layer floods and serves cache hits before Azure is ever involved; Azure WAF inspects the remaining traffic's *application-layer* request shape (payloads, headers, query strings) once it is inbound to this system's own cloud footprint. Losing either layer does not silently remove the other's protection.
- **Relationship to origin-side controls:** `22-security-reputation-hacking-authorization.md` §2.3 already establishes the ordering — CDN first, origin-level rate limiting (`12-distributed-rate-limiting.md`'s Redis-backed sliding-window limiter, enforced inside the API pods) second. Azure WAF sits *between* those two, as a third, distinct control: it is not a substitute for the Redis-backed per-caller rate limiter (which enforces business-level quotas per user/IP), and it is not a substitute for Cloudflare's volumetric absorption (which operates at a scale and geographic distribution Azure WAF does not attempt to match). Each layer catches a different shape of abuse.

### 2.3 Layer 3 — Load Balancer: L4 vs. L7, and which Azure service does which job

Two distinct load-balancing jobs exist in this topology, at two different layers of the stack — the review prompt's "Load Balancer" ask resolves into both, not one:

| Layer | Azure service | OSI layer | What it actually does |
|---|---|---|---|
| **Global HTTP(S) routing / L7** | **Azure Front Door** (§2.2 — same service hosting the WAF) | L7 | Front Door's own routing engine is itself the L7 load balancer for this topology: URL-path-based routing (splitting the redirect BFF's traffic from the authenticated create/management API's traffic, per `06-output-caching-bff-cdn.md` §3's BFF split), health-probe-based failover, and (if a second Azure region is ever added, §4) global traffic distribution across regions. This is the layer that understands HTTP — host headers, paths, cookies. |
| **In-cluster L4 distribution to AKS nodes** | **Azure Load Balancer** (Standard SKU), provisioned automatically as the `Service type: LoadBalancer` / ingress-controller entry point for the AKS cluster | L4 | Distributes inbound TCP connections across the healthy AKS nodes/pods behind a given Kubernetes `Service`. This is the layer Kubernetes itself manages — every AKS cluster with a public-facing `Service` or ingress controller (commonly an NGINX or Application Gateway Ingress Controller (AGIC) inside the cluster) provisions an Azure Load Balancer under the hood to get traffic from "an Azure public IP" to "the right node/pod." It has no notion of HTTP paths or cookies — it balances connections, not requests-by-content. |

**How they compose:** Cloudflare (global edge, L7-aware caching) → Azure Front Door + WAF (global L7 routing and inspection) → Azure Load Balancer (regional L4 distribution into the AKS node pool) → the Kubernetes Service/ingress layer inside AKS (L7-aware routing from the cluster's edge to the correct pod, via the ingress controller — itself a pod running inside AKS, fronted by the Azure Load Balancer). This is the standard Azure pattern for internet-facing AKS workloads: Front Door/Application Gateway for global/regional L7 concerns, Azure Load Balancer for the L4 hand-off into the cluster, and an in-cluster ingress controller for the final L7 hop to a specific `Service`. No layer duplicates another's job; each solves the concern proper to its position.

### 2.4 Layer 4 — Compute Tier: Azure Kubernetes Service (AKS)

The compute tier runs as **two categories of workload, as separate Kubernetes `Deployment` objects, on separate node pools (§4)**:

- **API pods** — the redirect BFF and the authenticated create/management API (`06-output-caching-bff-cdn.md` §3's split is preserved here as two separate `Deployment`s / container images, each independently scaled per §3 below). These are the pods the Azure Load Balancer and ingress controller route HTTP traffic to.
- **Background consumer pods** — the Worker Service containers from `21-background-job-hosting.md` (analytics-indexing consumer, cache-invalidation subscriber, outbox relay if built), each its own `Deployment`, with **no HTTP `Service`/ingress exposure at all** — they hold outbound connections to the broker (Service Bus/Kafka), Redis, and the database, and expose only a liveness/readiness probe (`21-background-job-hosting.md` §8: "an `IHostedService`-level readiness signal... rather than an HTTP health endpoint").

Both categories run as ordinary containerized .NET 9 processes inside AKS — no change to the application code from what `21-background-job-hosting.md` §8 already specifies (`Host.CreateApplicationBuilder`, `AddInfrastructureServices()`/`AddApplicationServices()`, one Docker image per host project). What AKS adds is the orchestration layer: scheduling, restart-on-failure, rolling deploys, and — the review prompt's actual ask — the elastic scaling model in §3.

Azure Functions (`21-background-job-hosting.md` §7's Option B for the retention-cleanup and reconciliation sweeps) sit **outside AKS entirely**, as their own separately-deployed Functions app — this is the deliberate, named exception `21-background-job-hosting.md` §8 already calls out, not an oversight in this topology.

### 2.5 Layer 5 — Data Tier

Every data-tier component below is a decision already made in its own document; this section only fixes *where* each one sits relative to AKS.

| Component | Decision (cross-referenced) | Placement relative to AKS |
|---|---|---|
| **Relational store — SQL Server** (partitioned/sharded past the threshold in `10-database-partitioning-sharding.md` §6) | `10-database-partitioning-sharding.md` | **Outside AKS** — Azure SQL Database (or SQL Server on Azure VMs, if the specific edition/feature requirements call for it) is a managed PaaS service, not a pod. Sharding (if/when the threshold in `10-database-partitioning-sharding.md` §6.3 is reached) means N independently-provisioned Azure SQL databases, each reached by the `IShardResolver`/`ShardedShortUrlRepository` seam that document already designed — AKS pods are the *callers* of these databases, never their host. |
| **Distributed cache — Redis** | `07-redis-caching-and-invalidation.md` §4: "a managed Redis offering (Azure Cache for Redis Premium)" | **Outside AKS** — Azure Cache for Redis is the recommended managed PaaS option in that document, chosen explicitly over a self-managed, AKS-hosted Redis Cluster to avoid taking on shard-rebalancing/failover operations for infrastructure that "is important but not authoritative" (that document §4). This document does not revisit that choice. |
| **Analytics store — Elasticsearch** | `03-elasticsearch-vs-sql-server.md` — recommends Elasticsearch, does not pin a hosting model | **Managed PaaS is the default recommendation here: Elastic Cloud on Azure** (Elastic's own managed offering, available via Azure Marketplace) — for the same reason Redis is managed rather than self-hosted (`07-redis-caching-and-invalidation.md` §4's reasoning applies identically): Elasticsearch cluster operations (shard/replica sizing, JVM heap tuning, node health, ILM policy execution) are meaningful standing operational work, and `03-elasticsearch-vs-sql-server.md` §5 already names "operational complexity of running a cluster" as Elasticsearch's most honest weakness. **Self-managed Elasticsearch on AKS (as a StatefulSet, e.g., via the Elastic Cloud on Kubernetes (ECK) operator) is named as a legitimate alternative**, not dismissed — it keeps all state inside the same cluster/network boundary and can be cheaper at very large, sustained scale once the team has the operational maturity to run it, mirroring the same self-managed-vs-managed trade-off `05-kafka-comaporison.md` §5 already worked through for the broker. This document's default is the managed option; the self-managed-on-AKS path is the named escalation if cost or data-residency constraints ever push that way. |
| **Message broker** | `05-kafka-comaporison.md` §4: Azure Service Bus Topics/Subscriptions (default); Kafka retained as a named upgrade path | **Outside AKS by default** — Azure Service Bus is a managed PaaS messaging service, not a pod. If the Kafka upgrade path is ever taken, the same managed-vs-self-managed choice applies as above: Azure Event Hubs' Kafka-compatible endpoint (managed) is the lower-operational-cost path; self-managed Kafka on AKS (via the Strimzi operator, the common Kafka-on-Kubernetes pattern) is the named alternative if the replay/multi-consumer needs from `05-kafka-comaporison.md` §3 are real and the team is prepared to own the operational surface `05-kafka-comaporison.md` §5 describes. |

**Why the data tier defaults to managed PaaS, stated once instead of per-component:** every component above independently reached the same conclusion in its own document — Redis, Elasticsearch, and the broker are each, in their respective documents, explicitly weighed against a self-managed/AKS-hosted alternative and each time the managed offering wins on operational-cost grounds for a system at this project's team size, not because self-hosting on AKS is impossible. This document names the pattern once: **default to Azure managed PaaS for every stateful data-tier component; treat AKS-hosted self-management as a named, available escalation path per component, not the default.** AKS itself hosts only the stateless-by-design compute (API pods, consumer pods) — the tier that benefits most from elastic scaling (§3) and the tier Kubernetes is actually built to orchestrate well.

---

## 3. The Kubernetes "Elastic" Auto-Scaling Model, Concretely

This is the core of the review prompt's ask — "use Kubernetes elastic model from Azure so server capacity will increase automatically." AKS's elasticity is **two independent, composing mechanisms**, not one:

### 3.1 Horizontal Pod Autoscaler (HPA) — scales the NUMBER OF PODS

The HPA watches a metric and adjusts the **replica count** of a `Deployment` — it never touches the number of underlying VMs (nodes); it only asks "how many copies of this pod should be running right now."

- **What triggers API pod scale-out — tied to the redirect path being the dominant traffic driver.** `06-output-caching-bff-cdn.md`'s scale assumption (10M→100M redirects/day, ~1,150 req/sec sustained average, materially higher at peak for viral spikes) is explicit that redirects dominate creates by roughly 20:1. The redirect BFF's `Deployment` is therefore the pod set under the most scaling pressure, and the metric that should drive its HPA is **requests-per-second (RPS) per pod**, not raw CPU. CPU is a reasonable default for the create/management API (whose load is closer to compute-bound business logic), but for the BFF — a thin, mostly I/O-bound (Redis lookup, output-cache hit) handler — CPU can stay flat even as queueing latency rises under load, because the bottleneck is concurrent connections/requests-in-flight, not CPU cycles. The recommended metric is a **custom metric via the Kubernetes Metrics API (fed by Prometheus, per the observability tier referenced in `13-observability-at-scale.md`, not restated here)** — `http_requests_per_second` per pod, or equivalently `nginx_ingress_controller_requests` if scraped from the ingress controller — with a target (e.g., "scale out when average RPS/pod exceeds X") tuned from real traffic data, not guessed upfront, mirroring the same "tune from observed data" posture `06-output-caching-bff-cdn.md` and `07-redis-caching-and-invalidation.md` already take toward their own TTL/sizing numbers.
- **What triggers consumer pod scale-out — tied to broker/queue depth, via KEDA.** The background consumer pods (`21-background-job-hosting.md`'s analytics-indexing and cache-invalidation Worker Services) are not driven by HTTP traffic at all — CPU/memory-based HPA is a poor fit for them, because a consumer can be CPU-idle while a backlog builds on the broker (e.g., during a burst that outpaces current consumer throughput) with no CPU signal reflecting that fact. The standard, purpose-built answer for scaling Kubernetes workloads off queue/broker metrics is **KEDA (Kubernetes Event-Driven Autoscaling)**, a CNCF project that Azure ships first-class support for as an AKS add-on. KEDA's Azure Service Bus scaler (or, if the Kafka upgrade path is taken, its Kafka scaler measuring consumer-group lag) watches **queue/topic-subscription message count** (Service Bus) or **consumer-group lag** (Kafka) and drives the consumer `Deployment`'s replica count directly off that number — scale to zero when the topic is empty, scale out aggressively when a backlog appears, independent of CPU. This is named explicitly by name because it is the mechanism, not a generic "use a custom metric" hand-wave: KEDA is what makes "broker depth" a first-class HPA-compatible metric with no bespoke metrics-adapter code to write.
- **Summary of the two triggers:** API pods scale on **RPS-per-pod** (redirect traffic is the volume driver); consumer pods scale on **broker/queue depth via KEDA** (throughput is driven by upstream event arrival rate, not request latency). Using the same metric (CPU) for both would under-scale the BFF under I/O-bound load and over-provision consumers that are CPU-idle but backlogged — the two workloads need different signals because they have different bottlenecks, consistent with `21-background-job-hosting.md` §2's observation that these are structurally different workload shapes.

### 3.2 Cluster Autoscaler — scales the NUMBER OF UNDERLYING AKS NODES

The Cluster Autoscaler is a separate mechanism from the HPA, and the two must be understood as composing, not substituting for each other:

- **What triggers it:** the Cluster Autoscaler watches for pods that are **unschedulable** — a pod the HPA just created (§3.1) that the Kubernetes scheduler cannot place on any existing node because every node in the relevant node pool (§4) is already at its CPU/memory-reservation capacity. When that happens, the Cluster Autoscaler provisions additional AKS nodes (VM instances) in that node pool, up to the node pool's configured maximum, and the newly-schedulable pods land on the new capacity once it's ready.
- **The composed sequence, concretely:** a viral link spike drives redirect RPS up → the BFF's HPA (§3.1) increases the `Deployment`'s replica count → if the existing nodes have spare capacity, the new pods schedule immediately and the whole scale-out is done in the time it takes a pod to start (seconds) → if the existing nodes are already full, the new pods sit `Pending` → the Cluster Autoscaler notices `Pending` pods with no schedulable node, requests new VM capacity from Azure (typically 1–3 minutes for a new node to join the cluster and become `Ready`, materially slower than pod scale-out) → once the new node is `Ready`, the `Pending` pods schedule onto it. This is precisely the mechanism the review prompt is asking for when it says "server capacity increases automatically" — it is the *node* count reacting to the *pod* count's inability to fit, not a separate, independently-tuned trigger.
- **Scale-in is symmetric and equally automatic:** the Cluster Autoscaler also removes nodes when they've been underutilized for a configurable grace period (default considerations: a node is a candidate for removal once all its pods could be rescheduled elsewhere and it's been below a utilization threshold for ~10 minutes), consistent with elastic scaling meaning "grows and shrinks," not "only grows."
- **Why this two-mechanism split matters, stated plainly:** the HPA alone cannot increase capacity beyond what's already provisioned — it can ask for more pods, but if there's no room, those pods just queue as `Pending` indefinitely. The Cluster Autoscaler alone does nothing on its own — it only reacts to unschedulable pods; it has no opinion about traffic or load by itself. **Azure's own "Kubernetes elastic model" is the composition of both**: HPA (and KEDA, for the consumer side) decides *how many pods*; Cluster Autoscaler decides *how many nodes those pods need to fit on*. This is the concrete, two-part answer to the review prompt's request for a model where "server capacity increases automatically" — capacity increases at both the pod level and the underlying VM level, each triggered by a different, well-defined signal.

### 3.3 Summary Table

| Mechanism | Scales | Trigger for API pods | Trigger for consumer pods | Reaction time |
|---|---|---|---|---|
| **Horizontal Pod Autoscaler (HPA)** | Number of pod replicas | RPS-per-pod (redirect traffic dominates, per `06-output-caching-bff-cdn.md`) | N/A — consumer pods use KEDA instead (below) | Seconds (pod start time), if nodes have room |
| **KEDA** (event-driven HPA extension) | Number of pod replicas, for event-driven workloads | N/A — API pods use RPS-based HPA instead (above) | Azure Service Bus queue/subscription depth (or Kafka consumer-group lag, if that upgrade path is taken) | Seconds, if nodes have room; scales to zero when idle |
| **Cluster Autoscaler** | Number of underlying AKS nodes (VMs) | Unschedulable (`Pending`) pods in the node pool, regardless of which of the above created them | Same | ~1–3 minutes (new VM provisioning + join) |

---

## 4. Separate Node Pools / Scaling Profiles

**Recommendation: separate AKS node pools for API pods and background consumer pods, not one shared node pool.**

- **API node pool — latency-sensitive, needs headroom.** The redirect BFF's p95/p99 latency targets (`06-output-caching-bff-cdn.md`'s ANFR-05 framing) mean this pool should run with **conservative bin-packing** — pods scheduled with real CPU/memory requests that reflect actual steady-state usage plus margin, so a burst doesn't immediately contend for CPU with a noisy neighbor pod on the same node. This node pool's Cluster Autoscaler should also be tuned to scale out somewhat proactively (a lower unschedulable-pod tolerance / faster scale-out threshold) precisely because the workload it serves is viral-spike-shaped — the cost of scaling out a little early is small; the cost of a slow reaction during a genuine traffic spike is a latency-SLA breach on the system's highest-visibility path.
- **Consumer node pool — throughput-oriented, can tolerate more aggressive bin-packing.** The Worker Service consumers (`21-background-job-hosting.md`) have no end-user-facing latency SLA in the same sense — `21-background-job-hosting.md` §4 already established that cold start and moment-to-moment latency are largely irrelevant for this workload class (the one partial exception, cache-invalidation's "seconds not minutes" target, is still far more tolerant than a redirect's p95). This node pool can run tighter bin-packing (more pods per node, smaller CPU/memory request margins) to optimize for cost-per-throughput rather than headroom, and its Cluster Autoscaler can tolerate a slower, more conservative scale-out reaction and a more aggressive scale-in (shrinking back down once a burst-driven backlog clears) without any user-facing consequence.
- **Why separate pools, not just separate `Deployment`s on a shared pool:** Kubernetes node pools (in AKS, an explicit, separately-configurable group of VMs) let each workload class run a **different VM SKU, different autoscaler min/max bounds, and different bin-packing/priority behavior** — a shared pool would force one scaling profile onto both, which is the wrong trade-off in both directions (over-cautious headroom wastes money on the throughput-oriented consumers; aggressive bin-packing on the API pool risks the latency-sensitive path). `node affinity`/`taints and tolerations` (standard AKS mechanisms) are what keep each `Deployment`'s pods pinned to its intended pool.
- **Forward-looking note — a third, specialized pool if the AI-pluggability direction is ever acted on.** If a future document in this series (an AI/vector-search-pluggability consideration, referenced only in passing here — not designed in this document) results in a workload that benefits from GPU acceleration (e.g., embedding generation for the "URLs and metadata as vectors" direction mentioned in the review's broader scope), that workload should get its **own, third AKS node pool** with a GPU-enabled VM SKU (e.g., the `NC`-series) — GPU nodes are materially more expensive per hour and should never be the pool the general API or consumer workloads land on by default. This is named here only as a forward-looking placeholder for topology consistency; it is not a commitment that such a workload exists today.

---

## 5. Multi-Region Consideration

**Question:** should this infrastructure span multiple Azure regions, given the CDN's inherent global edge presence?

- **The asymmetry that matters:** Cloudflare (§2.1) already gives this system a global edge presence for the traffic that matters most in volume — cached redirect responses never reach any Azure region at all. The AKS cluster and its data tier (§2.5), by contrast, live in exactly **one** Azure region in the topology described above. This is not an oversight; it is the honest state of the design as assembled from the component documents — none of them (Redis, Elasticsearch, the broker, the sharded SQL store) proposed a multi-region topology, and this document should not silently introduce one without justifying it against the same "does the stated scale actually require this" discipline `05-kafka-comaporison.md` and `10-database-partitioning-sharding.md` both apply to their own decisions.
- **Honest recommendation: a single, well-scaled Azure region is sufficient for v2, with multi-region named as a future escalation path, not adopted now.** Reasoning:
  - The CDN already absorbs the large majority of traffic (`06-output-caching-bff-cdn.md` §4.2's own framing: cache-hit ratios of "80–95%+ for skewed, cacheable public traffic") before it ever reaches the origin region — the single-region origin is not serving 100M/day directly, it is serving the residual cache-miss traffic, which is a materially smaller number.
  - `10-database-partitioning-sharding.md` §6.3 explicitly names "a genuine multi-region active-write requirement" as one of only three concrete triggers for sharding the core database — and frames it as a threshold not yet reached at this review's stated 5-year scale, not a present requirement. The same restraint applies one level up: multi-region *compute* is a heavier, costlier commitment than single-region-with-good-elasticity, and nothing in this series' traffic numbers (100M/day at 5-year horizon, ~1,150 req/sec average) crosses a threshold that a well-autoscaled single-region AKS cluster (§3) cannot serve, once the CDN has already done most of the work.
  - Multi-region active-active introduces real, non-trivial new costs this document should not understate: cross-region data replication/consistency for the SQL tier (directly interacting with — and complicating — the sharding design in `10-database-partitioning-sharding.md`), a second full copy of the Redis/Elasticsearch/broker stack (or a replicated topology for each), and Front Door's global routing logic needing real region-failover/latency-routing rules rather than a single backend pool. None of this is free, and none of it is justified by the traffic numbers this review is scoped to.
  - **What a single region does need, and already gets from this design:** availability-zone redundancy *within* the region — AKS supports spreading node pools across Availability Zones, and every managed data-tier service named in §2.5 (Azure Cache for Redis Premium, Azure SQL, Azure Service Bus Premium) supports zone-redundant deployment. This is a materially cheaper way to buy the *availability* benefit multi-region is often reached for, without the *data-consistency* cost multi-region actually introduces — and it is the honest, right-sized answer at this scale.
- **When to revisit:** if a future requirement introduces genuine regional data-residency constraints, a latency SLA that a single region cannot meet for a geographically distant user base even after CDN caching, or traffic that materially exceeds this review's 5-year projection, multi-region active-active becomes the next escalation — this document names it as the deliberate next step, not something this v2 design commits to building now.

---

## 6. Architecture Diagram

```
                                   ┌─────────────────────────────┐
                                   │        Public Internet       │
                                   └───────────────┬──────────────┘
                                                    │
                                                    ▼
                              ┌────────────────────────────────────────┐
                              │   Cloudflare CDN (edge, global PoPs)    │
                              │   - Edge cache, 30s TTL (redirect)      │  06-output-caching-bff-cdn.md
                              │   - Volumetric DDoS absorption          │  22-security...authorization.md §2.3
                              └───────────────────┬──────────────────--┘
                                     cache miss /  │  TTL expired
                                                   ▼
                              ┌──────────────────────────────────────--┐
                              │   Azure Front Door Premium + WAF        │  §2.2 (this doc)
                              │   - OWASP CRS managed rules, custom     │
                              │   - Global L7 routing (path-based:      │
                              │     /{shortCode} vs /api/*)             │
                              └───────────────────┬────────────────---─┘
                                                   ▼
                              ┌──────────────────────────────────────--┐
                              │   Azure Load Balancer (Standard, L4)    │  §2.3 (this doc)
                              │   - TCP distribution into AKS nodes     │
                              └───────────────────┬────────────────---─┘
                                                   ▼
        ┌──────────────────────────────────────────────────────────────────────────────┐
        │                     Azure Kubernetes Service (AKS) — single region             │
        │                                                                                │
        │  ┌───────────────── API Node Pool ─────────────────┐  ┌── Consumer Node Pool ─┐│
        │  │  (latency-sensitive, headroom bin-packing)       │  │ (throughput, dense)   ││
        │  │                                                   │  │                       ││
        │  │  ┌──────────────┐   ┌───────────────────────┐    │  │ ┌───────────────────┐ ││
        │  │  │ Redirect BFF │   │ Create/Mgmt API        │    │  │ │ Analytics-Indexer │ ││
        │  │  │ pods (HPA:   │   │ pods (HPA: CPU/RPS)    │    │  │ │ consumer (KEDA:   │ ││
        │  │  │ RPS/pod)     │   │                         │    │  │ │ Service Bus depth)│ ││
        │  │  └──────┬───────┘   └───────────┬────────────┘    │  │ └─────────┬─────────┘ ││
        │  │         │  output cache (10s)   │                 │  │           │           ││
        │  └─────────┼───────────────────────┼─────────────────┘  │ ┌─────────┴─────────┐ ││
        │            │                       │                    │ │ Cache-Invalidation │ ││
        │            │                       │                    │ │ subscriber (KEDA)  │ ││
        │            │                       │                    │ └─────────┬─────────┘ ││
        │            │                       │                    └───────────┼───────────┘│
        │            │                       │                                │            │
        │    Cluster Autoscaler (both pools): adds/removes AKS nodes when     │            │
        │    HPA/KEDA-driven pods are unschedulable on current node capacity  │            │
        └────────────┼───────────────────────┼────────────────────────────────┼────────────┘
                      │                       │                                │
                      ▼                       ▼                                ▼
        ┌───────────────────────┐  ┌───────────────────────┐   ┌──────────────────────────┐
        │ Azure Cache for Redis │  │ Azure SQL (sharded per │   │ Azure Service Bus Topics  │
        │ (Premium, clustered)  │  │ 10-database-           │   │ (default) / Event Hubs    │
        │ 07-redis-caching...   │  │ partitioning-sharding) │   │ Kafka-compat (upgrade     │
        └───────────────────────┘  └───────────────────────┘   │ path) — 05-kafka-...      │
                                                                 └──────────────┬────────────┘
                                                                                 ▼
                                                                 ┌───────────────────────────┐
                                                                 │ Elastic Cloud on Azure     │
                                                                 │ (managed) or self-managed  │
                                                                 │ on AKS via ECK operator     │
                                                                 │ 03-elasticsearch-vs-sql... │
                                                                 └───────────────────────────┘

  Azure Functions (separate deployment artifact, outside AKS): retention-cleanup sweep,
  reconciliation sweep — 21-background-job-hosting.md §7 Option B, triggered on a Timer,
  not part of the request path above.
```

---

## 7. What This Document Does NOT Cover — Explicit Exceptions

Consistent with this series' convention (`nfr-security.md`, `22-security-reputation-hacking-authorization.md` §4) of naming scope boundaries rather than letting them be discovered by absence:

- **Infrastructure-as-Code implementation.** This document describes the topology; it does not provide Bicep/Terraform/ARM templates, Kubernetes manifests, Helm charts, or CI/CD pipeline definitions to stand any of it up. That is a separate, downstream engineering effort.
- **Cost estimation.** No dollar figures, no reserved-instance/spot-VM pricing analysis, no comparison of Azure SKU pricing tiers. `07-redis-caching-and-invalidation.md` and `05-kafka-comaporison.md` both flag their own sizing numbers ("4gb" Redis budget, node counts) as illustrative starting points to be tuned from observed data, not costed commitments — the same caveat applies to every number in this document (HPA thresholds, node pool sizes, Cluster Autoscaler min/max bounds).
- **Specific Azure region or SKU sizing.** This document does not name which Azure region hosts the single-region deployment (§5), nor does it size AKS node VM SKUs, Azure SQL service tiers, Redis cache tiers, or Service Bus messaging-unit counts. Those are capacity-planning exercises that depend on real traffic measurement, explicitly deferred the same way `07-redis-caching-and-invalidation.md` §3.2 defers its own memory-budget sizing ("set from observed hit-rate curves in staging/production... not guessed upfront").
- **DevOps/CI-CD pipeline design.** Explicitly out of scope per the review prompt itself ("DevOps: Create a document and say it is out of scope") — this document does not describe build pipelines, deployment automation, or release strategy (blue/green, canary) for the workloads it places into AKS.
- **Observability implementation detail.** §3.1 references Prometheus-fed custom metrics for the HPA; the full observability stack (Loki/Grafana/OTEL) is `13-observability-at-scale.md`'s scope, not restated or redesigned here.
- **A concrete WAF rule set.** §2.2 names Azure WAF on Front Door as the service; it does not specify which managed rule set version, custom rule definitions, or rate-limit thresholds to configure — the same exception `22-security-reputation-hacking-authorization.md` §4 already states ("WAF configuration specifics... is deployment/operations work, downstream of this design").

---

## 8. Summary of Decisions

| # | Layer | Decision | Cross-reference |
|---|---|---|---|
| 1 | Edge | Cloudflare CDN — edge cache + primary DDoS absorption | `06-output-caching-bff-cdn.md`; `22-...authorization.md` §2.3 |
| 2 | Firewall/WAF | Azure Web Application Firewall on Azure Front Door Premium | §2.2 (this document) |
| 3 | Load Balancer | Front Door = global L7 routing; Azure Load Balancer (Standard) = regional L4 distribution into AKS | §2.3 (this document) |
| 4 | Compute | AKS — API pods (BFF + create/management, separate `Deployment`s) and consumer pods (Worker Service), on separate node pools | §2.4, §4 (this document); `06-...bff-cdn.md`; `21-background-job-hosting.md` |
| 5 | Elastic model | HPA (RPS-per-pod for API) + KEDA (broker/queue depth for consumers) scale pod count; Cluster Autoscaler scales node count when pods are unschedulable | §3 (this document) |
| 6 | Data tier | Managed Azure PaaS by default (Azure SQL, Azure Cache for Redis, Elastic Cloud on Azure, Azure Service Bus); AKS-hosted self-management named as an escalation path per component | §2.5 (this document) |
| 7 | Node pools | Separate pools for API (headroom, latency) vs. consumers (dense bin-packing, throughput); GPU pool named as a forward-looking placeholder only | §4 (this document) |
| 8 | Multi-region | Not adopted for v2 — single region + availability-zone redundancy is sufficient at this review's stated scale; multi-region is a named future escalation | §5 (this document) |

**This document does not claim this is the only valid topology** — it is the assembled, concrete answer to the review prompt's infrastructure ask, built from decisions already made elsewhere in this series, with every Azure service named explicitly rather than left as a generic placeholder, consistent with every other document in this review.
