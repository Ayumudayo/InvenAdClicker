using Microsoft.Extensions.Logging;

namespace InvenAdClicker.Utils
{
    // 메시지 포맷은 Provider에서 처리.
    public class MsLoggerAdapter : IAppLogger
    {
        private readonly ILogger<MsLoggerAdapter> _logger;

        public MsLoggerAdapter(ILogger<MsLoggerAdapter> logger)
        {
            _logger = logger;
        }

        public void Info(string msg)
            => _logger.LogInformation("{Message}", msg);

        public void Warn(string msg)
            => _logger.LogWarning("{Message}", msg);

        public void Error(string msg, System.Exception? ex = null)
            => _logger.LogError(ex, "{Message}", msg);

        public void Debug(string msg)
            => _logger.LogDebug("{Message}", msg);

        public void Fatal(string msg, System.Exception? ex = null)
            => _logger.LogCritical(ex, "{Message}", msg);
    }
}
