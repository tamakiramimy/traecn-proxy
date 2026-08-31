using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TrancnProxy;

public static class TraeStreamHeartbeat
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    public static async IAsyncEnumerable<T> ReadAsync<T>(
        IAsyncEnumerable<T> source,
        Func<CancellationToken, ValueTask> writeHeartbeat,
        TimeSpan? interval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writeHeartbeat);
        TimeSpan heartbeatInterval = interval ?? DefaultInterval;
        if (heartbeatInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        long lastHeartbeat = Stopwatch.GetTimestamp();
        while (true)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(lastHeartbeat);
            TimeSpan delayUntilHeartbeat = elapsed < heartbeatInterval
                ? heartbeatInterval - elapsed
                : TimeSpan.Zero;
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = Task.Delay(delayUntilHeartbeat, delayCancellation.Token);
            Task completed = await Task.WhenAny(moveNext, delay);
            if (completed == moveNext)
            {
                delayCancellation.Cancel();
                if (!await moveNext) yield break;
                yield return enumerator.Current;
                moveNext = enumerator.MoveNextAsync().AsTask();
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await writeHeartbeat(cancellationToken);
            lastHeartbeat = Stopwatch.GetTimestamp();
        }
    }
}
