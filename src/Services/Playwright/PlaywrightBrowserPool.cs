
using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightBrowserPool : IAsyncDisposable
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly Encryption _encryption;
        private readonly IBrowser _browser;
        private readonly ConcurrentBag<IPage> _pool;
        private readonly SemaphoreSlim _semaphore;

        public PlaywrightBrowserPool(IBrowser browser, AppSettings settings, ILogger logger, Encryption encryption)
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
            _logger.Info($"Playwright Browser Pool initialized with {_pool.Count} browser pages.");
        }

        private async Task<IPage> CreatePageAsync(CancellationToken cancellationToken)
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = new[] { new KeyValuePair<string, string>("Accept-Language", "en-US,en;q=0.9") }
            });
            var page = await context.NewPageAsync();

            // Block unnecessary resources using a more aggressive allowlist approach
            await page.RouteAsync("**/*", async route =>
            {
                var resourceType = route.Request.ResourceType;
                bool shouldContinue = false;

                // Core resources that are always allowed
                if (resourceType == "document" || resourceType == "script" || resourceType == "xhr" || resourceType == "fetch")
                {
                    shouldContinue = true;
                }
                // Conditionally allow other resources based on settings
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

            await LoginAsync(page);
            return page;
        }

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

            // Pool was empty, create a new page, but this path shouldn't be hit if initialized properly.
            _logger.Warn("Playwright pool was empty. Creating a new page on-demand.");
            return await CreatePageAsync(cancellationToken);
        }

        public void Release(IPage page)
        {
            if (page != null && !page.IsClosed)
            {
                _pool.Add(page);
            }
            else
            {
                // Page is broken or closed, don't return it to the pool.
                // Just release the semaphore so a new one can be created.
                _logger.Warn("Released a null or closed page. A new page will be created if needed.");
            }
            _semaphore.Release();
        }

        public async Task<IPage> RenewAsync(IPage oldPage)
        {
            _logger.Info("Renewing a Playwright page.");
            if (oldPage != null)
            {
                await oldPage.Context.CloseAsync();
            }
            
            // Semaphore is not released here because AcquireAsync will be called right after,
            // and it will wait on the semaphore.
            return await CreatePageAsync(CancellationToken.None);
        }

        private async Task LoginAsync(IPage page)
        {
            _encryption.LoadAndValidateCredentials(out var id, out var pw);
            await page.GotoAsync("https://member.inven.co.kr/user/scorpio/mlogin", new PageGotoOptions { Timeout = _settings.PageLoadTimeoutMilliseconds });
            await page.FillAsync("#user_id", id, new PageFillOptions { Timeout = (float)_settings.CommandTimeoutMilliSeconds });
            await page.FillAsync("#password", pw, new PageFillOptions { Timeout = (float)_settings.CommandTimeoutMilliSeconds });
            await page.ClickAsync("#loginBtn", new PageClickOptions { Timeout = (float)_settings.CommandTimeoutMilliSeconds });

            try
            {
                await page.WaitForFunctionAsync(@"() => {
                    const notice = document.querySelector('#notice');
                    return window.location.href !== 'https://member.inven.co.kr/user/scorpio/mlogin' || (notice && notice.textContent.includes('로그인 정보가 일치하지 않습니다.'));
                }", new PageWaitForFunctionOptions { Timeout = (float)_settings.PageLoadTimeoutMilliseconds });

                if (page.Url.Contains("member.inven.co.kr"))
                {
                     throw new ApplicationException("Login failed. Please check your credentials.");
                }
            }
            catch (TimeoutException)
            {
                // This can happen on successful login if the redirect is very fast.
                // We verify by checking the URL.
                if (page.Url.Contains("member.inven.co.kr"))
                {
                    throw new ApplicationException("Login failed due to a timeout after submitting credentials.");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _browser.CloseAsync();
        }
    }
}
