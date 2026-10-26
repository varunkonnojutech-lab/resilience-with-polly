namespace ResilienceDemo.Core;

/// <summary>
/// A stand-in for a flaky downstream dependency: a database, a third-party
/// API, another microservice. You program its behaviour up front — fail N
/// times then succeed, always fail, answer slowly — and it counts every
/// attempt, so demos and tests can prove exactly what a policy did.
/// </summary>
public sealed class FlakyService
{
    private readonly Queue<Func<CancellationToken, ValueTask<string>>> _script;
    private readonly Func<CancellationToken, ValueTask<string>>? _whenScriptExhausted;

    public FlakyService(
        IEnumerable<Func<CancellationToken, ValueTask<string>>> script,
        Func<CancellationToken, ValueTask<string>>? whenScriptExhausted = null)
    {
        _script = new Queue<Func<CancellationToken, ValueTask<string>>>(script);
        _whenScriptExhausted = whenScriptExhausted;
    }

    /// <summary>Number of times <see cref="CallAsync"/> has been invoked.</summary>
    public int Attempts { get; private set; }

    public async ValueTask<string> CallAsync(CancellationToken cancellationToken = default)
    {
        Attempts++;
        if (_script.Count > 0)
        {
            return await _script.Dequeue()(cancellationToken);
        }

        if (_whenScriptExhausted is not null)
        {
            return await _whenScriptExhausted(cancellationToken);
        }

        return "ok";
    }

    /// <summary>Fails <paramref name="failures"/> times, then returns <paramref name="value"/>.</summary>
    public static FlakyService FailTimesThenSucceed(int failures, string value = "ok")
    {
        var script = Enumerable.Repeat<Func<CancellationToken, ValueTask<string>>>(
            _ => ValueTask.FromException<string>(new HttpRequestException("Simulated transient failure")),
            failures);
        return new FlakyService(script.Append(_ => ValueTask.FromResult(value)));
    }

    /// <summary>Never succeeds.</summary>
    public static FlakyService AlwaysFail()
    {
        return new FlakyService(
            Array.Empty<Func<CancellationToken, ValueTask<string>>>(),
            _ => ValueTask.FromException<string>(new HttpRequestException("Simulated permanent failure")));
    }

    /// <summary>Answers after <paramref name="delay"/> (respects cancellation).</summary>
    public static FlakyService Slow(TimeSpan delay, string value = "slow-ok")
    {
        return new FlakyService(new Func<CancellationToken, ValueTask<string>>[]
        {
            async ct =>
            {
                await Task.Delay(delay, ct);
                return value;
            },
        });
    }
}
