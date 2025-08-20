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
            _logger.Info($"Playwright 브라우저 풀 초기화 완료({_pool.Count} 페이지 준비됨)");
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<IPage> CreatePageAsync(CancellationToken cancellationToken)
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = new[] { new KeyValuePair<string, string>("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8") }
            });
            var page = await context.NewPageAsync();

            // 불필요한 리소스를 차단(허용 목록 기반의 적극적 필터링)
            await page.RouteAsync("**/*", async route =>
            {
                var resourceType = route.Request.ResourceType;
                bool shouldContinue = false;

                // 핵심 리소스: 항상 허용
                if (resourceType == "document" || resourceType == "script" || resourceType == "xhr" || resourceType == "fetch")
                {
                    shouldContinue = true;
                }
                // 설정값에 따라 조건부 허용
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

            // 풀에 여유분이 없으면 새 페이지를 생성(정상 초기화 시 드문 경로)
            _logger.Warn("Playwright 풀에 여유 페이지가 없습니다. 필요 시 즉시 생성합니다.");
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
                _logger.Warn("null 또는 종료된 페이지가 반환되었습니다. 대체 페이지를 생성합니다.");
                // 여기서는 semaphore를 즉시 해제하지 않음. 새 페이지 생성 후 해제.
                Task.Run(async () =>
                {
                    try
                    {
                        var newPage = await CreatePageAsync(CancellationToken.None);
                        _pool.Add(newPage);
                        _semaphore.Release();
                        _logger.Info("풀 대체 페이지 생성 성공");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("대체 페이지 생성 실패", ex);
                        // 생성 실패 시에는 데드락 방지를 위해 semaphore를 해제
                        _semaphore.Release();
                    }
                });
            }
        }

        public async Task<IPage> RenewAsync(IPage oldPage, CancellationToken cancellationToken = default)
        {
            _logger.Info("Playwright 페이지 갱신");
            if (oldPage != null)
            {
                // 기존 페이지의 컨텍스트를 닫으면 페이지도 함께 종료됨
                await oldPage.Context.CloseAsync();
            }

            // 이전 페이지로 점유되던 semaphore 슬롯이 비워졌으므로
            // 풀 크기를 유지하기 위해 새 페이지를 생성한다.
            // 호출자가 이미 슬롯을 점유한 상태이므로 여기서 대기할 필요 없음
            try
            {
                return await CreatePageAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error("갱신 중 새 페이지 생성 실패", ex);
                // 실패 시 호출자는 페이지를 보유하거나 해제할 수 있어야 하므로 semaphore를 해제
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
                        throw new ApplicationException("로그인에 실패했습니다. 자격증명을 확인해 주세요.");
                    }

                    // 여기까지 도달했다면 로그인 성공
                    _logger.Info("로그인 성공");
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Warn($"로그인 {attempt}회차 시간 초과");
                    if (attempt == maxLoginAttempts)
                    {
                        _logger.Error("모든 로그인 시도가 시간 초과로 실패", ex);
                        throw new ApplicationException("여러 차례의 시도 끝에 로그인에 실패했습니다(시간 초과).", ex);
                    }
                    await Task.Delay(retryDelayMilliseconds);
                }
                catch (Exception ex)
                {
                    // 로그인 중 발생 가능한 기타 예외 처리(예: 페이지 종료 등)
                    _logger.Error($"로그인 {attempt}회차 중 예기치 못한 오류", ex);
                    throw; // 시간 초과 외 예외는 즉시 throw
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
