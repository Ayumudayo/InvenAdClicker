using Microsoft.Playwright;
using InvenAdClicker.Models;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    // 남는 워커가 클릭 작업으로 전환되는 수집/클릭 파이프라인 실행기
    public class PlaywrightPipelineRunner
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly PlaywrightBrowserPool _browserPool;
        private readonly ProgressTracker _progress;

        public PlaywrightPipelineRunner(AppSettings settings, ILogger logger,
            PlaywrightBrowserPool browserPool, ProgressTracker progress)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
            _progress = progress;
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

            int urlsRemaining = urls.Length; // 아직 착수하지 않은 URL 수
            int activeCollectors = 0;        // 현재 수집 중 워커 수
            int activeClickers = 0;          // 현재 클릭 중 워커 수
            var clickWriter = clickChannel.Writer;

            // URL 공급
            _ = Task.Run(async () =>
            {
                foreach (var url in urls)
                {
                    await urlChannel.Writer.WriteAsync(url, cancellationToken);
                    _progress.Update(url, ProgressStatus.Waiting, iterDelta: 1);
                }
                urlChannel.Writer.Complete();
            }, cancellationToken);

            // 클릭 채널 완료 감시자
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

                            // 목표 수집 워커 수: 남은 URL 수와 워커 수의 최소값
                            int remaining = Volatile.Read(ref urlsRemaining);
                            int targetCollectors = Math.Min(_settings.MaxDegreeOfParallelism, Math.Max(remaining, 0));

                            // 1) 수집 슬롯이 남아있고 URL이 남아있으면 수집
                            if (remaining > 0 && Volatile.Read(ref activeCollectors) < targetCollectors)
                            {
                                Interlocked.Increment(ref activeCollectors);
                                try
                                {
                                    if (urlReader.TryRead(out var url))
                                    {
                                        Interlocked.Decrement(ref urlsRemaining);
                                        await CollectOneAsync(page, url, clickWriter, cancellationToken);
                                        didWork = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Error($"[Collector{workerId}] 수집 처리 중 예외: {ex.Message}", ex);
                                    page = await _browserPool.RenewAsync(page);
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref activeCollectors);
                                }
                            }

                            if (!didWork)
                            {
                                // 2) 클릭 작업 처리 시도 (남는 워커 수 만큼만 허용)
                                int allowedClickers = _settings.MaxDegreeOfParallelism - targetCollectors; // 남는 워커 수
                                if (allowedClickers > 0 && Volatile.Read(ref activeClickers) < allowedClickers && linkReader.TryRead(out var work))
                                {
                                    Interlocked.Increment(ref activeClickers);
                                    try
                                    {
                                        await ClickOneAsync(page, work.page, work.link, cancellationToken);
                                        didWork = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Warn($"[Clicker{workerId}] 클릭 중 예외: {ex.Message}");
                                        page = await _browserPool.RenewAsync(page);
                                    }
                                    finally
                                    {
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
            _logger.Info("파이프라인 작업이 완료되었습니다.");
        }

        private async Task CollectOneAsync(
            IPage page,
            string url,
            ChannelWriter<(string page, string link)> clickWriter,
            CancellationToken cancellationToken)
        {
            _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);
            List<string> links = new();
            try
            {
                var allLinks = new HashSet<string>();
                for (int i = 0; i < _settings.CollectionAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = _settings.PageLoadTimeoutMilliseconds
                    });

                    var iframeHandles = await page.QuerySelectorAllAsync("iframe");
                    foreach (var iframeHandle in iframeHandles)
                    {
                        try
                        {
                            var frame = await iframeHandle.ContentFrameAsync();
                            if (frame != null)
                            {
                                var linksInFrame = await frame.Locator("a").AllAsync();
                                foreach (var linkLocator in linksInFrame)
                                {
                                    var href = await linkLocator.GetAttributeAsync("href");
                                    if (!string.IsNullOrEmpty(href) &&
                                        !href.Equals("#", StringComparison.OrdinalIgnoreCase) &&
                                        !href.Contains("empty.gif", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (href.StartsWith("//")) href = "https:" + href;
                                        allLinks.Add(href);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"[Collector] iframe 처리 실패 {url}: {ex.Message}");
                        }
                        finally
                        {
                            await iframeHandle.DisposeAsync();
                        }
                    }

                    if (i < _settings.CollectionAttempts - 1)
                    {
                        await page.ReloadAsync(new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.Load,
                            Timeout = _settings.PageLoadTimeoutMilliseconds
                        });
                    }
                }

                links = allLinks.ToList();
            }
            catch (Exception ex)
            {
                _logger.Error($"[Collector] 수집 중 예외: {url}: {ex.Message}");
                _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                throw;
            }
            finally
            {
                _progress.Update(url, threadDelta: -1);
            }

            var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
            _progress.Update(url, status, adsDelta: links.Count);

            if (links.Count > 0)
            {
                _progress.Update(url, pendingClicksDelta: links.Count);
                foreach (var link in links)
                    await clickWriter.WriteAsync((url, link), cancellationToken);
            }
        }

        private async Task ClickOneAsync(
            IPage page,
            string pageUrl,
            string link,
            CancellationToken cancellationToken)
        {
            _progress.Update(pageUrl, ProgressStatus.Clicking, threadDelta: +1);
            try
            {
                await page.GotoAsync(link, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Load,
                    Timeout = _settings.PageLoadTimeoutMilliseconds
                });
                await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
                _progress.Update(pageUrl, clickDelta: 1);
            }
            catch (Exception ex)
            {
                _logger.Error($"Playwright 클릭 오류 '{link}': {ex.Message}");
                _progress.Update(pageUrl, errDelta: 1);
                throw;
            }
            finally
            {
                _progress.Update(pageUrl, pendingClicksDelta: -1, threadDelta: -1);
            }
        }
    }
}
