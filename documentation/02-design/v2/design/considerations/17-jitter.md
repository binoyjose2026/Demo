# Consideration 17 — Jitter

**Version:** v2 (extreme-scalability review)
**Status:** Draft
**Scope:** This document covers exactly one thing — randomized jitter applied on top of a retry/backoff schedule. It does not define what gets retried or how the base delay is computed (see `15-retry.md` and `16-exponential-backoff.md`), does not cover per-attempt time budgets (`14-timeout.md`), does not cover trip/open/half-open state machines (`18-circuit-breaker.md`), and does not cover concurrency isolation between dependencies (`19-bulkhead.md`). Jitter is a small, cheap modification to the *delay* a retry already computes — it has no meaning without a retry policy underneath it. Applies to any of this system's outbound calls that retry with backoff: API instances calling the primary datastore, Redis (`07-redis-caching-and-invalidation.md`), Elasticsearch (`03-elasticsearch-vs-sql-server.md`), the broker (`05-kafka-comaporison.md`), and background consumers processing analytics events.

---

## 1. The Thundering Herd Problem, Precisely

`16-exponential-backoff.md` establishes a backoff *schedule* — a deterministic function of attempt number, e.g. `delay(n) = base * 2^(n-1)`, capped at some maximum. That determinism is the point of exponential backoff (it spaces retries out predictably) and also its failure mode.

At this system's scale — many horizontally-scaled API instances producing at ~1,150 creates/sec combined, and a larger fleet of background consumers processing thousands of analytics events/sec — a shared downstream dependency going down (Elasticsearch cluster, Redis, the broker, the primary database) does not fail one caller. It fails **every instance calling it at once**. Each of those instances independently starts running the exact same deterministic backoff schedule, typically starting its count from the same trigger (the outage's onset), because they all observed the failure at roughly the same time.

If the schedule is `200ms, 400ms, 800ms, 1600ms, 3200ms` for every instance with no randomness, then every instance's second retry lands at *the same 400ms mark*, every third retry at the same 800ms mark, and so on — hundreds of instances converging on the same handful of wall-clock instants. This is **lockstep**: independent processes, no coordination between them, but perfectly synchronized retry timing purely because they share the same deterministic formula and the same starting reference point.

The failure mode this produces: the dependency comes back online after an outage (or a deploy, or an autoscale event finishes provisioning capacity) and, at the instant it recovers, absorbs the *entire* retry backlog from every failed instance simultaneously — one massive correlated spike — rather than a spread-out trickle. That spike is frequently large enough to immediately re-trip the dependency (or the caller's own circuit breaker, `18-circuit-breaker.md`), producing a flap: recover → thundering herd → fail again → next synchronized retry wave hits the same wall. The very mechanism meant to give an overloaded/recovering system breathing room (backoff) instead concentrates load into narrower, sharper bursts than an un-backed-off retry storm would, precisely at the moment the system is least able to absorb a spike — the moment it just came back up.

This is qualitatively different from "too many retries" (that's what `15-retry.md`'s retry budget and `19-bulkhead.md`'s concurrency caps address). Jitter does not reduce the *number* of retries in flight system-wide; it changes *when* each individual instance's retries land, so they stop landing on the same handful of moments.

---

## 2. How Randomized Jitter Breaks the Synchronization

The fix does not touch the exponential growth curve from `16-exponential-backoff.md` at all — it perturbs the delay each instance actually sleeps for, so that instances computing the "same" nominal delay end up sleeping for different actual durations.

Mechanically: instead of `Thread.Sleep(delay(n))` using the deterministic `delay(n)`, each instance computes `delay(n)` as before and then draws a **random value derived from it** to sleep for instead. Because every instance draws its own independent random number (from its own PRNG, seeded independently — no shared seed, no coordination), a herd of 500 instances that all failed at the same instant and are all "on retry attempt 3" no longer wake up at the same millisecond. Their wake-up times are spread across an interval, turning one sharp spike into a smoothed-out arrival curve — the same total number of retries, redistributed over time instead of concentrated at one instant. The dependency sees a ramp it can actually absorb rather than a step function.

This is a small code change (one extra random draw per retry, in the delay calculation only) with an outsized effect on recovery behavior at this system's scale, which is exactly why it is treated as its own named pattern rather than folded silently into `16-exponential-backoff.md`.

---

## 3. Jitter Strategies, Compared

Following the formulation from the AWS Architecture Blog post that popularized this analysis ("Exponential Backoff And Jitter," Marc Brooker, 2015), given `base` (the initial delay), `cap` (the maximum delay), and `n` (the attempt number):

| Strategy | Formula | Behavior |
|---|---|---|
| **No jitter** (baseline, `16-exponential-backoff.md`) | `delay = min(cap, base * 2^(n-1))` | Deterministic — the thundering-herd problem in §1. |
| **Full Jitter** | `delay = random_between(0, min(cap, base * 2^(n-1)))` | Uniformly random anywhere from zero up to the exponential value. Maximizes spread, but can produce very short delays back-to-back (including near-zero), and successive attempts from the *same* instance are uncorrelated with each other — attempt 4 can land shorter than attempt 3. |
| **Equal Jitter** | `temp = min(cap, base * 2^(n-1)); delay = temp/2 + random_between(0, temp/2)` | Splits the difference — half the exponential value is guaranteed, the other half is randomized. Less aggressive spread than Full Jitter, still fully deterministic in its lower bound. |
| **Decorrelated Jitter** | `delay = min(cap, random_between(base, previous_delay * 3))` | Each delay is randomized *relative to the previous delay actually used* (not relative to the theoretical exponential value for that attempt number), with a floor of `base` so it never collapses toward zero, and a multiplier that still trends upward on average. |

**Recommendation for this system: Decorrelated Jitter.**

This matches the AWS Architecture Blog's own conclusion, and it is the right call here, not just a default endorsement:

- **Full Jitter's weakness is concrete at this system's traffic shape.** With thousands of analytics events/sec flowing through background consumers, a consumer that draws several small delays in a row (Full Jitter has no memory of the previous draw, so `random(0, 800)` can legitimately return 12ms right after `random(0, 400)` returned 390ms) ends up retrying almost immediately, repeatedly, against a dependency that may still be recovering — defeating the purpose of backing off at all on that particular instance, even though the *fleet-wide* spread still looks fine on average.
- **Decorrelated Jitter's `base` floor and dependence on the previous delay give it two properties Full Jitter lacks:** no attempt is ever near-instant after the first (the floor prevents that), and the delay still trends upward attempt-over-attempt on expectation (the `* 3` multiplier keeps the ceiling growing) even though each individual draw is randomized — so it keeps the useful *shape* of exponential backoff (later attempts are typically slower) while still being decorrelated enough across instances to avoid lockstep.
- **Given this system's scale** (many API instances on the create path, a larger consumer fleet on the analytics path, several shared downstream dependencies — Redis, Elasticsearch, the broker, the primary datastore — any of which can have a correlated fleet-wide failure), the failure mode Decorrelated Jitter specifically avoids (near-zero repeated retries from a subset of instances) is the one worth avoiding: it is the difference between "recovery ramp is smooth" and "recovery ramp has a jagged early spike from the unlucky subset of instances that drew small delays twice in a row."

Agreement is not automatic, though — the trade-off named honestly: Decorrelated Jitter is very slightly more code (it is stateful — each retry's delay depends on the prior delay, not purely on the attempt number, so the retry loop has to carry that state forward) than Full Jitter, and for a system with far lower concurrency (a handful of instances, not a fleet) the difference between the two strategies would not matter enough to justify the choice one way or the other. At this system's five-year-projection scale, it does.

---

## 4. Worked Example

Parameters consistent with `16-exponential-backoff.md`: `base = 200ms`, `cap = 10,000ms` (10s), 5 retry attempts.

**No jitter (deterministic, the problem):**

| Attempt | delay(n) = min(cap, base·2^(n-1)) |
|---|---|
| 1 | 200ms |
| 2 | 400ms |
| 3 | 800ms |
| 4 | 1,600ms |
| 5 | 3,200ms |

Every one of the (say) 500 API instances that hit a failure at the same moment sleeps for exactly these five values, in lockstep. All 500 instances' attempt-3 retries land at the same 1,400ms-elapsed mark (`200+400+800`).

**Full Jitter** — `delay = random_between(0, min(cap, base·2^(n-1)))`, one example draw sequence:

| Attempt | Range | Example draw |
|---|---|---|
| 1 | `[0, 200]` | 137ms |
| 2 | `[0, 400]` | 58ms |
| 3 | `[0, 800]` | 612ms |
| 4 | `[0, 1600]` | 1,450ms |
| 5 | `[0, 3200]` | 980ms |

Notice attempt 2 (58ms) is *shorter* than attempt 1 (137ms) — a legitimate outcome, and the exact "back-to-back near-instant retry" weakness named in §3.

**Decorrelated Jitter** — `delay = min(cap, random_between(base, previous_delay * 3))`, starting from `previous_delay = base = 200ms`:

| Attempt | Range (`base` to `previous_delay × 3`) | Example draw | New `previous_delay` |
|---|---|---|---|
| 1 | `[200, 600]` | 350ms | 350ms |
| 2 | `[200, 1,050]` | 800ms | 800ms |
| 3 | `[200, 2,400]` | 1,900ms | 1,900ms |
| 4 | `[200, 5,700]` | 4,200ms | 4,200ms |
| 5 | `[200, 10,000]` (capped from 12,600) | 7,600ms | 7,600ms (would cap next round too) |

Every draw is randomized (no two instances land on the same delay), no draw is ever below `base` (200ms), and the sequence still trends upward on expectation across attempts — the exponential *shape* survives, the *synchronization* does not. Across 500 instances that all failed at the same instant, attempt-3 delays are now spread across `[200ms, 2,400ms]` instead of landing on a single 800ms mark — the recovering dependency sees a two-second ramp of retry traffic instead of a single-instant spike.

---

## 5. Implementation in .NET (Polly)

This system already uses Polly for `15-retry.md`'s retry policies (Polly v8's `ResiliencePipeline`, current on .NET 9). Jitter is a one-line addition to the same retry strategy — it is not a separate policy layered on top:

```csharp
using Polly;
using Polly.Retry;

var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>(),   // per-attempt timeout — see 14-timeout.md
        BackoffType = DelayBackoffType.Exponential, // the curve from 16-exponential-backoff.md
        UseJitter = true,                           // <-- decorrelated-style jitter on the exponential curve
        Delay = TimeSpan.FromMilliseconds(200),      // base, matches §4's worked example
        MaxDelay = TimeSpan.FromSeconds(10),         // cap, matches §4's worked example
        MaxRetryAttempts = 5,
        OnRetry = args =>
        {
            _logger.LogWarning(
                "Retry {Attempt} in {Delay}ms calling {Dependency}",
                args.AttemptNumber, args.RetryDelay.TotalMilliseconds, "Elasticsearch");
            return default;
        }
    })
    .Build();

await pipeline.ExecuteAsync(
    async ct => await elasticsearchClient.IndexAsync(document, ct),
    cancellationToken);
```

`RetryStrategyOptions.UseJitter = true` combined with `BackoffType.Exponential` is Polly v8's built-in jitter on the exponential curve — the option to reach for by default in this project, since it needs no extra package and matches the pipeline-based API already used for `14-timeout.md`/`15-retry.md`/`18-circuit-breaker.md`.

For the classic, explicit AWS decorrelated-jitter formula from §3/§4 verbatim (useful if a reviewer wants the exact algorithm auditable rather than Polly's internal implementation), the `Polly.Contrib.WaitAndRetry` package exposes it directly as a delay generator, usable with either the legacy `Policy`-based API or fed manually into a v8 pipeline:

```csharp
using Polly.Contrib.WaitAndRetry;

IEnumerable<TimeSpan> delays = Backoff.DecorrelatedJitterBackoffV2(
    medianFirstRetryDelay: TimeSpan.FromMilliseconds(200),
    retryCount: 5);
```

---

## 6. Explicit Trade-Offs (Named, Not Hidden)

1. **Jitter adds unpredictability to per-request latency for the retrying caller.** A client whose request needed one retry might wait 220ms one time and 590ms the next for the identical failure — jitter optimizes fleet-wide recovery behavior, not any single caller's worst-case latency variance. If a caller has a hard SLA on retry-inclusive latency, that budget needs to account for the top of the jitter range, not the average.
2. **It does not reduce total retry volume.** Jitter is purely about *timing spread*; the concurrency limits that cap how much retry traffic is in flight at once are `19-bulkhead.md`'s job, and the decision of *whether* to keep retrying at all is `15-retry.md`'s retry-budget/give-up logic. Jitter without those is still a herd — just a slower-arriving one.
3. **Decorrelated Jitter's statefulness is a minor implementation cost.** The delay generator must carry `previous_delay` forward across attempts within one retry sequence (Polly's `DecorrelatedJitterBackoffV2` and the v8 `UseJitter` option both handle this internally — it is not something this project needs to hand-roll, but it is worth knowing it is there, since a naive from-scratch reimplementation could easily forget to carry state and silently degrade into an uncorrelated formula).
4. **Jitter is not a substitute for `18-circuit-breaker.md`.** Even a perfectly smoothed retry ramp still sends traffic at a dependency that may still be unhealthy immediately after "recovery" is first observed; the circuit breaker's open state is what stops sending traffic at all during the worst of an outage, before jitter's smoothing even becomes relevant.
