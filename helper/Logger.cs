using log4net.Config;

namespace InvenAdClicker.helper
{
    public static class Logger
    {
        public static readonly log4net.ILog log;

        static Logger()
        {
            XmlConfigurator.Configure(new FileInfo("log4net.config"));
            log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        }

        public static void Fatal(string message)
        {
            log.Fatal(message);
        }

        public static void Error(string message)
        {
            log.Error(message);
        }

        public static void Warn(string message)
        {
            log.Warn(message);
        }

        public static void Debug(string message)
        {
            log.Debug(message);
        }
        public static void Info(string message)
        {
            log.Info(message);
        }
    }
}