using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public class RollingFileLoggerProvider : ILoggerProvider
    {
        private readonly RollingFileLogger _logger;

        public RollingFileLoggerProvider()
        {
            _logger = new RollingFileLogger();
        }

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose()
        {
            _logger.Dispose();
        }

        private class RollingFileLogger : ILogger, IDisposable
        {
            private readonly Channel<LogEntry> _logChannel;
            private readonly Task _writeTask;
            private readonly CancellationTokenSource _cts;
            private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            private struct LogEntry
            {
                public DateTime Timestamp;
                public LogLevel Level;
                public string Message;
                public int ThreadId;
                public Exception? Exception;
            }

            public RollingFileLogger()
            {
                // Bounded Channel to apply backpressure if disk I/O is too slow
                _logChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(10000)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest // Prevent memory bloat
                });
                _cts = new CancellationTokenSource();
                _writeTask = Task.Run(ProcessLogQueue);
            }

            public void Dispose()
            {
                _logChannel.Writer.TryComplete();
                _cts.Cancel();
                try
                {
                    _writeTask.Wait(1000); // Wait for remaining logs to flush
                }
                catch { }
                _cts.Dispose();
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var msg = formatter(state, exception);
                if (string.IsNullOrEmpty(msg)) return;

                var entry = new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = logLevel,
                    Message = msg,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    Exception = exception
                };

                _logChannel.Writer.TryWrite(entry);
            }

            private async Task ProcessLogQueue()
            {
                try
                {
                    while (await _logChannel.Reader.WaitToReadAsync(_cts.Token))
                    {
                        while (_logChannel.Reader.TryRead(out var entry))
                        {
                            await WriteLogAsync(entry);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    try { Console.WriteLine($"[FATAL] Logger failed: {ex}"); } catch { }
                }
            }

            private async Task WriteLogAsync(LogEntry entry)
            {
                var line = FormatLogLine(entry);

                // Avoid corrupting the interactive progress UI; print to console only when output is redirected.
                if (Console.IsOutputRedirected)
                {
                    lock (ConsoleLocker.Lock)
                    {
                        try
                        {
                            var originalColor = Console.ForegroundColor;
                            Console.ForegroundColor = GetColor(entry.Level);
                            Console.WriteLine(line);
                            if (entry.Exception != null)
                            {
                                Console.WriteLine(entry.Exception.ToString());
                            }
                            Console.ForegroundColor = originalColor;
                        }
                        catch { }
                    }
                }

                // File I/O is now async and lock-free (single consumer)
                try
                {
                    var dailyPath = GetDailyPath(entry.Timestamp);
                    EnsureDirectory(dailyPath);
                    
                    var sb = new StringBuilder();
                    sb.AppendLine(line);
                    if (entry.Exception != null)
                    {
                        sb.AppendLine(entry.Exception.ToString());
                    }

                    await File.AppendAllTextAsync(dailyPath, sb.ToString(), Utf8NoBom, _cts.Token);

                    if (entry.Level == LogLevel.Critical)
                    {
                        var fatalPath = GetFatalPath(entry.Timestamp);
                        EnsureDirectory(fatalPath);
                        await File.AppendAllTextAsync(fatalPath, sb.ToString(), Utf8NoBom, _cts.Token);
                    }
                }
                catch
                {
                    // If file write fails, we drop it to avoid recursive failure
                }
            }

            private string FormatLogLine(LogEntry entry)
            {
                return $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss,fff}][{MapLevel(entry.Level)}] {entry.Message}";
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
                LogLevel.Trace => "DEBUG",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "FATAL",
                _ => "INFO"
            };

            private static ConsoleColor GetColor(LogLevel level) => level switch
            {
                LogLevel.Trace => ConsoleColor.DarkGray,
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Information => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Critical => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
