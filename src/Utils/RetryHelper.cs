namespace InvenAdClicker.Utils
{
    public static class RetryHelper
    {
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            int retryCount,
            ILogger logger)
        {
            int attempts = 0;
            while (true)
            {
                logger.Info($"[RetryHelper] Attempt {attempts + 1} starting.");
                try
                {
                    var result = await action();
                    logger.Info($"[RetryHelper] Attempt {attempts + 1} succeeded.");
                    return result;
                }
                catch (Exception ex)
                {
                    attempts++;
                    if (attempts <= retryCount)
                    {
                        logger.Warn($"[RetryHelper] Attempt {attempts} failed: {ex.Message}. Retrying...");
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