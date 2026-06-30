using InvenAdClicker.Utils;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Utils
{
    [TestFixture]
    public class RollingFileLoggerProviderTests
    {
        [Test]
        public void Dispose_WritesOneTimestampedRunLogAndMatchingFatalLog()
        {
            string originalDirectory = Directory.GetCurrentDirectory();
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"inven-log-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            Directory.SetCurrentDirectory(tempDirectory);

            try
            {
                using (var provider = new RollingFileLoggerProvider())
                {
                    var logger = provider.CreateLogger("test");
                    logger.LogInformation("regular message");
                    logger.LogCritical(new InvalidOperationException("boom"), "fatal message");
                }

                var normalLogs = Directory
                    .GetFiles(Path.Combine(tempDirectory, "logs"), "*.log", SearchOption.AllDirectories)
                    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}fatal{Path.DirectorySeparatorChar}"))
                    .ToArray();
                var fatalLogs = Directory.GetFiles(
                    Path.Combine(tempDirectory, "logs", "fatal"),
                    "*.log",
                    SearchOption.AllDirectories);

                Assert.That(normalLogs, Has.Length.EqualTo(1));
                Assert.That(fatalLogs, Has.Length.EqualTo(1));
                Assert.That(Path.GetFileName(normalLogs[0]), Does.Match(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}\.log$"));
                Assert.That(Path.GetFileName(fatalLogs[0]), Is.EqualTo(Path.GetFileName(normalLogs[0])));

                string normalContent = File.ReadAllText(normalLogs[0]);
                string fatalContent = File.ReadAllText(fatalLogs[0]);
                Assert.That(normalContent, Does.Contain("regular message"));
                Assert.That(normalContent, Does.Contain("fatal message"));
                Assert.That(fatalContent, Does.Contain("fatal message"));
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDirectory);
                try { Directory.Delete(tempDirectory, recursive: true); } catch { }
            }
        }

        [Test]
        public async Task Log_DoesNotBlockWhenQueueIsFull()
        {
            int appends = 0;
            var releaseAppender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var provider = new RollingFileLoggerProvider(
                capacity: 1,
                fileAppender: (_, _, _, _) =>
                {
                    Interlocked.Increment(ref appends);
                    return releaseAppender.Task;
                },
                disposeTimeout: TimeSpan.FromMilliseconds(100));

            var logger = provider.CreateLogger("test");
            logger.LogInformation("first");
            Assert.That(
                SpinWait.SpinUntil(() => Volatile.Read(ref appends) > 0, TimeSpan.FromSeconds(1)),
                Is.True);

            logger.LogInformation("second");
            var blockedCandidate = Task.Run(() => logger.LogInformation("third"));

            try
            {
                Task completed = await Task.WhenAny(blockedCandidate, Task.Delay(TimeSpan.FromMilliseconds(500)));
                Assert.That(completed, Is.SameAs(blockedCandidate),
                    "Logging must not block application threads when the bounded queue is saturated.");
            }
            finally
            {
                releaseAppender.TrySetResult();
            }
        }

        [Test]
        public async Task Dispose_ReturnsPromptlyWhenAppenderDoesNotComplete()
        {
            var appenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAppender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new RollingFileLoggerProvider(
                capacity: 10,
                fileAppender: (_, _, _, _) =>
                {
                    appenderStarted.TrySetResult();
                    return releaseAppender.Task;
                },
                disposeTimeout: TimeSpan.FromMilliseconds(50));

            provider.CreateLogger("test").LogInformation("message");
            await appenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var disposeTask = Task.Run(provider.Dispose);

            try
            {
                Task completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
                Assert.That(completed, Is.SameAs(disposeTask),
                    "Logger disposal must not block process shutdown indefinitely when file I/O is wedged.");
            }
            finally
            {
                releaseAppender.TrySetResult();
                await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            }
        }
    }
}
