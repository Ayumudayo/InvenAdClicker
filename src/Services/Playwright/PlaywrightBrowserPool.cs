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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<IPage> CreatePageAsync(CancellationToken cancellationToken)
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = new[] { new KeyValuePair<string, string>("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8") }
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

            // Pool was empty, create a new page, but this path shouldn't be hit if initialized properly.
            _logger.Warn("Playwright pool was empty. Creating a new page on-demand.");
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
                _logger.Warn("Released a null or closed page. Attempting to create a replacement.");
                // The semaphore is not released here. Instead, we try to create a new page.
                // The semaphore acts as a gate for creating new pages.
                Task.Run(async () =>
                {
                    try
                    {
                        var newPage = await CreatePageAsync(CancellationToken.None);
                        _pool.Add(newPage);
                        _semaphore.Release();
                        _logger.Info("Successfully created a replacement page for the pool.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to create a replacement page.", ex);
                        // If creation fails, we must release the semaphore to not deadlock the pool.
                        _semaphore.Release();
                    }
                });
            }
        }

        public async Task<IPage> RenewAsync(IPage oldPage, CancellationToken cancellationToken = default)
        {
            _logger.Info("Renewing a Playwright page.");
            if (oldPage != null)
            {
                // Close the context of the old page, which also closes the page itself.
                await oldPage.Context.CloseAsync();
            }

            // The semaphore slot from the old page is now free.
            // We create a new page to replace it, maintaining the pool size.
            // No need to wait on the semaphore here as the caller already acquired a slot.
            try
            {
                return await CreatePageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to create a new page during renewal.", ex);
                // If we fail, we must release the semaphore because the caller is expecting
                // to either have a page or be able to release one.
                _semaphore.Release();
                throw;
            }
        }

        #pragma warning disable CA1416
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task LoginAsync(IPage page)
#pragma warning restore CA1416
        {
            const int maxLoginAttempts = 3;
            const int retryDelayMilliseconds = 2000;

            _encryption.LoadAndValidateCredentials(out var id, out var pw);

            for (int attempt = 1; attempt <= maxLoginAttempts; attempt++)
            {
                try
                {
                    await page.GotoAsync("https://member.inven.co.kr/user/scorpio/mlogin", new PageGotoOptions { Timeout = _settings.PageLoadTimeoutMilliseconds, WaitUntil = WaitUntilState.DOMContentLoaded });
                    await page.FillAsync("#user_id", id, new PageFillOptions { Timeout = (float)_settings.CommandTimeoutMilliSeconds });
                    await page.FillAsync("#password", pw, new PageFillOptions { Timeout = (float)_settings.CommandTimeoutMilliSeconds });
                    await page.ClickAsync("#loginBtn", new PageClickOptions { Timeout = (float)_settings.CommandTimeoutMilliSeconds });

                    await page.WaitForFunctionAsync(@"() => {
                        const notice = document.querySelector('#notice');
                        return window.location.href !== 'https://member.inven.co.kr/user/scorpio/mlogin' || (notice && notice.textContent.includes('로그인 정보가 일치하지 않습니다.'));
                    }", new PageWaitForFunctionOptions { Timeout = (float)_settings.PageLoadTimeoutMilliseconds });

                    if (page.Url.Contains("member.inven.co.kr"))
                    {
                        throw new ApplicationException("Login failed. Please check your credentials.");
                    }

                    // If we reach here, login was successful
                    _logger.Info("Login successful.");
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Warn($"Login attempt {attempt} timed out.");
                    if (attempt == maxLoginAttempts)
                    {
                        _logger.Error("All login attempts have failed due to timeouts.", ex);
                        throw new ApplicationException("Login failed after multiple attempts due to timeouts.", ex);
                    }
                    await Task.Delay(retryDelayMilliseconds);
                }
                catch (Exception ex)
                {
                    // Catch other potential exceptions during login (e.g. page closed)
                    _logger.Error($"An unexpected error occurred during login attempt {attempt}.", ex);
                    throw; // Rethrow non-timeout exceptions immediately
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _browser.CloseAsync();
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
