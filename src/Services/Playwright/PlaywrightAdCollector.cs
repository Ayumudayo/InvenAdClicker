
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
using System.Threading.Tasks;

public class PlaywrightAdCollector : IAdCollector
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly PlaywrightWebBrowser _browser;
    private readonly ProgressTracker _progress;

    public PlaywrightAdCollector(AppSettings settings, ILogger logger, PlaywrightWebBrowser browser, ProgressTracker progress)
    {
        _settings = settings;
        _logger = logger;
        _browser = browser;
        _progress = progress;
    }

    public async Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
        string[] urls, CancellationToken cancellationToken = default)
    {
        var result = new ConcurrentDictionary<string, IEnumerable<string>>();
        _logger.Info("Collecting ads with Playwright...");

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);
            List<string> links = new List<string>();
            try
            {
                links = await CollectWithBrowserAsync(url, cancellationToken);
                result[url] = links;
                var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
                _progress.Update(url, status, adsDelta: links.Count);
                _logger.Info($"Collected {links.Count} ad links from {url}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to collect ads from {url}: {ex.Message}");
                result[url] = links; // Ensure an entry exists even on failure
                _progress.Update(url, ProgressStatus.Error, errDelta: 1);
            }
            finally
            {
                _progress.Update(url, threadDelta: -1);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private async Task<List<string>> CollectWithBrowserAsync(string url, CancellationToken cancellationToken)
    {
        var page = _browser.Page;
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
                            if (!string.IsNullOrEmpty(href) && !href.Equals("#", StringComparison.OrdinalIgnoreCase))
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
