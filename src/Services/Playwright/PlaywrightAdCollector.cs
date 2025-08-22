using Microsoft.Playwright;
using InvenAdClicker.Models;
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
        private readonly IAppLogger _logger;

        public PlaywrightAdCollector(AppSettings settings, IAppLogger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public async Task<List<string>> CollectLinksAsync(IPage page, string url, CancellationToken cancellationToken)
        {
            var allLinks = new HashSet<string>();
            bool IsAllowed(string value) => Utils.AdAllowList.IsAllowed(value);
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
                    // iframe이 없을 수도 있으므로 과도한 소음을 줄이기 위해 Debug로만 기록
                    _logger.Debug($"{url}에서 {_settings.IframeTimeoutMilliSeconds}ms 내에 iframe 미검출");
                }

                // DOM 스냅샷이 아닌 프레임 컬렉션을 사용해 안정적으로 순회
                var frames = page.Frames.Where(f => f.ParentFrame != null);
                foreach (var frame in frames)
                {
                    try
                    {
                        var frameUrl = frame.Url ?? string.Empty;
                        if (!IsAllowed(frameUrl)) continue; // 허용되지 않는 프레임은 스킵

                        var linksInFrame = await frame.Locator("a[href]").AllAsync();
                        foreach (var linkLocator in linksInFrame)
                        {
                            var href = await linkLocator.GetAttributeAsync("href");
                            if (!string.IsNullOrEmpty(href) &&
                                !href.Equals("#", StringComparison.OrdinalIgnoreCase) &&
                                !href.Contains("empty.gif", StringComparison.OrdinalIgnoreCase))
                            {
                                if (href.StartsWith("//")) href = "https:" + href;
                                if (IsAllowed(href))
                                    allLinks.Add(href);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[수집기] {url} 프레임 처리 실패: {ex.Message}");
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
