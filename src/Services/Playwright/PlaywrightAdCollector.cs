using Microsoft.Playwright;
using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public class PlaywrightAdCollector : IAdCollector
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly PlaywrightBrowserPool _browserPool;
    private readonly ProgressTracker _progress;

    public PlaywrightAdCollector(AppSettings settings, ILogger logger, PlaywrightBrowserPool browserPool, ProgressTracker progress)
    {
        _settings = settings;
        _logger = logger;
        _browserPool = browserPool;
        _progress = progress;
    }

    public async Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
        string[] urls, CancellationToken cancellationToken = default)
    {
        var result = new ConcurrentDictionary<string, IEnumerable<string>>();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

        _ = Task.Run(async () =>
        {
            foreach (var url in urls)
            {
                await channel.Writer.WriteAsync(url, cancellationToken);
                _progress.Update(url, ProgressStatus.Waiting, iterDelta: 1);
            }
            channel.Writer.Complete();
        }, cancellationToken);

        var workers = new Task[_settings.MaxDegreeOfParallelism];
        for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
        {
            int workerId = i;
            workers[i] = Task.Run(async () =>
            {
                var page = await _browserPool.AcquireAsync(cancellationToken);
                _logger.Info($"CollectorWorker {workerId} started with page.");

                try
                {
                    await foreach (var url in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);
                        List<string> links = null;

                        try
                        {
                            links = await CollectWithBrowserAsync(page, url, cancellationToken);
                            result[url] = links;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"[Collector{workerId}] Failed to process {url}: {ex.Message}", ex);
                            _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                            page = await _browserPool.RenewAsync(page);
                            continue;
                        }
                        finally
                        {
                            _progress.Update(url, threadDelta: -1);
                        }

                        var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
                        _progress.Update(url, status, adsDelta: links.Count);
                        _logger.Info($"[Collector{workerId}] {url} => {links.Count} links");
                    }
                }
                finally
                {
                    _browserPool.Release(page);
                    _logger.Info($"CollectorWorker {workerId} stopped.");
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private async Task<List<string>> CollectWithBrowserAsync(IPage page, string url, CancellationToken cancellationToken)
    {
        var allLinks = new HashSet<string>();

        for (int i = 0; i < _settings.CollectionAttempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = _settings.PageLoadTimeoutMilliseconds });

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
                                if (href.StartsWith("//"))
                                {
                                    href = "https:" + href;
                                }
                                allLinks.Add(href);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[Collector] iframe fail on {url}: {ex.Message}");
                }
                finally
                {
                    await iframeHandle.DisposeAsync();
                }
            }

            if (i < _settings.CollectionAttempts - 1)
            {
                await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load, Timeout = _settings.PageLoadTimeoutMilliseconds });
            }
        }

        return allLinks.ToList();
    }
}