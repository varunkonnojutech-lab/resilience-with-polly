# Resilience in .NET — retries, circuit breakers, timeouts, fallbacks & hedging with Polly v8

[![Build](https://github.com/varunkonnojutech-lab/resilience-with-polly/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/resilience-with-polly/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/resilience-with-polly)

Every distributed system fails. A downstream API times out, a database blips, a third-party service goes down for ten minutes. The question is never *whether* your code meets failure — it's whether failure stays contained or cascades through every caller waiting on a dead dependency. This repo is about containment: the five resilience strategies in Polly v8, each one runnable, each one proven by a test, and one combined pipeline you would actually ship.

## Who is this for

.NET developers who call other services — HTTP APIs, databases, message brokers — and want the production answer to "what happens when it fails?", not just a try/catch. If you have ever watched one slow dependency take down your whole API, this is the repo.

## What you'll learn

- **Retry** — exponential backoff with jitter, and why jitter matters: without it, a fleet of callers retries in lockstep and turns a recovering service's first healthy seconds into a second outage
- **Circuit breaker** — fail fast instead of waiting on a dead service: failure ratio, minimum throughput, break duration, and the half-open trial call that decides when traffic resumes
- **Timeout** — a hard deadline per attempt, and the ordering rule that decides whether a timeout kills one attempt or your whole retry sequence
- **Fallback** — degraded-but-usable answers (a cached value, a default) instead of a 500, and why every fallback must be logged, not silent
- **Hedging** — start a second attempt when the first is slow and take whichever finishes first: lower tail latency, paid for with extra load, so only for idempotent reads
- **Pipeline ordering** — strategies execute in the order added, first added = outermost; fallback outside, retry next, breaker next, timeout innermost, so each retry attempt gets its own timeout and its own breaker vote
- **Breaker tuning traps** — a breaker that opens mid-retry (minimum throughput too low) and a retry loop that keeps hammering through an open breaker (both demonstrated, both fixed)

## Quick start

You need .NET 8. No Docker, no network, no external services — every demo runs against a programmable fake dependency:

```bash
dotnet test
```

Run all six demos:

```bash
dotnet run --project src/ResilienceDemo.Demo
```

Run one demo on its own:

```bash
dotnet run --project src/ResilienceDemo.Demo -- breaker
```

Demos: `retry`, `breaker`, `timeout`, `fallback`, `hedging`, `combined`.

## What the demos show

**1. Retry** — a service that fails twice, then succeeds. Three attempts, one result, zero errors surfaced. One failure is not an outage — retry absorbed it.

**2. Circuit breaker** — an always-failing service. Call 1 fails, call 2 fails and the breaker opens, call 3 is rejected *instantly* with `BrokenCircuitException` — the dead service is not even tried. After the 2-second break, one trial call (half-open) succeeds and the breaker closes.

**3. Timeout** — a 2-second call with a 200 ms deadline. `TimeoutRejectedException` after ~200 ms: the caller was freed instead of waiting 2 seconds.

**4. Fallback** — the service is down, the page still renders. The pipeline returns `"cached-value"` instead of a 500.

**5. Hedging** — a slow primary (800 ms) against a fast replica. The hedged attempt starts after 100 ms and wins: result in ~130 ms, not 800 ms.

**6. Combined** — the pipeline you would actually ship. A service that fails twice then recovers: retried to success, breaker stayed closed. A service that is fully down: retried 4 times, breaker opened, fallback returned `"degraded-ok"`.

## The patterns, in one picture

```
Call → Fallback (outermost: catches everything below)
         → Retry (exponential backoff + jitter)
              → Circuit breaker (fail fast when the service is down)
                   → Timeout (hard deadline per attempt)
                        → the actual call
```

And the breaker lifecycle:

```
CLOSED ── failures cross ratio + throughput ──▶ OPEN (reject instantly)
  ▲                                              │ break duration passes
  └────── trial call succeeds ── HALF-OPEN ◀─────┘
```

## Project layout

```
src/ResilienceDemo.Core/        # FlakyService (programmable fake dependency)
                                #   + ResiliencePolicies (the five strategies + StandardPipeline)
src/ResilienceDemo.Demo/        # console app running the six demos, all offline
tests/ResilienceDemo.Tests/     # xUnit tests: one per behaviour, all against FlakyService
```

## Honest limits

- `FlakyService` is a fake, not a network. Real-world failure modes — partial responses, slow-but-not-timeout responses, connection-pool exhaustion — need a real downstream service to observe; the strategies here are the same ones you would point at it.
- The breaker in this repo is **in-process**. Across several app servers, each server trips its own breaker at a different time. A shared breaker state (Redis, or a sidecar) is a separate design decision, not a code change.
- Hedging doubles load on the downstream service while it runs. Never hedge writes, payments, or anything that must execute exactly once — this repo hedges reads only, and says so in the code.
- Timeouts here use Polly's pessimistic timeout (a separate task + cancellation). It frees your caller, but the abandoned downstream work may still complete server-side — another reason timeouts pair with idempotent operations.

## Next steps

- Point the standard pipeline at a real `HttpClient` call and watch the breaker open against a failing local API.
- Add a rate limiter in front of the pipeline (see the queue's `rate-limiting-dotnet` topic) so your own retries never become someone else's outage.
- Try the failure-injection variant: make `FlakyService` fail only on every third call and watch the breaker stay closed — breakers react to ratios, not single failures.
- Pair this with the [redis-caching-dotnet](https://github.com/varunkonnojutech-lab/redis-caching-dotnet) repo: fallback values usually come from a cache, and cache stampedes are what you get when every caller retries at once.
