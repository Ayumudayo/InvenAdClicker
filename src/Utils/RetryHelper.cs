using System;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public static class RetryHelper
    {
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            int retryCount,
            ILogger logger,
            int baseDelayMs = 1000)
        {
            int attempts = 0;
            while (true)
            {
                try
                {
                    var result = await action();
                    if (attempts > 0)
                    {
                        logger.Info($"[RetryHelper] Succeeded on attempt {attempts + 1}");
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    attempts++;
                    if (attempts <= retryCount)
                    {
                        var delay = baseDelayMs * (int)Math.Pow(2, attempts - 1); // 지수 백오프
                        logger.Warn($"[RetryHelper] Attempt {attempts} failed: {ex.Message}. Retrying in {delay}ms...");
                        await Task.Delay(delay);
                    }
                    else
                    {
                        logger.Error($"[RetryHelper] All {retryCount} retries failed.", ex);
                        throw;
                    }
                }
            }
        }
    }
}