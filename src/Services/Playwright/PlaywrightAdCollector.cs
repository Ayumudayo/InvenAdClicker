using Microsoft.Playwright;
using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.Services.Interfaces;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightAdCollector : IAdCollector<IPage>
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
            bool HasAny(string[]? arr) => arr != null && arr.Length > 0;
            bool ContainsAllowed(string value, string[]? allowList)
                => HasAny(allowList) && allowList!.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < _settings.CollectionAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _settings.PageLoadTimeoutMilliseconds
                });

                try
                {
                    // 광고 iframe이 DOM에 나타날 때까지 짧게 대기
                    await page.WaitForSelectorAsync("iframe", new PageWaitForSelectorOptions
                    {
                        Timeout = _settings.IframeTimeoutMilliSeconds
                    });
                }
                catch (TimeoutException)
                {
                    // iframe이 없을 수도 있으므로 정보만 남기고 계속 진행
                    _logger.Info($"No iframes detected (within {_settings.IframeTimeoutMilliSeconds}ms) on {url}.");
                }

                // DOM 스냅샷이 아닌 프레임 컬렉션을 사용해 안정적으로 순회
                var frames = page.Frames.Where(f => f.ParentFrame != null);
                foreach (var frame in frames)
                {
                    try
                    {
                        var frameUrl = frame.Url ?? string.Empty;
                        if (HasAny(_settings.FrameSrcAllowListContains) &&
                            !ContainsAllowed(frameUrl, _settings.FrameSrcAllowListContains))
                        {
                            continue; // 허용되지 않는 프레임은 스킵
                        }

                        var linksInFrame = await frame.Locator("a[href]").AllAsync();
                        foreach (var linkLocator in linksInFrame)
                        {
                            var href = await linkLocator.GetAttributeAsync("href");
                            if (!string.IsNullOrEmpty(href) &&
                                !href.Equals("#", StringComparison.OrdinalIgnoreCase) &&
                                !href.Contains("empty.gif", StringComparison.OrdinalIgnoreCase))
                            {
                                if (href.StartsWith("//")) href = "https:" + href;
                                if (!HasAny(_settings.LinkAllowListContains) ||
                                    ContainsAllowed(href, _settings.LinkAllowListContains))
                                {
                                    allLinks.Add(href);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[Collector] frame processing failed on {url}: {ex.Message}");
                    }
                }

                if (i < _settings.CollectionAttempts - 1)
                {
                    await page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _settings.PageLoadTimeoutMilliseconds
                    });
                }
            }
            return allLinks.ToList();
        }
    }
}