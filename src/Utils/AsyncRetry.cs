using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    internal static class AsyncRetry
    {
        internal static async Task<T> ExecuteAsync<T>(
            Func<int, Task<T>> operation,
            int maxAttempts,
            TimeSpan initialDelay,
            Func<Exception, bool> shouldRetry,
            CancellationToken cancellationToken,
            Func<int, Exception, Task>? beforeRetry = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            if (initialDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay));
            delay ??= Task.Delay;
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await operation(attempt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts && shouldRetry(ex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var milliseconds = Math.Min(30_000, initialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                    await delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (beforeRetry != null) await beforeRetry(attempt, ex);
                }
            }
        }
    }
}
