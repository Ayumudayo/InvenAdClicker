
using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace InvenAdClicker.Services.Pipeline
{
    public class GenericPipelineRunner<TPage> : IPipelineRunner where TPage : class
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly IBrowserPool<TPage> _browserPool;
        private readonly ProgressTracker _progress;
        private readonly IAdCollector<TPage> _adCollector;
        private readonly IAdClicker<TPage> _adClicker;

        public GenericPipelineRunner(AppSettings settings, ILogger logger,
            IBrowserPool<TPage> browserPool, ProgressTracker progress,
            IAdCollector<TPage> adCollector, IAdClicker<TPage> adClicker)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
            _progress = progress;
            _adCollector = adCollector;
            _adClicker = adClicker;
        }

        public async Task RunAsync(string[] urls, CancellationToken cancellationToken = default)
        {
            var urlChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false
            });

            var clickChannel = Channel.CreateUnbounded<(string page, string link)>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            });

            int urlsRemaining = urls.Length;
            int activeCollectors = 0;
            int activeClickers = 0;
            var clickWriter = clickChannel.Writer;

            _ = Task.Run(async () =>
            {
                foreach (var url in urls)
                {
                    await urlChannel.Writer.WriteAsync(url, cancellationToken);
                    _progress.Update(url, ProgressStatus.Waiting, iterDelta: 1);
                }
                urlChannel.Writer.Complete();
            }, cancellationToken);

            _ = Task.Run(async () =>
            {
                await urlChannel.Reader.Completion;
                while (Volatile.Read(ref activeCollectors) > 0)
                    await Task.Delay(50, cancellationToken);
                clickWriter.TryComplete();
            }, cancellationToken);

            var workers = new Task[_settings.MaxDegreeOfParallelism];
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    var page = await _browserPool.AcquireAsync(cancellationToken);
                    _logger.Info($"PipelineWorker {workerId} started.");

                    try
                    {
                        var urlReader = urlChannel.Reader;
                        var linkReader = clickChannel.Reader;

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            bool didWork = false;

                            int remaining = Volatile.Read(ref urlsRemaining);
                            int targetCollectors = Math.Min(_settings.MaxDegreeOfParallelism, Math.Max(remaining, 0));

                            if (remaining > 0 && Volatile.Read(ref activeCollectors) < targetCollectors)
                            {
                                Interlocked.Increment(ref activeCollectors);
                                if (urlReader.TryRead(out var url))
                                {
                                    Interlocked.Decrement(ref urlsRemaining);
                                    didWork = true;
                                    try
                                    {
                                        _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);
                                        var links = await _adCollector.CollectLinksAsync(page, url, cancellationToken);
                                        var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
                                        _progress.Update(url, status, adsDelta: links.Count);

                                        if (links.Count > 0)
                                        {
                                            _progress.Update(url, pendingClicksDelta: links.Count);
                                            foreach (var link in links)
                                                await clickWriter.WriteAsync((url, link), cancellationToken);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Error($"[Collector{workerId}] Unhandled exception during collection for {url}: {ex.Message}", ex);
                                        _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                                        page = await _browserPool.RenewAsync(page, cancellationToken);
                                    }
                                    finally
                                    {
                                        _progress.Update(url, threadDelta: -1);
                                        Interlocked.Decrement(ref activeCollectors);
                                    }
                                }
                                else
                                {
                                    Interlocked.Decrement(ref activeCollectors); // No URL, so decrement immediately
                                }
                            }

                            if (!didWork)
                            {                                
                                int allowedClickers = _settings.MaxDegreeOfParallelism - targetCollectors;
                                if (allowedClickers > 0 && Volatile.Read(ref activeClickers) < allowedClickers && linkReader.TryRead(out var work))
                                {
                                    Interlocked.Increment(ref activeClickers);
                                    didWork = true;
                                    try
                                    {
                                        _progress.Update(work.page, ProgressStatus.Clicking, threadDelta: +1);
                                        page = await _adClicker.ClickAdAsync(page, work.link, cancellationToken);
                                        _progress.Update(work.page, clickDelta: 1);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Error($"[Clicker{workerId}] Final attempt failed for link '{work.link}' from page '{work.page}'", ex);
                                        _progress.Update(work.page, errDelta: 1);
                                        try
                                        {
                                            // 클릭 실패 후 브라우저/페이지 갱신으로 회복력 향상
                                            page = await _browserPool.RenewAsync(page, cancellationToken);
                                        }
                                        catch (Exception renewEx)
                                        {
                                            _logger.Warn($"[Clicker{workerId}] Failed to renew browser after click error: {renewEx.Message}");
                                        }
                                    }
                                    finally
                                    {
                                        _progress.Update(work.page, pendingClicksDelta: -1, threadDelta: -1);
                                        Interlocked.Decrement(ref activeClickers);
                                    }
                                }
                            }

                            if (!didWork)
                            {
                                var urlWaitTask = urlReader.WaitToReadAsync(cancellationToken).AsTask();
                                var linkWaitTask = linkReader.WaitToReadAsync(cancellationToken).AsTask();
                                await Task.WhenAny(urlWaitTask, linkWaitTask);

                                bool urlHasData = urlWaitTask.IsCompletedSuccessfully && urlWaitTask.Result;
                                bool linkHasData = linkWaitTask.IsCompletedSuccessfully && linkWaitTask.Result;

                                if (!urlHasData && urlReader.Completion.IsCompleted && !linkHasData)
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        _browserPool.Release(page);
                        _logger.Info($"PipelineWorker {workerId} stopped.");
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(workers);
            _logger.Info("Pipeline runner has completed.");
        }
    }
}
