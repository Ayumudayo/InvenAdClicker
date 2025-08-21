using System;

namespace InvenAdClicker.Utils
{
    public interface IAppLogger
    {
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg, Exception? ex = null);
        void Debug(string msg);
        void Fatal(string msg, Exception? ex = null);
    }
}
