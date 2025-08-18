using Microsoft.Playwright;
using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightAdCollector
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;

        public PlaywrightAdCollector(AppSettings settings, ILogger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public async Task<List<string>> CollectLinksAsync(IPage page, string url, CancellationToken cancellationToken)
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
                        _logger.Warn($"[Collector] iframe processing failed on {url}: {ex.Message}");
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
            return allLinks.ToList();
        }
    }
}
