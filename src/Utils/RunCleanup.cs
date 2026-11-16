using InvenAdClicker.Models;
using System;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    internal static class RunCleanup
    {
        // Async scopes have no cancellation API. Report a deadline and still observe late faults.
        internal static async Task<PipelineRunResult> DisposeAsync(
            PipelineRunResult result, Func<ValueTask> dispose, TimeSpan timeout)
        {
            Task? disposal = null;
            try
            {
                disposal = dispose().AsTask();
                await disposal.WaitAsync(timeout);
                return result;
            }
            catch (Exception ex)
            {
                if (disposal != null && !disposal.IsCompleted)
                    _ = disposal.ContinueWith(static task => { _ = task.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                return result.WithCleanupError(ex) with
                {
                    ShutdownTimedOut = result.ShutdownTimedOut || ex is TimeoutException
                };
            }
        }
    }
}
