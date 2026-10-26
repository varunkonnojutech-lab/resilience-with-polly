using System.Diagnostics;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ResilienceDemo.Core;

namespace ResilienceDemo.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task Retry_SucceedsAfterTransientFailures()
    {
        var service = FlakyService.FailTimesThenSucceed(2);
        var result = await ResiliencePolicies.RetryPipeline()
            .ExecuteAsync(ct => service.CallAsync(ct));

        Assert.Equal("ok", result);
        Assert.Equal(3, service.Attempts);
    }

    [Fact]
    public async Task Retry_GivesUpAfterMaxAttempts()
    {
        var service = FlakyService.FailTimesThenSucceed(10);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ResiliencePolicies.RetryPipeline(maxRetries: 3)
                .ExecuteAsync(ct => service.CallAsync(ct)).AsTask());

        // 1 initial attempt + 3 retries, then the exception surfaces.
        Assert.Equal(4, service.Attempts);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterFailures_ThenRecovers()
    {
        var service = FlakyService.AlwaysFail();
        var pipeline = ResiliencePolicies.CircuitBreakerPipeline(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.ExecuteAsync(ct => service.CallAsync(ct)).AsTask());
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.ExecuteAsync(ct => service.CallAsync(ct)).AsTask());

        // Breaker is open now: rejected instantly, the service is not even tried.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync(ct => service.CallAsync(ct)).AsTask());
        Assert.Equal(2, service.Attempts);

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var recovering = FlakyService.FailTimesThenSucceed(0);
        var result = await pipeline.ExecuteAsync(ct => recovering.CallAsync(ct));
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Timeout_RejectsSlowCall()
    {
        var service = FlakyService.Slow(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            ResiliencePolicies.TimeoutPipeline(TimeSpan.FromMilliseconds(200))
                .ExecuteAsync(ct => service.CallAsync(ct)).AsTask());
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Timeout should fire near 200 ms, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Fallback_ReturnsSafeValueWhenServiceFails()
    {
        var service = FlakyService.AlwaysFail();
        var result = await ResiliencePolicies.FallbackPipeline("cached-value")
            .ExecuteAsync(ct => service.CallAsync(ct));

        Assert.Equal("cached-value", result);
    }

    [Fact]
    public async Task Hedging_ReturnsFastestAttempt()
    {
        var slow = FlakyService.Slow(TimeSpan.FromMilliseconds(800), "slow-primary");
        var pipeline = ResiliencePolicies.HedgingPipeline(
            TimeSpan.FromMilliseconds(100),
            _ => ValueTask.FromResult("fast-replica"));

        var stopwatch = Stopwatch.StartNew();
        var result = await pipeline.ExecuteAsync(ct => slow.CallAsync(ct));
        stopwatch.Stop();

        Assert.Equal("fast-replica", result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(600),
            $"Hedged result should arrive well before 800 ms, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task StandardPipeline_RetriesTransientFailuresToSuccess()
    {
        var service = FlakyService.FailTimesThenSucceed(2);
        var result = await ResiliencePolicies.StandardPipeline()
            .ExecuteAsync(ct => service.CallAsync(ct));

        Assert.Equal("ok", result);
        Assert.Equal(3, service.Attempts);
    }

    [Fact]
    public async Task StandardPipeline_FallsBackWhenServiceIsDown()
    {
        var service = FlakyService.AlwaysFail();
        var result = await ResiliencePolicies.StandardPipeline()
            .ExecuteAsync(ct => service.CallAsync(ct));

        Assert.Equal("degraded-ok", result);
        Assert.Equal(4, service.Attempts); // 1 attempt + 3 retries, then fallback
    }
}
