using log4net;
using System;

namespace InvenAdClicker.Utils
{
    public class Log4NetLogger : IAppLogger
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

        public void Error(string msg, Exception? ex = null)
        {
            lock (ConsoleLocker.Lock)
            {
                if (ex is null)
                    _log.Error(msg);
                else
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
