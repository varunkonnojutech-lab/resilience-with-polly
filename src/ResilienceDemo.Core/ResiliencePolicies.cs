using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Hedging;
using Polly.Retry;
using Polly.Timeout;

namespace ResilienceDemo.Core;

/// <summary>
/// One strategy at a time, then one combined pipeline — all built on
/// Polly v8's <see cref="ResiliencePipeline{TResult}"/>.
///
/// Ordering rule that matters: strategies execute in the order they are
/// added, first added = outermost. So the combined pipeline adds fallback
/// first (it must catch everything below it), then retry, then the circuit
/// breaker, then the timeout innermost — so each retry attempt gets its
/// own timeout and its own breaker vote, instead of one timeout killing
/// the whole retry sequence.
/// </summary>
public static class ResiliencePolicies
{
    /// <summary>
    /// Retries transient failures with exponential backoff. Jitter is off
    /// here so demos and tests are deterministic; turn it on in production
    /// (see <see cref="StandardPipeline"/>) so a fleet of callers does not
    /// retry in lockstep and thundering-herd the recovering service.
    /// </summary>
    public static ResiliencePipeline<string> RetryPipeline(int maxRetries = 3, Action<string>? log = null)
    {
        return new ResiliencePipelineBuilder<string>()
            .AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                MaxRetryAttempts = maxRetries,
                Delay = TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                OnRetry = args =>
                {
                    log?.Invoke($"[retry] attempt {args.AttemptNumber} failed " +
                        $"({args.Outcome.Exception?.GetType().Name}); retrying in {args.RetryDelay}");
                    return default;
                },
            })
            .Build();
    }

    /// <summary>
    /// Stops calling a dependency that is failing fast. After
    /// <paramref name="minimumThroughput"/> calls with at least half of
    /// them failing, the breaker opens for <paramref name="breakDuration"/>
    /// and calls fail immediately with <see cref="BrokenCircuitException"/>
    /// instead of waiting on a dead service. One trial call after the break
    /// (half-open) decides whether to close again.
    /// </summary>
    public static ResiliencePipeline<string> CircuitBreakerPipeline(
        TimeSpan breakDuration, int minimumThroughput = 2, Action<string>? log = null)
    {
        return new ResiliencePipelineBuilder<string>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = minimumThroughput,
                BreakDuration = breakDuration,
                OnOpened = args =>
                {
                    log?.Invoke($"[breaker] OPEN for {args.BreakDuration} after {args.Outcome.Exception?.GetType().Name}");
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    log?.Invoke("[breaker] HALF-OPEN — trial call allowed");
                    return default;
                },
                OnClosed = _ =>
                {
                    log?.Invoke("[breaker] CLOSED — service recovered");
                    return default;
                },
            })
            .Build();
    }

    /// <summary>
    /// Gives a call a hard deadline. A slow dependency throws
    /// <see cref="TimeoutRejectedException"/> instead of holding a thread
    /// (and your caller) hostage. Keep timeouts inside retries, not around
    /// them — see <see cref="StandardPipeline"/>.
    /// </summary>
    public static ResiliencePipeline<string> TimeoutPipeline(TimeSpan timeout, Action<string>? log = null)
    {
        return new ResiliencePipelineBuilder<string>()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = timeout,
                OnTimeout = args =>
                {
                    log?.Invoke($"[timeout] call exceeded {args.Timeout}");
                    return default;
                },
            })
            .Build();
    }

    /// <summary>
    /// Returns a safe answer instead of an exception: a cached value, a
    /// default, an empty list. Fallback is for degraded-but-usable, never
    /// for hiding a failure you need to know about — log every fallback.
    /// </summary>
    public static ResiliencePipeline<string> FallbackPipeline(string fallbackValue, Action<string>? log = null)
    {
        return new ResiliencePipelineBuilder<string>()
            .AddFallback(new FallbackStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                FallbackAction = _ =>
                {
                    log?.Invoke($"[fallback] returning \"{fallbackValue}\"");
                    return Outcome.FromResultAsValueTask(fallbackValue);
                },
            })
            .Build();
    }

    /// <summary>
    /// Hedging: if the first attempt has not finished after
    /// <paramref name="delay"/>, start a second attempt (here, a fast
    /// replica of the same call) and take whichever finishes first. Hedging
    /// trades extra load for lower tail latency — only hedge idempotent,
    /// read-only calls, never payments or writes that must run once.
    /// </summary>
    public static ResiliencePipeline<string> HedgingPipeline(
        TimeSpan delay, Func<CancellationToken, ValueTask<string>> hedgedAction)
    {
        return new ResiliencePipelineBuilder<string>()
            .AddHedging(new HedgingStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                MaxHedgedAttempts = 1,
                Delay = delay,
                ActionGenerator = args => () => RunHedgedAsync(hedgedAction, args.ActionContext.CancellationToken),
            })
            .Build();
    }

    private static async ValueTask<Outcome<string>> RunHedgedAsync(
        Func<CancellationToken, ValueTask<string>> hedgedAction, CancellationToken cancellationToken)
    {
        try
        {
            return Outcome.FromResult(await hedgedAction(cancellationToken));
        }
        catch (Exception ex)
        {
            return Outcome.FromException<string>(ex);
        }
    }

    /// <summary>
    /// The pipeline you would actually ship: fallback outside, retry with
    /// jitter, circuit breaker, timeout per attempt inside. The breaker
    /// needs 4 calls before it can open, so a couple of transient failures
    /// followed by a recovery are retried to success instead of tripping
    /// the breaker mid-retry.
    /// </summary>
    public static ResiliencePipeline<string> StandardPipeline(Action<string>? log = null)
    {
        return new ResiliencePipelineBuilder<string>()
            .AddFallback(new FallbackStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                FallbackAction = _ =>
                {
                    log?.Invoke("[fallback] returning \"degraded-ok\"");
                    return Outcome.FromResultAsValueTask("degraded-ok");
                },
            })
            .AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    log?.Invoke($"[retry] attempt {args.AttemptNumber} failed; retrying in {args.RetryDelay}");
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<Exception>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    log?.Invoke($"[breaker] OPEN for {args.BreakDuration}");
                    return default;
                },
                OnClosed = _ =>
                {
                    log?.Invoke("[breaker] CLOSED");
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                OnTimeout = args =>
                {
                    log?.Invoke($"[timeout] attempt exceeded {args.Timeout}");
                    return default;
                },
            })
            .Build();
    }
}
