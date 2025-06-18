using log4net;
using System;

namespace InvenAdClicker.Utils
{
    public class Log4NetLogger : ILogger
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(Log4NetLogger));

        public void Info(string msg)                  => _log.Info(msg);
        public void Warn(string msg)                  => _log.Warn(msg);
        public void Error(string msg, Exception ex)   => _log.Error(msg, ex);
    }
}
