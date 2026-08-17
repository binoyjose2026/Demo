# v2 Design Consideration — Output Caching, BFF, and CDN for the Public Redirect API

**Status:** v2 scalability exploration (not yet adopted into the shipped v1 design).
**Scope:** the public, anonymous redirect endpoint only — `GET /{shortCode}` (AF-02). Does **not** cover the authenticated create/management API, which has a materially different traffic and security profile (see Section 2).
**Companion documents:** `v1/design/fn-fetch.md` (redirect flow, the 301-vs-302 decision this document must reconcile with), `v1/design/nfr-performance.md` (v1 latency targets and in-process caching), `v2/design/considerations/05-kafka-comaporison.md` (create-path scaling — separate concern).
**Scale assumption driving this document:** 10M redirects/day today → 100M/day in 5 years, roughly 1,150 req/sec sustained average, materially higher at peak (viral link spikes are the realistic peak-shape for a URL shortener, not a flat diurnal curve). Redirects overwhelmingly dominate over creates (ANFR-05).

---

## 1. Why This Document Exists

v1's fetch design (`fn-fetch.md`) already established two load-bearing decisions this document must not silently override:

1. **Q7/ANFR-02 — the `(ShortCode → OriginalUrl)` mapping is immutable once created.** This is what makes *any* caching of that mapping safe with no invalidation-on-update problem (`fn-fetch.md` Section 6).
2. **Section 10 — 302 Found, not 301, specifically so every request reaches the origin server.** v1 deliberately rejected client-side/browser-level caching of the redirect response because three requirements depend on the server seeing *every* access: AF-08 (access-event/analytics recording), and AF-06/AF-07 (expiry and deactivation must take effect immediately, not just on the next uncached request).

The three techniques below (output caching, BFF split, CDN edge caching) all sit on top of guarantee #1 — they cache the *lookup*, which is safe because the URL never changes. But two of them (output caching, CDN) introduce a **new form of the same risk that killed 301 in v1**: if a cached response is served without hitting the origin, deactivation/expiry enforcement and analytics both degrade. This document's job is to reconcile that tension explicitly, not repeat the 301 mistake at a different layer with a different technology.

**The reconciliation principle used throughout:** v1 chose "always hit origin" because it had no other way to bound staleness. v2 has one — a **short, bounded TTL** at each caching layer. A TTL turns "never see the origin again" (301's failure mode) into "see the origin again within N seconds" — a quantifiable staleness window traded deliberately against origin load reduction, not an open-ended cache-forever decision. Every TTL recommended below is chosen against that trade-off explicitly, not picked as a round number.

---

## 2. Technique 1 — ASP.NET Core Output Caching

### 2.1 What it is

.NET 7+ ships `Microsoft.AspNetCore.OutputCaching` as first-class middleware: it caches the *entire HTTP response* (status code, headers, body) for a configured policy and serves subsequent matching requests directly from the cache, short-circuiting the controller/service/repository/database path entirely for a cache hit. This is distinct from the in-process `CachingShortUrlRepository` decorator v1 already anticipates in `nfr-performance.md` Section 6 — that caches the *data* (`OriginalUrl`, `ExpiresAtUtc`) and still runs the resolver's expiry check and analytics hook on every request; output caching caches the *finished response* and skips that logic entirely on a hit.

### 2.2 Why it matters at this scale

At 1,150 req/sec average (100M/day), and given that URL-shortener traffic is famously power-law distributed (a small number of short codes — campaign links, social posts — receive a disproportionate share of clicks), a small in-memory response cache can absorb a large fraction of requests for the hottest codes without a single database round-trip. This is the cheapest lever available: no new infrastructure, built into the framework, and it reduces both database load and the sustained CPU/IO cost of the resolver pipeline for the codes that matter most.

### 2.3 The reconciliation — TTL must be short enough that expiry/deactivation stay meaningfully enforced

This is where output caching must not repeat v1's 301 mistake. `fn-fetch.md` Section 10 rejected 301 specifically because a browser-level redirect cache would let AF-07 (deactivation) and AF-06/Section 7.1 (expiry) go unenforced indefinitely for cached clients, and would silently undercount AF-08/AF-09 (access events / click counts). Output caching, if applied naively with a long or unbounded TTL, reintroduces exactly this failure mode — just moved from the browser to the server.

**Recommendation: cache successful (`Resolved`) redirect responses for 10 seconds, varied by route value (`shortCode`).**

Reasoning:

- **Staleness window vs. origin protection.** A 10-second TTL means a deactivated or just-expired link can still serve a stale redirect for at most ~10 seconds after the moment it stops being valid — a small, bounded, and operationally acceptable window (nobody deactivates a link expecting sub-second global effect; AF-07 has no stated real-time SLA). Against that, even a 10-second window is enough to collapse a viral burst — thousands of requests for the same hot code arriving within a 10-second window — down to roughly one origin hit per 10 seconds for that code, which is the overwhelming majority of the traffic reduction available from caching at all. Going from 10s → 60s buys comparatively little additional origin-load reduction (the request curve per code flattens quickly) while multiplying the staleness window 6x for no proportional benefit — so 10 seconds is chosen as the point where the marginal origin-load win drops off relative to the marginal staleness cost, not an arbitrary round number.
- **Do not cache non-`Resolved` outcomes (404/410) at all**, or cache them for a much shorter TTL (e.g., 2 seconds) — caching a `NotFound`/`Expired` response risks a link that becomes valid (edge case: none exist in v1 today, since codes are never reactivated per Q11/Q12) or, more realistically, keeps returning "unavailable" for a code that a retry-happy client or crawler is hammering, which is a minor UX nuisance but not a correctness risk in either direction. Keeping this TTL short (or zero) is the conservative default until there's a measured reason to extend it.
- **Analytics (AF-08) still fires on cache misses only.** A 10-second output cache means the access-event count recorded by `fn-analytics.md`'s hook becomes an *undercount* relative to true click volume during cache hits — this is a real, deliberate trade-off, not an oversight. It should be called out to product/analytics stakeholders explicitly: v2's output-cache TTL trades exact click counts for origin scalability. If exact click counts are a hard requirement (not stated as one in AF-08/AF-09 as currently written), the CDN/edge log stream (Section 4) becomes the source of truth for volume, while the origin's own counter becomes a "uniques past the cache" proxy — a reconciliation approach, not a blocker to adopting output caching.
- **Vary by `shortCode` only**, not by any other header/query string — the redirect target and status depend on nothing else, so a naive default cache key (which could include query strings by policy) must be scoped down to avoid needless cache-key fragmentation (e.g., UTM-tagged variants of the same short link should still share one cache entry unless a future requirement says otherwise).

### 2.4 Implementation shape (illustrative)

```csharp
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("RedirectPolicy", policy =>
        policy.Expire(TimeSpan.FromSeconds(10))
              .SetVaryByRouteValue("shortCode")
              .Tag("redirect"));
});

// RedirectController
[HttpGet("/{shortCode}")]
[OutputCache(PolicyName = "RedirectPolicy")]
public async Task<IActionResult> RedirectAsync(string shortCode, CancellationToken ct) { ... }
```

A deactivation (AF-07) can additionally **evict** the specific cache entry proactively via `IOutputCacheStore.EvictByTagAsync` at the moment of deactivation, rather than only relying on TTL expiry — this tightens the staleness window from "up to 10 seconds" to "near-immediate for the deactivation trigger, TTL-bounded as a backstop for everything else" (e.g., natural expiry, which has no single event to hook). This is a cheap addition worth including in v2's implementation, not just the TTL alone.

---

## 3. Technique 2 — BFF-Style Split for the Public Redirect Surface

### 3.1 The problem with one API serving both concerns

v1's API today serves both the authenticated create/management surface (AF-01, AF-05, AF-07 — requires an owner/auth context) and the anonymous public redirect surface (AF-02 — requires no auth, is followed by literally anyone with the link) from the same ASP.NET Core application. That's a reasonable default for an MVP (`fn-fetch.md` already treats the redirect route as routing-level special case, Section 3), but at 100M/day it becomes an architectural liability because the two surfaces have almost nothing in common operationally:

| Dimension | Create/management API | Public redirect ("BFF") |
|---|---|---|
| Callers | Authenticated link owners, low volume | Anonymous public, extremely high volume |
| Traffic shape | Roughly proportional to create rate (5M/day in 5 years per this review's scope) | 100M/day in 5 years — ~20x create volume |
| Auth/security posture | Requires auth (JWT/API key), authorization checks, rate limiting per user | No auth at all; rate limiting (if any) is per-IP/anti-abuse, not per-user |
| Caching rules | Little to no response caching — data changes on every call by design | Aggressive: output cache (Section 2), CDN edge cache (Section 4) |
| Failure blast radius | An outage degrades link creation — painful but recoverable, low volume | An outage breaks every already-shared link across the internet — the product's entire external surface |
| Scaling trigger | Scales with number of active users/creators | Scales with total link popularity — driven by external events (a link going viral) outside the system's control |
| Deployment cadence / risk tolerance | Can tolerate more frequent deploys, more business logic churn | Wants to be as close to "thin, stable, rarely redeployed" as possible — it's the piece under the most sustained load |

Running both from one deployable means a scaling event on the redirect path (e.g., a viral link spike) either over-provisions the *entire* app (wasteful — the create path doesn't need that capacity) or, worse, a slow/heavy operation on the create side (e.g., a bulk-create burst, or a slow analytics query someone runs against the same process) can starve request-handling threads/connections that the redirect path needs to hit its p95 < 50ms target (`nfr-performance.md` Section 2). These are two different reliability domains sharing one blast radius for no architectural reason.

### 3.2 What the split looks like concretely

**Backend-for-Frontend, here, means:** a dedicated, minimal ASP.NET Core service (or a separately deployed/scaled instance of the same codebase, at minimum) whose *only* job is `GET /{shortCode}` — resolve, check expiry/deactivation (Section 7 of `fn-fetch.md`), redirect, fire the analytics side effect. It is "backend for frontend" in the sense that its frontend is literally the public internet clicking links, not an authenticated SPA/app — but the pattern's value here is identical to its usual motivation: a purpose-built, minimal surface tailored to one consumer's needs, instead of a general-purpose API trying to serve everyone.

Concretely:

- **Separate deployable / separate scale unit.** The redirect BFF gets its own container image (or App Service/Kubernetes deployment), scaled independently — horizontal pod/instance count driven by redirect QPS, not by create-side load. At 1,150 req/sec average with viral bursts, this is an autoscaling target the create API never needs to hit.
- **No auth middleware in the pipeline at all** — not "auth optional," but the authentication/authorization middleware is simply absent from this app's `Program.cs`. This isn't just a performance micro-optimization (skipping token validation on every request, which does matter at this volume); it's a security-surface reduction — there is no auth-related code path to be vulnerable in this service at all, since AF-02 requires none (Q30/API-only decisions don't grant a redirect endpoint any owner-identity requirement).
- **Minimal dependency graph.** The BFF only needs read access to `ShortUrl` (via the same `IShortUrlResolverService`/`IShortUrlRepository` abstractions from `fn-fetch.md`, reused, not reimplemented) and a write path for AF-08's access event (already decoupled/non-blocking per `nfr-performance.md` Section 6, item 6). It does not need the full `Application`-layer surface the create/management API depends on (user context, validation pipelines for create DTOs, etc.).
- **Shares the domain model, not the process.** This is not a rewrite: `ShortUrlResolverService`, `IShortUrlRepository`, and the `ShortUrl` entity from `fn-fetch.md`/`data-design-guidelines.md` are unchanged and reused as a shared library/project reference. The split is at the **hosting/deployment boundary** (two ASP.NET Core host projects, two deployables, two scaling policies), not at the domain-model boundary — consistent with the layering `design-guidelines.md` already establishes; this document does not propose touching `Domain`/`Application` project structure, only adding a second `Api`-layer host.
- **Output caching (Section 2) lives here, not on the create API** — it's meaningless on endpoints whose whole point is fresh, per-user, per-request state.
- **This is what makes CDN fronting (Section 4) safe and clean** — a CDN in front of one narrow, cacheable, anonymous route is a much smaller blast-radius surface to reason about than a CDN sitting in front of a mixed API where most routes must never be cached.

### 3.3 What does *not* move

Create (AF-01), metadata (AF-05, already a separate concern per `fn-fetch.md` Section 9), and deactivation (AF-07) stay on the authenticated management API. Metadata retrieval in particular is explicitly *not* part of the BFF — `fn-fetch.md` Section 9 already establishes it must not share implementation with redirect (it must bypass the soft-delete filter and must not count as a click), and it is a low-volume, authenticated-context read, so it has none of the traffic/caching/security profile that motivates this split.

---

## 4. Technique 3 — Cloudflare CDN in Front of the Redirect BFF

### 4.1 Why a CDN, and why now

Even a well-optimized origin (indexed SQL lookup + in-process cache + output cache, Sections 2–3) still means every request reaches *some* server process in *some* datacenter. At 100M/day (1,150 req/sec average, materially higher at peak for viral spikes), the cheapest possible request is the one that never reaches the origin at all. A CDN like Cloudflare, sitting in front of the BFF from Section 3, can terminate the HTTP request at an edge PoP geographically close to the end user and serve the redirect response directly from edge cache — cutting both origin load and end-user latency (no cross-region round trip for a cache hit), which also helps the ANFR-05 latency target far more than any origin-side optimization can, since the fastest possible response is one the origin never has to compute.

Because the BFF (Section 3) is a narrow, single-purpose, anonymous, cacheable surface, it is specifically the kind of endpoint CDN edge caching is designed for — this is exactly why the split in Section 3 is a *prerequisite* for doing this safely and simply, rather than configuring cache rules against a handful of routes buried inside a mixed API.

### 4.2 The reconciliation — this is a direct, deliberate exception to v1's 302-not-301 decision

This is the trade-off this document must not gloss over. `fn-fetch.md` Section 10's entire rationale for choosing 302 over 301 was: **"every access — first or hundredth — hits `RedirectController`... has the opportunity to be blocked by [expiry/deactivation] checks and counted by [analytics]."** A CDN edge cache that serves a redirect response for a cached short code **is precisely the thing 302 was chosen to prevent** — just implemented at the CDN edge instead of in the requesting browser.

This document does not pretend that tension away. It resolves it the same way Section 2 resolved it for output caching: **a short, explicit, bounded edge-cache TTL**, documented here as a deliberate v2 exception to v1's "every fetch hits the origin" assumption — not a silent reversal of it.

**Recommendation: Cloudflare edge-cache TTL of 30 seconds for successful (2xx/3xx) redirect responses, via a Cache Rule scoped to the redirect route, with `Cache-Control: public, max-age=30` set by the origin (defense in depth — don't rely on Cloudflare dashboard config alone; make the origin's own response headers say the same thing, since output caching in Section 2 is already producing a response object this header can ride on).**

Reasoning:

- **Staleness window.** 30 seconds is the maximum time a deactivated or just-expired link can continue to be served from a given edge PoP after the moment it should stop resolving. This is longer than the 10-second origin-level output cache TTL (Section 2.3) by design — CDN edge caching's whole value proposition is absorbing traffic *before* it reaches the origin at all, so its TTL is necessarily the outer, coarser bound; the origin's own 10-second output cache is the tighter, second layer a request hits on the (much rarer) occasions it does reach origin. 30 seconds is short enough that "I deactivated a link and it kept working across the internet for half a minute" is a defensible, disclosed operational characteristic, not a silent correctness gap — and it is far, far short of 301's failure mode (effectively unbounded, client-controlled, potentially permanent).
- **Origin load reduction.** At 100M/day with realistic popularity skew, a 30-second edge TTL means each Cloudflare PoP serving a hot code from cache needs to hit the origin at most roughly once per 30 seconds *per PoP* for that code, regardless of how many thousands of clicks arrive at that PoP in that window. For a viral link spike — the actual peak-load scenario this system needs to survive, not the average — this is the difference between the origin seeing a spike proportional to click volume and the origin seeing a spike proportional to (number of edge PoPs × 1/30s), which is a bounded, small number irrespective of how viral the link gets. This is the single biggest lever in this document for protecting the origin against the peak traffic the ANFR-05/ANFR-06 latency targets otherwise have to be provisioned to survive directly.
- **Analytics degrades further than output caching alone (compounding, not new).** Just as Section 2.3 disclosed for output caching, edge-cached hits never reach the origin's AF-08 access-event hook at all — this is now the dominant source of undercounting at scale, more so than the origin's own output cache, since the CDN is designed to absorb the *majority* of traffic. **This must be explicitly disclosed as a v2 trade-off, not discovered later:** if AF-08/AF-09's click counts need to stay accurate at CDN scale, the correct fix is not to shorten the CDN TTL further (which defeats the purpose of adding a CDN at all) but to consume **Cloudflare's edge log stream (Logpush)** as the authoritative record of total request volume per short code, reconciling it against the smaller "origin-visible" count the application itself records. This is called out here as the necessary companion decision, not solved in this document — it belongs with `fn-analytics.md`'s v2 counterpart.
- **Do not cache 404/410 responses at the edge**, or cache them for a much shorter TTL (e.g., 5 seconds) — same reasoning as Section 2.3, but the blast radius of getting this wrong is larger at the edge (a wrong "unavailable" cached globally is worse than one cached in a single process's memory).
- **This TTL is a starting recommendation, not a permanent constant** — it should be tuned based on real click-through data once v2 is live (Cloudflare Cache Analytics reports hit ratio and can validate whether 30s is capturing most of the achievable cache-hit benefit, per the same diminishing-returns logic as Section 2.3's 10s vs 60s comparison).

### 4.3 Explicit trade-off statement (for the record)

> **v1 assumption (fn-fetch.md §10):** every redirect request reaches the origin server, guaranteeing real-time enforcement of expiry/deactivation and exact analytics counts.
> **v2 exception (this document, §4.2):** at 100M/day scale, a Cloudflare edge cache with a 30-second TTL is introduced in front of the redirect BFF. This means a deactivated/expired link may continue to resolve successfully from edge cache for up to ~30 seconds after it should stop, and access-event analytics recorded by the origin undercounts true click volume by roughly the CDN's cache-hit ratio (commonly 80–95%+ for skewed, cacheable public traffic). This is accepted as a deliberate, bounded, disclosed trade-off in exchange for absorbing the large majority of origin load and materially improving end-user latency — not an oversight or a silent reversal of the v1 decision.

### 4.4 Why not 1301/1-hour/1-day TTLs

Longer edge TTLs (minutes to hours) would reduce origin load further, but the marginal gain shrinks quickly past the point where an edge PoP is already collapsing a viral burst down to ~1 origin request per TTL window (Section 4.2) — a 5-minute TTL doesn't meaningfully outperform 30 seconds on load reduction for a code that's actually hot, but it does multiply the deactivation-staleness window 10x and makes the analytics undercount problem worse without a proportional benefit. 30 seconds is chosen as the point on that curve where origin-load reduction is already near its practical ceiling for hot codes, while the staleness/undercount cost stays small and operationally defensible.

---

## 5. Layered Summary — How the Three Techniques Compose

```
Client
  │  GET /{shortCode}
  ▼
Cloudflare CDN edge (Section 4)         ── 30s edge-cache TTL, per shortCode
  │  cache miss / TTL expired
  ▼
Redirect BFF — separately scaled (Section 3)
  │  ASP.NET Core Output Cache (Section 2) ── 10s, per shortCode, evicted on deactivation
  │  cache miss / TTL expired
  ▼
ShortUrlResolverService → IShortUrlRepository (unchanged from fn-fetch.md)
  │  in-process cache (nfr-performance.md §6) → SQLite/SQL lookup on true miss
  ▼
Expiry/deactivation check + AF-08 analytics hook — only reached on a true origin hit
```

Each layer's TTL is intentionally shorter the closer it sits to the origin — CDN (30s) > output cache (10s) — so that the layer with the coarsest visibility into origin-side changes (the CDN, which knows nothing about a deactivation event) has the shortest possible staleness tolerance still worth the operational overhead, while the layer closest to the source of truth carries the tightest bound and the eviction hook (Section 2.4) for near-immediate deactivation propagation.

---

## 6. Requirements Traceability

| Requirement | How this document addresses it |
|---|---|
| AF-02 | All three techniques cache the redirect response this endpoint produces; Section 5 shows the composed request path. |
| AF-06 | Both TTLs (10s output cache, 30s edge cache) bound how long an expired link can keep resolving after `ExpiresAtUtc` — Sections 2.3, 4.2. |
| AF-08 | Explicitly disclosed as degraded (undercounted) by caching layers that never reach the origin's analytics hook — Sections 2.3, 4.2 — with Cloudflare Logpush proposed as the reconciliation mechanism. |
| ANFR-05 | CDN edge hits and output-cache hits both reduce redirect latency far below the origin's own p95 <50ms/p99 <150ms target (`nfr-performance.md` §2) by avoiding the origin entirely. |
| ANFR-06 | The core scale lever of this document: absorbing the large majority of 100M/day traffic before origin, and giving the redirect path an independently-scaled deployment unit (Section 3). |

---

## 7. Summary of Decisions and Exceptions

| # | Decision | Rationale / Trade-off |
|---|---|---|
| 1 | ASP.NET Core Output Cache on the redirect route, 10s TTL, varied by `shortCode` only | Cheap, framework-native origin-load reduction; TTL bounds expiry/deactivation staleness (Section 2.3) |
| 2 | Proactive cache eviction on deactivation, via `IOutputCacheStore.EvictByTagAsync` | Tightens staleness for the one event with a clear trigger point; TTL remains the backstop for natural expiry |
| 3 | Split the public redirect endpoint into its own BFF-style deployable, separate from the authenticated create/management API | Different scaling profile, no-auth security posture, independent blast radius (Section 3.1–3.2) |
| 4 | Metadata (AF-05) and create/deactivation (AF-01/AF-07) stay on the management API, not the BFF | Different traffic/auth profile; `fn-fetch.md` §9 already treats metadata as a separate concern |
| 5 | Cloudflare CDN in front of the BFF, 30s edge-cache TTL for successful responses, shorter/no cache for 404/410 | Absorbs the majority of 100M/day traffic before origin; TTL is the deliberate reconciliation with v1's 302-not-301 decision (Section 4.2–4.3) |
| 6 | **Exception, explicitly logged:** v1's guarantee that "every fetch hits the origin" (`fn-fetch.md` §10) no longer holds unconditionally in v2 | Traded for origin-load reduction at 100M/day scale; bounded by TTLs, not open-ended; analytics reconciliation via CDN logs is the required companion decision (Section 4.2) |
