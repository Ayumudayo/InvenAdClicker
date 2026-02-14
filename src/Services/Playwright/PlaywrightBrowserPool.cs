using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightBrowserPool : IBrowserPool<IPage>
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly Encryption _encryption;
        private readonly IBrowser _browser;
        private readonly ConcurrentBag<IPage> _pool;
        private readonly SemaphoreSlim _semaphore;
        private string? _storageStatePath;

        public PlaywrightBrowserPool(IBrowser browser, AppSettings settings, IAppLogger logger, Encryption encryption)
        {
            _browser = browser;
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
            _pool = new ConcurrentBag<IPage>();
            _semaphore = new SemaphoreSlim(settings.MaxDegreeOfParallelism, settings.MaxDegreeOfParallelism);
        }

        public async Task InitializePoolAsync(CancellationToken cancellationToken = default)
        {
            // 1. Perform initial login and capture storage state
            _logger.Info("초기 로그인 및 세션 캡처 시작...");
            var loginContext = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                JavaScriptEnabled = true
            });
            var loginPage = await loginContext.NewPageAsync();
            try
            {
                await PlaywrightLoginWorkflow.LoginAsync(loginPage, _settings, _encryption, cancellationToken);
                
                // Save state to a temp file
                _storageStatePath = Path.Combine(Path.GetTempPath(), $"inven_auth_{Guid.NewGuid():N}.json");
                await loginContext.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = _storageStatePath
                });
                _logger.Info($"로그인 세션 캡처 완료: {_storageStatePath}");
            }
            catch (Exception ex)
            {
                _logger.Fatal("초기 로그인 실패. 프로그램을 종료합니다.", ex);
                throw;
            }
            finally
            {
                await loginContext.CloseAsync();
            }

            // 2. Initialize pool with authenticated contexts
            var initialTasks = new Task[_settings.MaxDegreeOfParallelism];
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                initialTasks[i] = CreateAndPoolPageAsync(cancellationToken);
            }
            await Task.WhenAll(initialTasks);
            _logger.Info($"Playwright 브라우저 풀 초기화 완료({_pool.Count}개 인스턴스, 인증 세션 적용됨)");
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<IPage> CreatePageAsync(CancellationToken cancellationToken)
        {
            var contextOptions = new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                JavaScriptEnabled = _settings.Debug.Enabled ? _settings.Debug.JavaScriptEnabled : true,
                StorageStatePath = _storageStatePath // Inject authenticated session
            };
            var context = await _browser.NewContextAsync(contextOptions);
            var page = await context.NewPageAsync();

            // Setup Network Routes (Blockers)
            await page.RouteAsync("**/*", async route =>
            {
                var resourceType = route.Request.ResourceType;
                bool shouldContinue = false;

                if (resourceType == "document" || resourceType == "script" || resourceType == "xhr" || resourceType == "fetch")
                {
                    shouldContinue = true;
                }
                else if (_settings.Debug.Enabled)
                {
                    shouldContinue = resourceType switch
                    {
                        "image" => _settings.Debug.AllowImages,
                        "stylesheet" => _settings.Debug.AllowStylesheets,
                        "font" => _settings.Debug.AllowFonts,
                        _ => false
                    };
                }

                if (shouldContinue)
                {
                    await route.ContinueAsync();
                }
                else
                {
                    await route.AbortAsync();
                }
            });

            // Setup Console Listener (One-time subscription per page)
            page.Console += (_, msg) =>
            {
                try
                {
                    var text = msg.Text ?? string.Empty;
                    if (text.Contains(Constants.InvenAdClickerLogPrefix))
                    {
                        _logger.Info($"{Constants.PlaywrightConsoleLogPrefix} {text}");
                    }
                }
                catch { }
            };

            // Setup Init Scripts (One-time injection per context)
            // zicfm topslideAd hook
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
          var hostOk = (function(){ try{ return new URL(e.origin).hostname === 'zicf.inven.co.kr'; }catch(_){ return false; } })();
          if(e && hostOk && e.data && e.data.clickurl){
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

            // Verify login (fast check)
            // Since we injected state, we just need to double check or refresh if needed.
            // For now, assume state is valid. If we implement token refresh logic, it goes here.
            
            return page;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task CreateAndPoolPageAsync(CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var page = await CreatePageAsync(cancellationToken);
                _pool.Add(page);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<IPage> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            if (_pool.TryTake(out var page))
            {
                return page;
            }

            // Should not happen if pool is maintained properly, but if so, create new
            _logger.Warn("Playwright 풀에 여유 인스턴스가 없어 새 페이지를 생성합니다.");
            try 
            {
                return await CreatePageAsync(cancellationToken);
            }
            catch
            {
                _semaphore.Release(); // Creation failed, give back slot
                throw;
            }
        }

        public void Release(IPage page)
        {
            if (page != null && !page.IsClosed)
            {
                _pool.Add(page);
                _semaphore.Release();
            }
            else
            {
                _logger.Warn("null 또는 종료된 페이지를 반환했습니다. 새로 생성합니다.");
                _semaphore.Release(); // Just release the slot, don't auto-recreate here (Runner will handle)
            }
        }

        public async Task PreloadAsync(int count, CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < count; i++)
            {
                await CreateAndPoolPageAsync(cancellationToken);
            }
        }

        public async Task MaintainPoolAsync(int desiredSize, CancellationToken cancellationToken = default)
        {
            var deficit = desiredSize - _pool.Count;
            if (deficit <= 0)
            {
                return;
            }

            var tasks = new List<Task>();
            for (int i = 0; i < deficit; i++)
            {
                tasks.Add(CreateAndPoolPageAsync(cancellationToken));
            }

            await Task.WhenAll(tasks);
            _logger.Info("풀 보충 완료");
        }

        public void HandleBrowserCrash(Exception ex)
        {
            _logger.Error("브라우저 풀에서 예기치 못한 오류", ex);
            while (_pool.TryTake(out var page))
            {
                try { page.Context.CloseAsync().GetAwaiter().GetResult(); }
                catch { /* 무시 */ }
            }
        }

        public async Task<IPage> RenewAsync(IPage oldPage, CancellationToken cancellationToken = default)
        {
            _logger.Info("Playwright 페이지 갱신");
            if (oldPage != null)
            {
                try { await oldPage.Context.CloseAsync(); } catch { }
            }

            // Important: Do NOT call _semaphore.Release(). 
            // We are holding a slot (from AcquireAsync) and replacing the resource within that slot.
            try
            {
                return await CreatePageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error("새 페이지 생성 실패", ex);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            while (_pool.TryTake(out var page))
            {
                try { await page.Context.CloseAsync(); }
                catch { /* 무시 */ }
            }

            await _browser.CloseAsync();
            
            // Clean up temp file
            if (!string.IsNullOrEmpty(_storageStatePath) && File.Exists(_storageStatePath))
            {
                try { File.Delete(_storageStatePath); } catch { }
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
