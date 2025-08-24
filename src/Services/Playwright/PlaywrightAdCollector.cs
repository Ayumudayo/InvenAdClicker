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
            const string ActualAdLinkPrefix = "https://zicf.inven.co.kr/RealMedia";
            var allLinks = new HashSet<string>();
            bool IsAllowed(string value) => Utils.AdAllowList.IsAllowed(value);
            // 페이지 콘솔 로그 중 우리 태그만 브리징(노이즈 억제)
            try
            {
                page.Console += (_, msg) =>
                {
                    try
                    {
                        var text = msg.Text ?? string.Empty;
                        if (text.Contains("[InvenAdClicker]"))
                        {
                            _logger.Info($"[PW Console] {text}");
                        }
                    }
                    catch { }
                };
            }
            catch { }
            // zicfm topslideAd 대응: postMessage/함수 래핑으로 clickurl 수집 훅 주입
            try
            {
                await page.AddInitScriptAsync(@"(function(){
  try{
    if(window.top !== window){ return; }
    window.__ad_links = window.__ad_links || [];
    window.__ad_logs = window.__ad_logs || [];
    if(!window.__ad_hooked){
      window.__ad_hooked = true;
      try{ window.__ad_logs.push('hook installed'); }catch(_){ }
      window.addEventListener('message', function(e){
        try{
          if(e && e.origin === 'https://zicf.inven.co.kr' && e.data && e.data.clickurl){
            var u = e.data.clickurl; if(u && typeof u === 'string'){ window.__ad_links.push(u); try{ window.__ad_logs.push('pm:'+u); }catch(_){ } }
          }
        }catch(_){ }
      }, false);
      var _sts = window.showTopSlide;
      if(typeof _sts === 'function'){
        window.showTopSlide = function(img, clickurl){ try{ if(clickurl && typeof clickurl === 'string'){ window.__ad_links.push(clickurl); try{ window.__ad_logs.push('wrap:showTopSlide:'+clickurl); }catch(_){ } } }catch(_){ } return _sts.apply(this, arguments); };
      }
      var _stsh = window.showTopSlideHome;
      if(typeof _stsh === 'function'){
        window.showTopSlideHome = function(img, clickurl){ try{ if(clickurl && typeof clickurl === 'string'){ window.__ad_links.push(clickurl); try{ window.__ad_logs.push('wrap:showTopSlideHome:'+clickurl); }catch(_){ } } }catch(_){ } return _stsh.apply(this, arguments); };
      }
    }
  }catch(_){ }
})();");
                _logger.Info("[수집기] 초기 훅 스크립트 주입 예약 완료");
            }
            catch (Exception ex)
            {
                _logger.Warn($"[수집기] 초기 스크립트 주입 실패: {ex.Message}");
            }
            for (int i = 0; i < _settings.CollectionAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _settings.PageLoadTimeoutMilliseconds
                });
                _logger.Info($"[수집기] 탐색 시도 {i+1}/{_settings.CollectionAttempts}: {url}");

                try
                {
                    // 광고 iframe이 DOM에 나타날 때까지 짧게 대기
                    await page.WaitForSelectorAsync("iframe", new PageWaitForSelectorOptions
                    {
                        Timeout = _settings.IframeTimeoutMilliSeconds
                    });
                    // postMessage가 도달할 약간의 여유
                    await page.WaitForTimeoutAsync(200);
                }
                catch (TimeoutException)
                {
                    // iframe이 없을 수도 있으므로 과도한 소음을 줄이기 위해 Debug로만 기록
                    _logger.Debug($"{url}에서 {_settings.IframeTimeoutMilliSeconds}ms 내에 iframe 미검출");
                }

                // DOM 스냅샷이 아닌 프레임 컬렉션을 사용해 안정적으로 순회
                var frames = page.Frames.Where(f => f.ParentFrame != null);
                try { _logger.Info($"[수집기] 프레임 수: {frames.Count()} (전체: {page.Frames.Count})"); } catch { }
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

                // postMessage/래핑으로 수집된 클릭 URL 병합
                try
                {
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
                            _logger.Debug($"[수집기] postMessage/래핑 수집 링크: {href}");
                            if (IsAllowed(href)) allLinks.Add(href);
                        }
                    }
                    _logger.Info($"[수집기] 누적 링크 수(필터 전): {allLinks.Count}");
                }
                catch (Exception ex)
                {
                    _logger.Debug($"postMessage 수집 링크 병합 실패: {ex.Message}");
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
            
            // 필터링
            var result = allLinks.Where(l => !string.IsNullOrEmpty(l) && l.StartsWith(ActualAdLinkPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            _logger.Info($"[수집기] 최종 링크: {result.Count} / 전체 {allLinks.Count}");
            if (result.Count == 0)
            {
                _logger.Warn("[수집기] RealMedia 링크 미검출.");
            }
            return result;
        }
    }
}
