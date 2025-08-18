using log4net;
using System;

namespace InvenAdClicker.Utils
{
    public class Log4NetLogger : ILogger
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(Log4NetLogger));

        public void Info(string msg)
        {
            lock (ConsoleLocker.Lock)
            {
                _log.Info(msg);
            }
        }

        public void Warn(string msg)
        {
            lock (ConsoleLocker.Lock)
            {
                _log.Warn(msg);
            }
        }

        public void Error(string msg, Exception ex)
        {
            lock (ConsoleLocker.Lock)
            {
                _log.Error(msg, ex);
            }
        }

        public void Debug(string msg)
        {
            lock (ConsoleLocker.Lock)
            {
                _log.Debug(msg);
            }
        }
    }
}
