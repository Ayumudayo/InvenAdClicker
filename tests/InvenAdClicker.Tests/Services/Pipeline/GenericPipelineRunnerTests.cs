using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Pipeline;
using InvenAdClicker.Utils;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Pipeline
{
    [TestFixture]
    public class GenericPipelineRunnerTests
    {
        [Test]
        public async Task IdleCollectorSwitchesToClickingBeforeAllCollectorsFinish()
        {
            string slowUrl = $"slow-{Guid.NewGuid():N}";
            string burstUrl = $"burst-{Guid.NewGuid():N}";
            var progress = ProgressTracker.Instance;
            progress.Initialize(new[] { slowUrl, burstUrl });

            var slowCollectionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clickRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondClickerObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedClickerIds = new ConcurrentDictionary<int, byte>();

            var settings = new AppSettings
            {
                MaxDegreeOfParallelism = 4
            };

            var runner = new GenericPipelineRunner<string>(
                settings,
                new TestLogger(),
                new TestBrowserPool(),
                progress,
                new DelegateCollector(async (_, url, cancellationToken) =>
                {
                    if (url == slowUrl)
                    {
                        await slowCollectionRelease.Task.WaitAsync(cancellationToken);
                        return new List<string>();
                    }

                    return new List<string> { "link-1", "link-2", "link-3" };
                }),
                new DelegateClicker(async (page, _, clickerId, cancellationToken) =>
                {
                    startedClickerIds.TryAdd(clickerId, 0);
                    if (startedClickerIds.Count >= 2)
                    {
                        secondClickerObserved.TrySetResult();
                    }

                    await clickRelease.Task.WaitAsync(cancellationToken);
                    return page;
                }));

            var runTask = runner.RunAsync(new[] { slowUrl, burstUrl }, CancellationToken.None);

            try
            {
                bool switchedEarly = await WaitForAsync(secondClickerObserved.Task, TimeSpan.FromSeconds(1));
                Assert.That(switchedEarly, Is.True,
                    "남는 수집 워커는 모든 수집 종료를 기다리지 않고 클릭 작업으로 전환되어야 합니다.");
            }
            finally
            {
                slowCollectionRelease.TrySetResult();
                clickRelease.TrySetResult();
                await runTask;
            }
        }

        private static async Task<bool> WaitForAsync(Task task, TimeSpan timeout)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout));
            return completed == task;
        }

        private sealed class DelegateCollector : IAdCollector<string>
        {
            private readonly Func<string, string, CancellationToken, Task<List<string>>> _collectAsync;

            public DelegateCollector(Func<string, string, CancellationToken, Task<List<string>>> collectAsync)
            {
                _collectAsync = collectAsync;
            }

            public Task<List<string>> CollectLinksAsync(string page, string url, CancellationToken cancellationToken)
            {
                return _collectAsync(page, url, cancellationToken);
            }
        }

        private sealed class DelegateClicker : IAdClicker<string>
        {
            private readonly Func<string, string, int, CancellationToken, Task<string>> _clickAsync;

            public DelegateClicker(Func<string, string, int, CancellationToken, Task<string>> clickAsync)
            {
                _clickAsync = clickAsync;
            }

            public Task<string> ClickAdAsync(string page, string link, int clickerId, CancellationToken cancellationToken)
            {
                return _clickAsync(page, link, clickerId, cancellationToken);
            }
        }

        private sealed class TestBrowserPool : IBrowserPool<string>
        {
            private int _nextPageId;

            public Task InitializePoolAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<string> AcquireAsync(CancellationToken cancellationToken = default)
            {
                string page = $"page-{Interlocked.Increment(ref _nextPageId)}";
                return Task.FromResult(page);
            }

            public void Release(string browser)
            {
            }

            public Task<string> RenewAsync(string oldBrowser, CancellationToken cancellationToken = default)
            {
                return AcquireAsync(cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private sealed class TestLogger : IAppLogger
        {
            public void Debug(string msg)
            {
            }

            public void Error(string msg, Exception? ex = null)
            {
            }

            public void Fatal(string msg, Exception? ex = null)
            {
            }

            public void Info(string msg)
            {
            }

            public void Warn(string msg)
            {
            }
        }
    }
}
