using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            var initialTasks = new Task[_settings.MaxDegreeOfParallelism];
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                initialTasks[i] = CreateAndPoolPageAsync(cancellationToken);
            }
            await Task.WhenAll(initialTasks);
            _logger.Info($"Playwright 브라우저 풀 초기화 완료({_pool.Count}개 인스턴스 준비됨)");
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<IPage> CreatePageAsync(CancellationToken cancellationToken)
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders
            });
            var page = await context.NewPageAsync();

            // 불필요한 리소스를 차단(네트워크 비용 절감)
            await page.RouteAsync("**/*", async route =>
            {
                var resourceType = route.Request.ResourceType;
                bool shouldContinue = false;

                // 핵심 리소스: 항상 허용
                if (resourceType == "document" || resourceType == "script" || resourceType == "xhr" || resourceType == "fetch")
                {
                    shouldContinue = true;
                }
                // 설정 기반으로 이미지/CSS/폰트 허용 여부 제어
                else if (resourceType == "image" && !_settings.DisableImages)
                {
                    shouldContinue = true;
                }
                else if (resourceType == "stylesheet" && !_settings.DisableCss)
                {
                    shouldContinue = true;
                }
                else if (resourceType == "font" && !_settings.DisableFonts)
                {
                    shouldContinue = true;
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

            await PlaywrightLoginWorkflow.LoginAsync(page, _settings, _encryption, cancellationToken);
            _logger.Info("로그인 성공");
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

            _logger.Warn("Playwright 풀에 여유 인스턴스가 없어 새 페이지를 생성합니다.");
            return await CreatePageAsync(cancellationToken);
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
                _semaphore.Release();
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
            // 풀 상태를 초기화해 누수 방지
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
                await oldPage.Context.CloseAsync();
            }

            try
            {
                return await CreatePageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error("새 페이지 생성 실패", ex);
                _semaphore.Release();
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
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
