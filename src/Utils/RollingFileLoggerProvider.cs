using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
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

        public RollingFileLoggerProvider(
            int capacity,
            Func<string, string, Encoding, CancellationToken, Task>? fileAppender = null,
            TimeSpan? disposeTimeout = null)
        {
            _logger = new RollingFileLogger(capacity, fileAppender, disposeTimeout);
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
            private readonly string _runLogPath;
            private readonly string _fatalRunLogPath;
            private readonly Func<string, string, Encoding, CancellationToken, Task> _fileAppender;
            private readonly TimeSpan _disposeTimeout;
            private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            private struct LogEntry
            {
                public DateTime Timestamp;
                public LogLevel Level;
                public string Message;
                public int ThreadId;
                public Exception? Exception;
            }

            public RollingFileLogger(
                int capacity = 10000,
                Func<string, string, Encoding, CancellationToken, Task>? fileAppender = null,
                TimeSpan? disposeTimeout = null)
            {
                // Keep logging bounded and non-blocking if disk I/O falls behind.
                _logChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(Math.Max(1, capacity))
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
                _cts = new CancellationTokenSource();
                var processStartedAt = Process.GetCurrentProcess().StartTime;
                _runLogPath = GetRunPath(processStartedAt);
                _fatalRunLogPath = GetFatalRunPath(processStartedAt);
                _fileAppender = fileAppender ?? File.AppendAllTextAsync;
                _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(1);
                _writeTask = Task.Run(ProcessLogQueue);
            }

            public void Dispose()
            {
                _logChannel.Writer.TryComplete();
                var completed = false;
                try
                {
                    completed = _writeTask.Wait(_disposeTimeout);
                }
                catch { }

                if (!completed)
                {
                    try { _cts.Cancel(); } catch { }
                    try { completed = _writeTask.Wait(TimeSpan.FromMilliseconds(100)); } catch { }
                }

                if (completed)
                {
                    _cts.Dispose();
                }
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
                    while (await _logChannel.Reader.WaitToReadAsync())
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
                    EnsureDirectory(_runLogPath);
                    
                    var sb = new StringBuilder();
                    sb.AppendLine(line);
                    if (entry.Exception != null)
                    {
                        sb.AppendLine(entry.Exception.ToString());
                    }

                    await _fileAppender(_runLogPath, sb.ToString(), Utf8NoBom, _cts.Token);

                    if (entry.Level == LogLevel.Critical)
                    {
                        EnsureDirectory(_fatalRunLogPath);
                        await _fileAppender(_fatalRunLogPath, sb.ToString(), Utf8NoBom, _cts.Token);
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

            private static string GetRunPath(DateTime processStartedAt)
            {
                var dir = Path.Combine("logs", processStartedAt.ToString("yyyy"), processStartedAt.ToString("MM"));
                var file = processStartedAt.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".log";
                return Path.Combine(dir, file);
            }

            private static string GetFatalRunPath(DateTime processStartedAt)
            {
                var dir = Path.Combine("logs", "fatal", processStartedAt.ToString("yyyy"), processStartedAt.ToString("MM"));
                var file = processStartedAt.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".log";
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
