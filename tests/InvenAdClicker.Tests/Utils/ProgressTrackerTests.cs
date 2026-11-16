using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.Utils;
using NUnit.Framework;

namespace InvenAdClicker.Tests.Utils
{
    public class ProgressTrackerTests
    {
        [Test]
        public void ConcurrentUpdatesKeepCountersAndSnapshotsDetached()
        {
            var state = new ProgressState();
            state.Initialize(new[] { "target" });
            var before = state.Snapshot().Single();

            Parallel.For(0, 1000, _ => state.Update("target", adsDelta: 1,
                clickDelta: 1, pendingClicksDelta: 1, errDelta: 1));

            var after = state.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(before.TotalAds, Is.Zero);
                Assert.That(after.TotalAds, Is.EqualTo(1000));
                Assert.That(after.ClickedAds, Is.EqualTo(1000));
                Assert.That(after.PendingClicks, Is.EqualTo(1000));
                Assert.That(after.Errors, Is.EqualTo(1000));
            });
        }

        [Test]
        public void LastClickFinishesTargetAndFreezesCompletedSpeed()
        {
            var state = new ProgressState();
            state.Initialize(new[] { "target" });
            state.Update("target", ProgressStatus.Clicking, pendingClicksDelta: 2);
            state.Update("target", clickDelta: 1, pendingClicksDelta: -1);
            Assert.That(state.Snapshot().Single().Status, Is.EqualTo(ProgressStatus.Clicking));
            state.Update("target", clickDelta: 1, pendingClicksDelta: -1, threadDelta: -1);

            var snapshot = state.Snapshot();
            var now = DateTime.UtcNow;
            var completed = ProgressMetrics.Calculate(snapshot, now);
            var later = ProgressMetrics.Calculate(snapshot, now.AddHours(1));
            Assert.Multiple(() =>
            {
                Assert.That(completed.IsComplete, Is.True);
                Assert.That(completed.Rows.Single().Threads, Is.Zero);
                Assert.That(completed.AverageClicksPerSecond, Is.GreaterThan(0));
                Assert.That(later.AverageClicksPerSecond, Is.EqualTo(completed.AverageClicksPerSecond));
            });
        }

        [Test]
        public void MetricsAggregateAllRowsAndPrioritizeActiveUrls()
        {
            var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
            var rows = new[]
            {
                new ProgressRowSnapshot("finished", ProgressStatus.Finished, 1, 6, 6, 0, 1, 0, 3, now.AddSeconds(-20)),
                new ProgressRowSnapshot("active", ProgressStatus.Clicking, 2, 8, 4, 4, 2, 1, null, now.AddSeconds(-2)),
                new ProgressRowSnapshot("empty", ProgressStatus.NoAds, 1, 0, 0, 0, 0, 0, null, null)
            };

            var metrics = ProgressMetrics.Calculate(rows, now);
            Assert.Multiple(() =>
            {
                Assert.That(metrics.Rows.First().Url, Is.EqualTo("active"));
                Assert.That(metrics.TotalAds, Is.EqualTo(14));
                Assert.That(metrics.ClickedAds, Is.EqualTo(10));
                Assert.That(metrics.PendingClicks, Is.EqualTo(4));
                Assert.That(metrics.Errors, Is.EqualTo(3));
                Assert.That(metrics.AverageClicksPerSecond, Is.EqualTo(2.5));
                Assert.That(metrics.IsComplete, Is.False);
            });
        }

        [TestCase(20, 2)]
        [TestCase(80, 25)]
        [TestCase(180, 8)]
        public void RendererBuildsSizedFrameFromSnapshot(int width, int height)
        {
            var state = new ProgressState();
            state.Initialize(new[] { "https://example.com" });
            var now = DateTime.UtcNow;
            var metrics = ProgressMetrics.Calculate(state.Snapshot(), now);
            var frame = new ConsoleDashboardRenderer().BuildFrame(metrics, now,
                12, 8, 8, 10, 6, width, height, false);

            Assert.Multiple(() =>
            {
                Assert.That(frame.Length, Is.EqualTo(height));
                Assert.That(frame.All(line => line.Text.Length == width), Is.True);
                Assert.That(string.Join("\n", frame.Select(line => line.Text)),
                    Does.Contain(width < 40 ? "Console window" : "Summary"));
            });
        }

        [Test]
        public async Task InitializeStartsFreshSessionAndClearsPreviousStopRequest()
        {
            var tracker = new ProgressTracker();
            tracker.Initialize(new[] { "old" });
            tracker.StopProgress();
            tracker.Initialize(new[] { "new" });

            // A stale stop flag would make this return before the cancellation is requested.
            using var cancellation = new CancellationTokenSource();
            var rendering = tracker.PrintProgressAsync(cancellation.Token);
            try
            {
                Assert.That(rendering.IsCompleted, Is.False);
                tracker.Update("new", ProgressStatus.NoAds);
                Assert.That(await Task.WhenAny(rendering, Task.Delay(2000)), Is.SameAs(rendering));
                await rendering;
            }
            finally
            {
                cancellation.Cancel();
                await rendering;
            }
        }
    }
}
