using Microsoft.Playwright;
using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public class PlaywrightAdClicker : IAdClicker
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly PlaywrightBrowserPool _browserPool;
    private readonly ProgressTracker _progress;

    public PlaywrightAdClicker(AppSettings settings, ILogger logger, PlaywrightBrowserPool browserPool, ProgressTracker progress)
    {
        _settings = settings;
        _logger = logger;
        _browserPool = browserPool;
        _progress = progress;
    }

    public async Task ClickAsync(
        Dictionary<string, IEnumerable<string>> pageToLinks,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<(string page, string link)>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

        _ = Task.Run(async () =>
        {
            foreach (var (page, links) in pageToLinks)
            {
                _progress.Update(page, pendingClicksDelta: links.Count());
                foreach (var link in links)
                    await channel.Writer.WriteAsync((page, link), cancellationToken);
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
                _logger.Info($"ClickerWorker {workerId} started.");

                try
                {
                    await foreach (var (pageUrl, link) in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        _progress.Update(pageUrl, ProgressStatus.Clicking, threadDelta: +1);
                        bool success = false;
                        try
                        {
                            await ClickWithBrowserAsync(page, link, cancellationToken);
                            success = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"[{workerId}] Attempt failed for link '{link}' from page '{pageUrl}': {ex.Message}");
                            page = await _browserPool.RenewAsync(page);
                        }

                        if (success)
                        {
                            _progress.Update(pageUrl, clickDelta: 1);
                        }
                        else
                        {
                            _logger.Error($"[{workerId}] Final attempt failed for link '{link}' from page '{pageUrl}'");
                            _progress.Update(pageUrl, errDelta: 1);
                        }

                        _progress.Update(pageUrl, pendingClicksDelta: -1, threadDelta: -1);
                    }
                }
                finally
                {
                    _browserPool.Release(page);
                    _logger.Info($"ClickerWorker {workerId} stopped.");
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
        _logger.Info("All ad clicking done.");
    }

    public async Task ClickWithBrowserAsync(IPage page, string link, CancellationToken cancellationToken)
    {
        try
        {
            await page.GotoAsync(link, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = _settings.PageLoadTimeoutMilliseconds });
            await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error($"Playwright error during click of '{link}': {ex.Message}");
            throw;
        }
    }
}