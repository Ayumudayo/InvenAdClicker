using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Pipeline;
using InvenAdClicker.Utils;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Pipeline;

[TestFixture]
public class PipelineOutcomeTests
{
    private static readonly PipelineShutdownOptions ShortShutdown = new()
    {
        CooperativeTimeout = TimeSpan.FromMilliseconds(30),
        AbortTimeout = TimeSpan.FromMilliseconds(100),
        FinalObservationTimeout = TimeSpan.FromMilliseconds(100)
    };

    [TestCase(1, "success", PipelineRunStatus.Success, 0, 2, 0, 0, 2, 0, 0)]
    [TestCase(3, "success", PipelineRunStatus.Success, 0, 2, 0, 0, 2, 0, 0)]
    [TestCase(1, "noads", PipelineRunStatus.Success, 0, 2, 0, 2, 0, 0, 0)]
    [TestCase(3, "noads", PipelineRunStatus.Success, 0, 2, 0, 2, 0, 0, 0)]
    [TestCase(1, "somecollection", PipelineRunStatus.PartialFailure, 3, 1, 1, 0, 1, 0, 0)]
    [TestCase(3, "somecollection", PipelineRunStatus.PartialFailure, 3, 1, 1, 0, 1, 0, 0)]
    [TestCase(1, "allcollection", PipelineRunStatus.TotalFailure, 4, 0, 2, 0, 0, 0, 0)]
    [TestCase(3, "allcollection", PipelineRunStatus.TotalFailure, 4, 0, 2, 0, 0, 0, 0)]
    [TestCase(1, "someclick", PipelineRunStatus.PartialFailure, 3, 2, 0, 0, 1, 1, 0)]
    [TestCase(3, "someclick", PipelineRunStatus.PartialFailure, 3, 2, 0, 0, 1, 1, 0)]
    [TestCase(1, "allclick", PipelineRunStatus.TotalFailure, 4, 2, 0, 0, 0, 2, 0)]
    [TestCase(3, "allclick", PipelineRunStatus.TotalFailure, 4, 2, 0, 0, 0, 2, 0)]
    [TestCase(1, "dryrun", PipelineRunStatus.Success, 0, 2, 0, 0, 0, 0, 2)]
    [TestCase(3, "dryrun", PipelineRunStatus.Success, 0, 2, 0, 0, 0, 0, 2)]
    public async Task ResultCountsUseSeparateUrlAndLinkUnits(int workers, string scenario, PipelineRunStatus expected,
        int exitCode, int collected, int collectionFailed, int noAds, int clicked, int clickFailed, int simulated)
    {
        var pool = new Pool();
        var runner = MakeRunner(workers, pool, (_, url, _) =>
        {
            if (scenario == "allcollection" || scenario == "somecollection" && url == "a")
                throw new InvalidOperationException("collection");
            return Task.FromResult(scenario == "noads" ? new List<string>() : new List<string> { url + "-link" });
        }, (page, link, _, _) =>
        {
            if (scenario == "allclick" || scenario == "someclick" && link == "a-link")
                throw new InvalidOperationException("click");
            return Task.FromResult(page);
        }, dryRun: scenario == "dryrun");
        var result = await runner.RunAsync(new[] { "a", "b" });
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(expected));
            Assert.That(result.ExitCode, Is.EqualTo(exitCode));
            Assert.That(result.TotalUrls, Is.EqualTo(2));
            Assert.That(result.CollectionSucceeded, Is.EqualTo(collected));
            Assert.That(result.CollectionFailed, Is.EqualTo(collectionFailed));
            Assert.That(result.NoAdsUrls, Is.EqualTo(noAds));
            Assert.That(result.DiscoveredLinks, Is.EqualTo(clicked + clickFailed + simulated));
            Assert.That(result.ClickSucceeded, Is.EqualTo(clicked));
            Assert.That(result.ClickFailed, Is.EqualTo(clickFailed));
            Assert.That(result.SimulatedClicks, Is.EqualTo(simulated));
            Assert.That(result.UnprocessedUrls, Is.Zero);
            Assert.That(result.UnprocessedLinks, Is.Zero);
            Assert.That(result.UnfinishedTaskCount, Is.Zero);
            Assert.That(pool.Leases, Is.Zero);
        });
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task FatalRenewalRetainsExceptionIdentityWhenReleaseAlsoFails(int workers)
    {
        var primary = new InvalidOperationException("renewal fatal");
        var cleanup = new ApplicationException("release failed");
        var pool = new Pool { RenewalError = primary, ReleaseError = cleanup };
        var result = await MakeRunner(workers, pool, (_, _, _) => throw new Exception("recoverable collection"))
            .RunAsync(new[] { "a" });
        Assert.Multiple(() =>
        {
            Assert.That(result.PrimaryException, Is.SameAs(primary));
            Assert.That(result.CleanupErrors, Does.Contain(cleanup));
            Assert.That(result.ExitCode, Is.EqualTo(2));
            Assert.That(result.UnfinishedTaskCount, Is.Zero);
        });
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task CancellationDuringCollectionStopsProducerAndAllWorkers(int workers)
    {
        using var cts = new CancellationTokenSource();
        var started = Signal();
        var pool = new Pool();
        var runner = MakeRunner(workers, pool, async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new List<string>();
        });
        // Enough input to keep the bounded producer waiting while collectors are in-flight.
        var run = runner.RunAsync(Enumerable.Range(0, 1000).Select(i => i.ToString()).ToArray(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.Cancelled));
            Assert.That(result.ExitCode, Is.EqualTo(1));
            Assert.That(result.UnprocessedUrls, Is.EqualTo(1000));
            Assert.That(result.UnfinishedTaskCount, Is.Zero);
            Assert.That(pool.Leases, Is.Zero);
        });
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task CancellationDuringClickCountsDiscoveredButUnfinishedLinks(int workers)
    {
        using var cts = new CancellationTokenSource();
        var started = Signal();
        var pool = new Pool();
        var runner = MakeRunner(workers, pool, (_, _, _) => Task.FromResult(Enumerable.Range(0, 1000).Select(i => i.ToString()).ToList()),
            async (_, _, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "never";
            });
        var run = runner.RunAsync(new[] { "a" }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.Cancelled));
            Assert.That(result.CollectionSucceeded, Is.EqualTo(1));
            Assert.That(result.DiscoveredLinks, Is.EqualTo(1000));
            Assert.That(result.UnprocessedLinks, Is.EqualTo(1000));
            Assert.That(result.ClickFailed, Is.Zero);
            Assert.That(result.UnfinishedTaskCount, Is.Zero);
            Assert.That(pool.Leases, Is.Zero);
        });
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task CancellationWhilePoolAcquisitionWaitsStopsEveryTask(int workers)
    {
        using var cts = new CancellationTokenSource();
        var started = Signal();
        var pool = new Pool { Acquire = async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        }};
        var run = MakeRunner(workers, pool).RunAsync(new[] { "a" }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.Cancelled));
        Assert.That(result.UnfinishedTaskCount, Is.Zero);
    }

    [Test]
    public async Task BrowserAbortUnblocksNonCooperativeNavigationBeforeReturning()
    {
        using var cts = new CancellationTokenSource();
        var started = Signal();
        var release = Signal();
        var pool = new Pool { Abort = _ => { release.TrySetResult(); return Task.CompletedTask; } };
        var run = MakeRunner(1, pool, async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            throw new OperationCanceledException();
        }).RunAsync(new[] { "a" }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(pool.AbortCalls, Is.EqualTo(1));
            Assert.That(pool.Leases, Is.Zero);
            Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.Cancelled));
            Assert.That(result.UnfinishedTaskCount, Is.Zero);
            Assert.That(result.ShutdownTimedOut, Is.False);
        });
    }

    [Test]
    public async Task UncooperativeDependencyReportsTimeoutAndLateReleaseRemainsObserved()
    {
        using var cts = new CancellationTokenSource();
        var started = Signal();
        var release = Signal();
        var released = Signal();
        var pool = new Pool { OnRelease = () => released.TrySetResult() };
        var run = MakeRunner(1, pool, async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            throw new ApplicationException("late browser error");
        }).RunAsync(new[] { "a" }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        try
        {
            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.FatalFailure));
                Assert.That(result.ExitCode, Is.EqualTo(2));
                Assert.That(result.ShutdownTimedOut, Is.True);
                Assert.That(result.UnfinishedTaskCount, Is.EqualTo(1));
                Assert.That(result.UnprocessedUrls, Is.EqualTo(1));
            });
        }
        finally
        {
            release.TrySetResult();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.That(pool.Leases, Is.Zero);
    }

    [Test]
    public async Task ProducerFailureCancelsConsumersWaitingOnEmptyChannels()
    {
        var error = new ApplicationException("producer failure");
        var pool = new Pool();
        var progress = new ProgressTracker(); progress.Initialize(new[] { "a" });
        var runner = new FailingProducer(new AppSettings { MaxDegreeOfParallelism = 3 }, pool, progress, error);
        var result = await runner.RunAsync(new[] { "a" }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.PrimaryException, Is.SameAs(error));
        Assert.That(result.UnprocessedUrls, Is.EqualTo(1));
        Assert.That(result.UnfinishedTaskCount, Is.Zero);
        Assert.That(pool.Leases, Is.Zero);
    }

    [TestCase(1)]
    [TestCase(3)]
    public async Task FatalClickRenewalCancelsPeerCollectionAndKeepsFatalStatus(int workers)
    {
        using var cts = new CancellationTokenSource();
        var error = new ApplicationException("click renewal");
        var pool = new Pool { RenewalError = error };
        var runner = MakeRunner(workers, pool, click: (_, _, _, _) => throw new Exception("click failed"));
        var result = await runner.RunAsync(new[] { "a", "b" }, cts.Token);
        cts.Cancel();
        Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.FatalFailure));
        Assert.That(result.PrimaryException, Is.SameAs(error));
        Assert.That(result.ClickFailed, Is.GreaterThanOrEqualTo(1));
        Assert.That(result.UnfinishedTaskCount, Is.Zero);
    }

    [Test]
    public async Task ReleaseFailureAfterUserCancellationIsFatalCleanupFailure()
    {
        using var cts = new CancellationTokenSource();
        var started = Signal();
        var cleanup = new ApplicationException("release");
        var pool = new Pool { ReleaseError = cleanup };
        var run = MakeRunner(1, pool, async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new List<string>();
        }).RunAsync(new[] { "a" }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var result = await run;
        Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.FatalFailure));
        Assert.That(result.PrimaryException, Is.Null);
        Assert.That(result.CleanupErrors, Does.Contain(cleanup));
    }

    [Test]
    public void LateScopeCleanupPreservesPrimaryExceptionAndMakesCancelledRunFatal()
    {
        var primary = new ApplicationException("primary");
        var cleanup = new Exception("scope");
        var fatal = new PipelineRunResult { Status = PipelineRunStatus.FatalFailure, PrimaryException = primary };
        Assert.That(fatal.WithCleanupError(cleanup).PrimaryException, Is.SameAs(primary));
        var cancelled = new PipelineRunResult { Status = PipelineRunStatus.Cancelled };
        var result = cancelled.WithCleanupError(cleanup);
        Assert.That(result.ExitCode, Is.EqualTo(2));
        Assert.That(result.CleanupErrors, Does.Contain(cleanup));
        Assert.That(cancelled.CleanupErrors, Is.Empty);
    }

    [Test]
    public async Task EmptyInputDoesNotAcquireResourcesOrBecomeTotalFailure()
    {
        var pool = new Pool { Acquire = _ => throw new Exception("must not acquire") };
        var result = await MakeRunner(3, pool).RunAsync(Array.Empty<string>());
        Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.Success));
        Assert.That(result.TotalUrls, Is.Zero);
    }

    [Test]
    public async Task RenewedPageIsReleasedWhenFollowingCollectionIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        string? releasedPage = null;
        var pool = new Pool { OnPageReleased = page => releasedPage = page };
        var runner = MakeRunner(1, pool, (_, url, _) =>
        {
            if (url == "a") throw new Exception("renew");
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var result = await runner.RunAsync(new[] { "a", "b" }, cts.Token);
        Assert.That(result.Status, Is.EqualTo(PipelineRunStatus.Cancelled));
        Assert.That(releasedPage, Is.EqualTo("renewed"));
        Assert.That(pool.Leases, Is.Zero);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static GenericPipelineRunner<string> MakeRunner(int workers, Pool pool,
        Func<string, string, CancellationToken, Task<List<string>>>? collect = null,
        Func<string, string, int, CancellationToken, Task<string>>? click = null, bool dryRun = false)
    {
        var progress = new ProgressTracker(); progress.Initialize(new[] { "a", "b" });
        return new GenericPipelineRunner<string>(new AppSettings { MaxDegreeOfParallelism = workers, DryRun = dryRun },
            new Logger(), pool, progress,
            new Collector(collect ?? ((_, url, _) => Task.FromResult(new List<string> { url + "-link" }))),
            new Clicker(click ?? ((page, _, _, _) => Task.FromResult(page))), ShortShutdown);
    }

    private sealed class FailingProducer : GenericPipelineRunner<string>
    {
        private readonly Exception _error;
        public FailingProducer(AppSettings settings, Pool pool, ProgressTracker progress, Exception error)
            : base(settings, new Logger(), pool, progress,
                new Collector((_, _, _) => Task.FromResult(new List<string>())),
                new Clicker((p, _, _, _) => Task.FromResult(p)), ShortShutdown) { _error = error; }
        protected override Task ProduceUrlsAsync(string[] urls, ChannelWriter<string> writer, CancellationToken token) => throw _error;
    }

    private sealed class Collector(Func<string, string, CancellationToken, Task<List<string>>> action) : IAdCollector<string>
    {
        public Task<List<string>> CollectLinksAsync(string page, string url, CancellationToken token) => action(page, url, token);
    }
    private sealed class Clicker(Func<string, string, int, CancellationToken, Task<string>> action) : IAdClicker<string>
    {
        public Task<string> ClickAdAsync(string page, string link, int id, CancellationToken token) => action(page, link, id, token);
    }
    private sealed class Pool : IBrowserPool<string>
    {
        public Func<CancellationToken, Task<string>>? Acquire;
        public Func<CancellationToken, Task>? Abort;
        public Exception? RenewalError, ReleaseError;
        public Action? OnRelease;
        public Action<string>? OnPageReleased;
        public int Leases, AbortCalls;
        public Task InitializePoolAsync(CancellationToken token = default) => Task.CompletedTask;
        public async Task<string> AcquireAsync(CancellationToken token = default)
        {
            var page = Acquire == null ? "page" : await Acquire(token);
            Interlocked.Increment(ref Leases);
            return page;
        }
        public Task<string> RenewAsync(string old, CancellationToken token = default) =>
            RenewalError != null ? Task.FromException<string>(RenewalError) : Task.FromResult("renewed");
        public void Release(string page)
        {
            Interlocked.Decrement(ref Leases);
            OnPageReleased?.Invoke(page);
            OnRelease?.Invoke();
            if (ReleaseError != null) throw ReleaseError;
        }
        public Task AbortAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref AbortCalls);
            return Abort?.Invoke(token) ?? Task.CompletedTask;
        }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Logger : IAppLogger
    {
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg, Exception? ex = null) { }
        public void Fatal(string msg, Exception? ex = null) { }
    }
}
