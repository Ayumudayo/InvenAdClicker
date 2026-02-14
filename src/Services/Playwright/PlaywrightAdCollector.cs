using Microsoft.Playwright;
using InvenAdClicker.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;

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

            var swTotal = Stopwatch.StartNew();

            // Note: Console events and InitScripts are now handled in PlaywrightBrowserPool

            for (int i = 0; i < _settings.CollectionAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PlaywrightPageTelemetry.ResetRouteStats(page);
                var attemptStartLinks = allLinks.Count;
                var swAttempt = Stopwatch.StartNew();
                long gotoMs = 0;
                long iframeWaitMs = 0;
                long bufferMs = 0;
                long extractMs = 0;
                long extrasMs = 0;
                long reloadMs = 0;
                bool iframeTimedOut = false;

                var sw = Stopwatch.StartNew();
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _settings.PageLoadTimeoutMilliseconds
                });
                gotoMs = sw.ElapsedMilliseconds;
                _logger.Info($"[수집기] 탐색 시도 {i+1}/{_settings.CollectionAttempts}: {url}");

                try
                {
                    sw.Restart();
                    await page.WaitForSelectorAsync("iframe", new PageWaitForSelectorOptions
                    {
                        Timeout = _settings.IframeTimeoutMilliSeconds,
                        State = WaitForSelectorState.Attached
                    });
                    iframeWaitMs = sw.ElapsedMilliseconds;

                    // Buffer for postMessage/dynamic ads
                    sw.Restart();
                    await page.WaitForTimeoutAsync(_settings.PostMessageBufferMilliseconds);
                    bufferMs = sw.ElapsedMilliseconds;
                }
                catch (TimeoutException)
                {
                    iframeTimedOut = true;
                    iframeWaitMs = sw.ElapsedMilliseconds;
                    _logger.Debug($"{url}에서 {_settings.IframeTimeoutMilliSeconds}ms 내에 iframe 미검출");
                }

                // Optimization: Collect all links from all frames in parallel using JS evaluation where possible
                // Playwright handles cross-frame evaluation automagically if we iterate frames
                sw.Restart();
                var frames = page.Frames.Where(f => f.ParentFrame != null).ToList();
                try { _logger.Info($"[수집기] 프레임 수: {frames.Count()} (전체: {page.Frames.Count})"); } catch { }

                foreach (var frame in frames)
                {
                    try
                    {
                        var frameUrl = frame.Url ?? string.Empty;
                        if (!IsAllowed(frameUrl)) continue;

                        // Bulk extraction using EvaluateAsync (Much faster than Locator.AllAsync loop)
                        var hrefs = await frame.EvaluateAsync<string[]>(@"
                            () => Array.from(document.querySelectorAll('a[href]'))
                                .map(a => a.getAttribute('href'))
                                .filter(h => h && h !== '#' && !h.includes('empty.gif'))
                        ");

                        foreach (var href in hrefs)
                        {
                            var normalized = href;
                            if (normalized.StartsWith("//")) normalized = "https:" + normalized;
                            if (IsAllowed(normalized))
                                allLinks.Add(normalized);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Some frames might be cross-origin restricted or detached
                        _logger.Debug($"[수집기] 프레임 링크 추출 실패: {ex.Message}");
                    }
                }
                extractMs = sw.ElapsedMilliseconds;

                // Merge postMessage/hook collected links
                try
                {
                    sw.Restart();
                    var extras = await page.EvaluateAsync<string[]>("(function(){try{return (window.__ad_links||[]).slice();}catch(_){return [];}})()");
                    var logs = await page.EvaluateAsync<string[]>("(function(){try{var a=(window.__ad_logs||[]).slice(); window.__ad_logs=[]; return a;}catch(_){return [];}})()");
                    if (logs != null && logs.Length > 0)
                    {
                        foreach (var line in logs)
                        {
                            _logger.Info($"[ad-hook] {line}");
                        }
                    }
                    _logger.Info($"[수집기] postMessage/래핑 수집 링크 수: {extras?.Length ?? 0}");
                    foreach (var extra in extras ?? Array.Empty<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(extra))
                        {
                            var href = extra.StartsWith("//") ? ("https:" + extra) : extra;
                            if (IsAllowed(href)) allLinks.Add(href);
                        }
                    }
                    _logger.Info($"[수집기] 누적 링크 수(필터 전): {allLinks.Count}");
                    extrasMs = sw.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"postMessage 수집 링크 병합 실패: {ex.Message}");
                }

                if (i < _settings.CollectionAttempts - 1)
                {
                    sw.Restart();
                    await page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _settings.PageLoadTimeoutMilliseconds
                    });
                    reloadMs = sw.ElapsedMilliseconds;
                }

                swAttempt.Stop();
                var routeStats = PlaywrightPageTelemetry.SnapshotRouteStats(page);
                var added = Math.Max(0, allLinks.Count - attemptStartLinks);
                _logger.Info(
                    $"[perf][collect] url='{url}' attempt={i + 1}/{_settings.CollectionAttempts} " +
                    $"goto={gotoMs}ms iframeWait={iframeWaitMs}ms{(iframeTimedOut ? "(timeout)" : string.Empty)} " +
                    $"buffer={bufferMs}ms extract={extractMs}ms extras={extrasMs}ms reload={reloadMs}ms " +
                    $"links+={added} linksTotal={allLinks.Count} route={routeStats.FormatCompact()} total={swAttempt.ElapsedMilliseconds}ms");
            }

            swTotal.Stop();
             
            // Filtering
            var result = allLinks.Where(l => !string.IsNullOrEmpty(l) && l.StartsWith(Constants.ActualAdLinkPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            _logger.Info($"[수집기] 최종 링크: {result.Count} / 전체 {allLinks.Count}");
            if (result.Count == 0)
            {
                _logger.Warn("[수집기] RealMedia 링크 미검출.");
            }

            _logger.Info($"[perf][collect] url='{url}' done attempts={_settings.CollectionAttempts} total={swTotal.ElapsedMilliseconds}ms links={result.Count}/{allLinks.Count}");
            return result;
        }
    }
}
