using Polly.CircuitBreaker;
using Polly.Timeout;
using ResilienceDemo.Core;

/// <summary>
/// Runs the resilience demos. Usage:
///   dotnet run --project src/ResilienceDemo.Demo                       (all demos)
///   dotnet run --project src/ResilienceDemo.Demo -- breaker            (one demo)
/// Demos: retry, breaker, timeout, fallback, hedging, combined.
/// Everything runs against FlakyService — no network, no Docker needed.
/// </summary>
void Log(string message) => Console.WriteLine($"  {message}");

var demos = args.Length == 0
    ? new[] { "retry", "breaker", "timeout", "fallback", "hedging", "combined" }
    : args;

foreach (var demo in demos)
{
    switch (demo)
    {
        case "retry": await RetryDemo(); break;
        case "breaker": await BreakerDemo(); break;
        case "timeout": await TimeoutDemo(); break;
        case "fallback": await FallbackDemo(); break;
        case "hedging": await HedgingDemo(); break;
        case "combined": await CombinedDemo(); break;
        default: Console.WriteLine($"Unknown demo: {demo} (try: retry, breaker, timeout, fallback, hedging, combined)"); break;
    }
}

async Task RetryDemo()
{
    Console.WriteLine("\n== RETRY: fails twice, then succeeds ==");
    var service = FlakyService.FailTimesThenSucceed(2);
    var pipeline = ResiliencePolicies.RetryPipeline(log: Log);
    var result = await pipeline.ExecuteAsync(ct => service.CallAsync(ct));
    Console.WriteLine($"  result={result}, attempts={service.Attempts} (one failure is not an outage — retry absorbed it)");
}

async Task BreakerDemo()
{
    Console.WriteLine("\n== CIRCUIT BREAKER: fail fast while the service is down ==");
    var service = FlakyService.AlwaysFail();
    var pipeline = ResiliencePolicies.CircuitBreakerPipeline(TimeSpan.FromSeconds(2), log: Log);
    for (var i = 1; i <= 3; i++)
    {
        try
        {
            await pipeline.ExecuteAsync(ct => service.CallAsync(ct));
            Console.WriteLine($"  call {i}: succeeded (unexpected)");
        }
        catch (BrokenCircuitException)
        {
            Console.WriteLine($"  call {i}: BrokenCircuitException — rejected instantly, service not even tried (attempts={service.Attempts})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  call {i}: {ex.GetType().Name} (attempts={service.Attempts})");
        }
    }
    Console.WriteLine("  waiting out the 2 s break, then a recovering service gets one trial call...");
    await Task.Delay(TimeSpan.FromSeconds(2.2));
    var recovering = FlakyService.FailTimesThenSucceed(0);
    var result = await pipeline.ExecuteAsync(ct => recovering.CallAsync(ct));
    Console.WriteLine($"  trial call: {result} — breaker closes, traffic resumes");
}

async Task TimeoutDemo()
{
    Console.WriteLine("\n== TIMEOUT: a 2 s call with a 200 ms deadline ==");
    var service = FlakyService.Slow(TimeSpan.FromSeconds(2));
    var pipeline = ResiliencePolicies.TimeoutPipeline(TimeSpan.FromMilliseconds(200), log: Log);
    try
    {
        await pipeline.ExecuteAsync(ct => service.CallAsync(ct));
    }
    catch (TimeoutRejectedException)
    {
        Console.WriteLine("  TimeoutRejectedException after ~200 ms — the caller was freed instead of waiting 2 s");
    }
}

async Task FallbackDemo()
{
    Console.WriteLine("\n== FALLBACK: the service is down, the page still renders ==");
    var service = FlakyService.AlwaysFail();
    var pipeline = ResiliencePolicies.FallbackPipeline("cached-value", log: Log);
    var result = await pipeline.ExecuteAsync(ct => service.CallAsync(ct));
    Console.WriteLine($"  result={result} — degraded, but the user got an answer instead of a 500");
}

async Task HedgingDemo()
{
    Console.WriteLine("\n== HEDGING: slow primary vs fast replica, first to finish wins ==");
    var slow = FlakyService.Slow(TimeSpan.FromMilliseconds(800), "slow-primary");
    var pipeline = ResiliencePolicies.HedgingPipeline(
        TimeSpan.FromMilliseconds(100),
        _ => ValueTask.FromResult("fast-replica"));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var result = await pipeline.ExecuteAsync(ct => slow.CallAsync(ct));
    Console.WriteLine($"  result={result} in {stopwatch.ElapsedMilliseconds} ms (not 800 ms — hedging cut the tail latency)");
}

async Task CombinedDemo()
{
    Console.WriteLine("\n== COMBINED: the pipeline you would actually ship ==");
    var flaky = FlakyService.FailTimesThenSucceed(2);
    var pipeline = ResiliencePolicies.StandardPipeline(log: Log);
    var result = await pipeline.ExecuteAsync(ct => flaky.CallAsync(ct));
    Console.WriteLine($"  result={result}, attempts={flaky.Attempts} — retries absorbed the blip, breaker stayed closed");

    // Fresh pipeline: breaker state is per pipeline instance, so the
    // always-fail run below is not polluted by the run above (whose
    // breaker would still be counting those earlier failures).
    var down = FlakyService.AlwaysFail();
    var downPipeline = ResiliencePolicies.StandardPipeline(log: Log);
    result = await downPipeline.ExecuteAsync(ct => down.CallAsync(ct));
    Console.WriteLine($"  service fully down: result={result}, attempts={down.Attempts} — retried, then fell back gracefully");
}
