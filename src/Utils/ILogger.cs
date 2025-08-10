using System;

namespace InvenAdClicker.Utils
{
    public interface ILogger
    {
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg, Exception? ex = null);
        void Debug(string msg);
    }
}
