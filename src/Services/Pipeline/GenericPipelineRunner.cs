using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Pipeline
{
    public class GenericPipelineRunner<TPage> : IPipelineRunner where TPage : class
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly IBrowserPool<TPage> _browserPool;
        private readonly ProgressTracker _progress;
        private readonly IAdCollector<TPage> _adCollector;
        private readonly IAdClicker<TPage> _adClicker;
        private readonly PipelineShutdownOptions _shutdown;

        public GenericPipelineRunner(AppSettings settings, IAppLogger logger,
            IBrowserPool<TPage> browserPool, ProgressTracker progress,
            IAdCollector<TPage> adCollector, IAdClicker<TPage> adClicker,
            PipelineShutdownOptions? shutdownOptions = null)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
            _progress = progress;
            _adCollector = adCollector;
            _adClicker = adClicker;
            _shutdown = shutdownOptions ?? new PipelineShutdownOptions();
            _shutdown.Validate();
        }

        public async Task<PipelineRunResult> RunAsync(string[] urls, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(urls);
            if (urls.Length == 0)
                return new PipelineRunResult { Status = cancellationToken.IsCancellationRequested ? PipelineRunStatus.Cancelled : PipelineRunStatus.Success };
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var state = new RunState(urls.Length);
            var token = runCts.Token;
            int mdp = Math.Max(1, _settings.MaxDegreeOfParallelism);
            var urlChannel = Channel.CreateBounded<string>(Math.Max(4, mdp * 2));
            var clickChannel = Channel.CreateBounded<(string source, string link)>(Math.Max(100, mdp * 50));

            void Stop()
            {
                state.Stopped.TrySetResult();
                try { runCts.Cancel(); } catch (Exception ex) { state.AddCleanup(ex); }
                urlChannel.Writer.TryComplete();
                clickChannel.Writer.TryComplete();
            }

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                state.MarkCancelled();
                Stop();
            });

            async Task Guard(Func<Task> action)
            {
                try { await action(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (ChannelClosedException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    state.RecordFatal(ex);
                    Stop();
                }
            }

            // Task.Run without its scheduling token guarantees that every worker executes its finally.
            Task Worker(Func<PageLease, Task> action) => Task.Run(() => Guard(async () =>
            {
                var lease = new PageLease();
                try
                {
                    token.ThrowIfCancellationRequested();
                    lease.Page = await _browserPool.AcquireAsync(token);
                    await action(lease);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (ChannelClosedException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    state.RecordFatal(ex);
                    Stop();
                }
                finally
                {
                    if (lease.Page != null)
                    {
                        try { _browserPool.Release(lease.Page); }
                        catch (Exception ex) { state.AddCleanup(ex); Stop(); }
                    }
                }
            }));

            var tasks = new List<Task>();
            tasks.Add(Task.Run(() => Guard(async () =>
            {
                try
                {
                    await ProduceUrlsAsync(urls, urlChannel.Writer, token);
                }
                finally { urlChannel.Writer.TryComplete(); }
            })));

            if (mdp == 1)
            {
                tasks.Add(Worker(async lease =>
                {
                    await foreach (var url in urlChannel.Reader.ReadAllAsync(token))
                    {
                        var links = await CollectAsync(lease, url, state, token);
                        foreach (var link in links)
                        {
                            _progress.Update(url, pendingClicksDelta: 1);
                            await ClickAsync(lease, 0, url, link, state, token);
                        }
                    }
                }));
            }
            else
            {
                int remainingCollectors = mdp - 1;
                for (int i = 0; i < mdp - 1; i++)
                {
                    int id = i + 1;
                    tasks.Add(Worker(async lease =>
                    {
                        try
                        {
                            await foreach (var url in urlChannel.Reader.ReadAllAsync(token))
                            {
                                var links = await CollectAsync(lease, url, state, token);
                                foreach (var link in links)
                                {
                                    _progress.Update(url, pendingClicksDelta: 1);
                                    try { await clickChannel.Writer.WriteAsync((url, link), token); }
                                    catch { _progress.Update(url, pendingClicksDelta: -1); throw; }
                                }
                            }
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref remainingCollectors) == 0)
                                clickChannel.Writer.TryComplete();
                        }
                        await foreach (var work in clickChannel.Reader.ReadAllAsync(token))
                            await ClickAsync(lease, id, work.source, work.link, state, token);
                    }));
                }
                tasks.Add(Worker(async lease =>
                {
                    await foreach (var work in clickChannel.Reader.ReadAllAsync(token))
                        await ClickAsync(lease, 0, work.source, work.link, state, token);
                }));
            }

            var allTasks = Task.WhenAll(tasks);
            // Observe even dependencies that ignore both cancellation and browser abort and finish later.
            Observe(allTasks);
            bool shutdownTimedOut = false;
            var first = await Task.WhenAny(allTasks, state.Stopped.Task);
            if (first != allTasks || state.Stopped.Task.IsCompleted)
            {
                Stop();
                if (!await CompletesWithinAsync(allTasks, _shutdown.CooperativeTimeout))
                {
                    using var abortCts = new CancellationTokenSource(_shutdown.AbortTimeout);
                    Task abortTask;
                    try { abortTask = _browserPool.AbortAsync(abortCts.Token); }
                    catch (Exception ex) { state.AddCleanup(ex); abortTask = Task.CompletedTask; }
                    Observe(abortTask);
                    if (await CompletesWithinAsync(abortTask, _shutdown.AbortTimeout))
                    {
                        try { await abortTask; }
                        catch (Exception ex) { state.AddCleanup(ex); }
                    }
                    else
                    {
                        shutdownTimedOut = true;
                        state.AddCleanup(new TimeoutException("브라우저 작업 중단 제한 시간을 초과했습니다."));
                    }
                    if (!await CompletesWithinAsync(allTasks, _shutdown.FinalObservationTimeout))
                        shutdownTimedOut = true;
                }
            }
            if (allTasks.IsCompleted) await allTasks;
            urlChannel.Writer.TryComplete();
            clickChannel.Writer.TryComplete();
            // A timed-out run is explicitly unsuccessful; its counters are a snapshot at return time.
            return state.Result(shutdownTimedOut, tasks.Count(task => !task.IsCompleted));
        }

        protected virtual async Task ProduceUrlsAsync(string[] urls, ChannelWriter<string> writer, CancellationToken token)
        {
            foreach (var url in urls)
                await writer.WriteAsync(url, token);
        }

        private static void Observe(Task task) => _ = task.ContinueWith(
            completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted) return true;
            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(timeout, delayCts.Token);
            bool completed = await Task.WhenAny(task, delay) == task;
            if (completed) delayCts.Cancel();
            return completed;
        }

        private async Task<List<string>> CollectAsync(PageLease lease, string url, RunState state, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _progress.Update(url, ProgressStatus.Collecting, threadDelta: 1);
            try
            {
                List<string> links;
                try { links = await _adCollector.CollectLinksAsync(lease.Page!, url, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref state.CollectionFailed);
                    _logger.Error($"[Collector] Collection Error on {url}: {ex.Message}", ex);
                    _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                    lease.Page = await _browserPool.RenewAsync(lease.Page!, token);
                    return new List<string>();
                }
                Interlocked.Increment(ref state.CollectionSucceeded);
                Interlocked.Add(ref state.DiscoveredLinks, links.Count);
                if (links.Count == 0) Interlocked.Increment(ref state.NoAdsUrls);
                _progress.Update(url, links.Count == 0 ? ProgressStatus.NoAds : ProgressStatus.Collected, adsDelta: links.Count);
                return links;
            }
            finally { _progress.Update(url, threadDelta: -1); }
        }

        private async Task ClickAsync(PageLease lease, int id, string source, string link, RunState state, CancellationToken token)
        {
            _progress.Update(source, ProgressStatus.Clicking, threadDelta: 1);
            try
            {
                token.ThrowIfCancellationRequested();
                try { lease.Page = await _adClicker.ClickAdAsync(lease.Page!, link, id, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref state.ClickFailed);
                    _logger.Error($"[Clicker] Click Error on '{link}' (from '{source}'): {ex.Message}", ex);
                    _progress.Update(source, errDelta: 1);
                    lease.Page = await _browserPool.RenewAsync(lease.Page!, token);
                    return;
                }
                if (_settings.DryRun) Interlocked.Increment(ref state.SimulatedClicks);
                else Interlocked.Increment(ref state.ClickSucceeded);
                _progress.Update(source, clickDelta: 1);
            }
            finally { _progress.Update(source, pendingClicksDelta: -1, threadDelta: -1); }
        }

        private sealed class PageLease { public TPage? Page; }

        private sealed class RunState
        {
            private readonly object _gate = new();
            private readonly int _total;
            private readonly List<Exception> _cleanup = new();
            private Exception? _primary;
            private bool _cancelled;
            public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int CollectionSucceeded, CollectionFailed, NoAdsUrls, DiscoveredLinks, ClickSucceeded, ClickFailed, SimulatedClicks;
            public RunState(int total) { _total = total; }
            public void MarkCancelled() { lock (_gate) _cancelled = true; }
            public void RecordFatal(Exception ex)
            {
                lock (_gate)
                {
                    if (_primary == null && !_cancelled && !Stopped.Task.IsCompleted) _primary = ex;
                    else _cleanup.Add(ex);
                }
            }
            public void AddCleanup(Exception ex) { lock (_gate) _cleanup.Add(ex); }
            public PipelineRunResult Result(bool timedOut, int unfinished)
            {
                lock (_gate)
                {
                    var status = PipelineRunStatus.Success;
                    if (_primary != null || _cleanup.Count > 0 || timedOut || unfinished > 0) status = PipelineRunStatus.FatalFailure;
                    else if (_cancelled) status = PipelineRunStatus.Cancelled;
                    else if ((CollectionFailed > 0 && CollectionSucceeded == 0) ||
                        (ClickFailed > 0 && ClickSucceeded + SimulatedClicks == 0)) status = PipelineRunStatus.TotalFailure;
                    else if (CollectionFailed > 0 || ClickFailed > 0) status = PipelineRunStatus.PartialFailure;
                    return new PipelineRunResult
                    {
                        Status = status, TotalUrls = _total, CollectionSucceeded = CollectionSucceeded,
                        CollectionFailed = CollectionFailed, NoAdsUrls = NoAdsUrls, DiscoveredLinks = DiscoveredLinks,
                        ClickSucceeded = ClickSucceeded, ClickFailed = ClickFailed, SimulatedClicks = SimulatedClicks,
                        PrimaryException = _primary, CleanupErrors = _cleanup.ToArray(), ShutdownTimedOut = timedOut,
                        UnfinishedTaskCount = unfinished
                    };
                }
            }
        }
    }
}
