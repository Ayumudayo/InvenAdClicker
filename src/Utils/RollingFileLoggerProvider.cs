using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace InvenAdClicker.Utils
{
    // logs/yyyy/MM/yyyy-MM-dd.log 및 logs/fatal.log(Critical 전용)로 기록
    // 포맷: "[yyyy-MM-dd HH:mm:ss,fff][LEVEL] message"
    public class RollingFileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RollingFileLogger();
        public void Dispose() { }

        private class RollingFileLogger : ILogger
        {
            private static readonly object Sync = new object();
            private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, exception);
                if (string.IsNullOrEmpty(msg)) return;

                var now = DateTime.Now; // 로컬 시간
                var line = $"[{now:yyyy-MM-dd HH:mm:ss,fff}][{MapLevel(logLevel)}] {msg}";

                lock (ConsoleLocker.Lock)
                {
                    try
                    {
                        var dailyPath = GetDailyPath(now);
                        EnsureDirectory(dailyPath);
                        var dailyText = exception is null ?
                            line + Environment.NewLine :
                            line + Environment.NewLine + exception.ToString() + Environment.NewLine;
                        File.AppendAllText(dailyPath, dailyText, Utf8NoBom);

                        if (logLevel == LogLevel.Critical)
                        {
                            var fatalLine = $"[{now:yyyy-MM-dd HH:mm:ss,fff}][Thread : {Thread.CurrentThread.ManagedThreadId}][{MapLevel(logLevel)}] {msg}";
                            var fatalPath = GetFatalPath(now);
                            EnsureDirectory(fatalPath);
                            var fatalText = exception is null ?
                                fatalLine + Environment.NewLine :
                                fatalLine + Environment.NewLine + exception.ToString() + Environment.NewLine;
                            File.AppendAllText(fatalPath, fatalText, Utf8NoBom);
                        }
                    }
                    catch
                    {
                        try { Console.WriteLine(line); } catch { }
                    }
                }
            }

            private static string GetDailyPath(DateTime now)
            {
                var dir = Path.Combine("logs", now.ToString("yyyy"), now.ToString("MM"));
                var file = now.ToString("yyyy-MM-dd") + ".log";
                return Path.Combine(dir, file);
            }

            private static string GetFatalPath(DateTime now)
            {
                var dir = Path.Combine("logs", "fatal", now.ToString("yyyy"), now.ToString("MM"));
                var file = now.ToString("yyyy-MM-dd") + ".log";
                return Path.Combine(dir, file);
            }

            private static void EnsureDirectory(string filePath)
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
            }

            private static string MapLevel(LogLevel level) => level switch
            {
                LogLevel.Trace => "DEBUG", // 세분 Trace는 DEBUG로 통합
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "FATAL",
                _ => "INFO"
            };
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
