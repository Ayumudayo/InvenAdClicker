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
        private volatile IBrowser _browser;
        private readonly Func<Task<IBrowser>>? _browserFactory;
        private readonly Action? _abortDriver;
        private readonly ConcurrentQueue<IPage> _pool;
        private readonly SemaphoreSlim _semaphore;
        private readonly SemaphoreSlim _browserRestartLock = new(1, 1);
        private readonly SemaphoreSlim _creationLock = new(1, 1);
        private readonly object _poolStateLock = new();
        // Includes available and checked-out pages; maintenance must not count only the queue.
        private int _pageCount;
        private readonly CancellationTokenSource _maintenanceCancellation = new();
        private readonly SemaphoreSlim _maintenanceSignal = new(0, 1);
        private Task? _maintenanceTask;
        private Task? _disposeTask;
        private Task? _abortTask;
        private bool _disposed;
        private string? _storageState;

        public PlaywrightBrowserPool(IBrowser browser, AppSettings settings, IAppLogger logger, Encryption encryption, Func<Task<IBrowser>>? browserFactory = null, Action? abortDriver = null)
        {
            _browser = browser;
            _browserFactory = browserFactory;
            _abortDriver = abortDriver;
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
            _pool = new ConcurrentQueue<IPage>();
            _semaphore = new SemaphoreSlim(settings.MaxDegreeOfParallelism, settings.MaxDegreeOfParallelism);
        }

        public async Task InitializePoolAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            // 1. Perform initial login and capture storage state
            _logger.Info("초기 로그인 및 세션 캡처 시작...");
            var loginContext = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                JavaScriptEnabled = true
            });
            Exception? loginFailure = null;
            try
            {
                var loginPage = await loginContext.NewPageAsync();
                await PlaywrightLoginWorkflow.LoginAsync(loginPage, _settings, _encryption, cancellationToken);
                
                await CaptureStorageStateAsync(loginContext, cancellationToken);
                _logger.Info("로그인 세션 캡처 완료");
            }
            catch (Exception ex)
            {
                loginFailure = ex;
                _logger.Fatal("초기 로그인 실패. 프로그램을 종료합니다.", ex);
                throw;
            }
            finally
            {
                if (loginFailure == null) await loginContext.CloseAsync();
                else await ExceptionCleanup.CaptureAsync(loginFailure, () => loginContext.CloseAsync());
            }

            // 2. Initialize pool with authenticated contexts
            await MaintainPoolAsync(_settings.MaxDegreeOfParallelism, cancellationToken);
            StartMaintenance();
            _logger.Info($"Playwright 브라우저 풀 초기화 완료({_pool.Count}개 인스턴스, 인증 세션 적용됨)");
        }

        internal async Task CaptureStorageStateAsync(IBrowserContext context, CancellationToken cancellationToken = default)
        {
            // No Path is supplied: authentication state never needs a temporary disk file.
            var state = await context.StorageStateAsync();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_poolStateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _storageState = state;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<IPage> CreatePageAsync(CancellationToken cancellationToken)
        {
            var contextOptions = new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                JavaScriptEnabled = _settings.Debug.Enabled ? _settings.Debug.JavaScriptEnabled : true,
                StorageState = _storageState
            };

            var browser = await GetConnectedBrowserAsync(cancellationToken);
            try
            {
                return await CreatePageAsync(browser, contextOptions);
            }
            catch (Exception ex) when (IsBrowserTargetClosed(ex) && _browserFactory != null)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Warn($"Playwright 브라우저 연결이 종료되어 재시작합니다: {ex.Message}");
                await RestartBrowserAsync(browser, cancellationToken);
                return await CreatePageAsync(_browser, contextOptions);
            }
        }

        private async Task<IPage> CreatePageAsync(IBrowser browser, BrowserNewContextOptions contextOptions)
        {
            var context = await browser.NewContextAsync(contextOptions);
            try
            {
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
                        PlaywrightPageTelemetry.RecordRouteDecision(page, resourceType, continued: true);
                        await route.ContinueAsync();
                    }
                    else
                    {
                        PlaywrightPageTelemetry.RecordRouteDecision(page, resourceType, continued: false);
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
            catch
            {
                try { await context.CloseAsync(); } catch { }
                throw;
            }
        }

        private async Task<IBrowser> GetConnectedBrowserAsync(CancellationToken cancellationToken)
        {
            var browser = _browser;
            if (browser.IsConnected || _browserFactory == null)
            {
                return browser;
            }

            _logger.Warn("Playwright 브라우저 연결이 끊겨 재시작합니다.");
            await RestartBrowserAsync(browser, cancellationToken);
            return _browser;
        }

        private async Task RestartBrowserAsync(IBrowser failedBrowser, CancellationToken cancellationToken)
        {
            if (_browserFactory == null)
            {
                return;
            }

            await _browserRestartLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                if (!ReferenceEquals(_browser, failedBrowser) && _browser.IsConnected)
                {
                    return;
                }

                await ClosePooledPagesAsync();

                try
                {
                    if (failedBrowser.IsConnected)
                    {
                        await failedBrowser.CloseAsync();
                    }
                }
                catch
                {
                    // The browser may already be gone; restart is still the recovery path.
                }

                var replacement = await _browserFactory();
                lock (_poolStateLock)
                {
                    if (!_disposed) _browser = replacement;
                }
                if (IsDisposed)
                {
                    await replacement.CloseAsync();
                    ThrowIfDisposed();
                }
                _logger.Info("Playwright 브라우저 재시작 완료");
            }
            finally
            {
                _browserRestartLock.Release();
            }
        }

        private async Task ClosePooledPagesAsync()
        {
            while (_pool.TryDequeue(out var page))
            {
                lock (_poolStateLock) { _pageCount--; }
                try { await page.Context.CloseAsync(); }
                catch { /* 무시 */ }
            }
        }

        private static bool IsBrowserTargetClosed(Exception ex)
        {
            return ex.GetType().FullName == "Microsoft.Playwright.TargetClosedException"
                || (ex is PlaywrightException
                    && ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<IPage> AcquireAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCancellation.Token);
            cancellationToken = stopping.Token;
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await _creationLock.WaitAsync(cancellationToken);
                try
                {
                    ThrowIfDisposed();
                    while (_pool.TryDequeue(out var page))
                    {
                        if (IsPageReusable(page)) return page;
                        lock (_poolStateLock) { _pageCount--; }
                        await ClosePageContextBestEffortAsync(page);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.Debug("Playwright 풀에 사용할 페이지가 없어 새 페이지를 생성합니다.");
                    var replacement = await CreatePageAsync(cancellationToken);
                    await ValidateCreatedPageAsync(replacement, cancellationToken);
                    lock (_poolStateLock) { _pageCount++; }
                    return replacement;
                }
                finally { _creationLock.Release(); }
            }
            catch
            {
                _semaphore.Release();
                throw;
            }
        }

        public void Release(IPage page)
        {
            try
            {
                lock (_poolStateLock)
                {
                    if (!_disposed && IsPageReusable(page))
                    {
                        _pool.Enqueue(page);
                        return;
                    }
                    _pageCount--;
                }
                _logger.Warn("종료된 페이지를 폐기하고 유지보수 루프에 보충을 요청합니다.");
                ClosePageContextBestEffortAsync(page).GetAwaiter().GetResult();
                SignalMaintenance();
            }
            finally { _semaphore.Release(); }
        }

        private static async Task ClosePageContextBestEffortAsync(IPage? page)
        {
            if (page == null) return;
            try { await page.Context.CloseAsync(); }
            catch { /* The page/context/browser may already be closed. */ }
        }

        private static bool IsPageReusable(IPage? page)
        {
            if (page == null || page.IsClosed)
            {
                return false;
            }

            try
            {
                var browser = page.Context?.Browser;
                return browser?.IsConnected == true;
            }
            catch
            {
                return false;
            }
        }

        public Task PreloadAsync(int count, CancellationToken cancellationToken = default)
            => MaintainPoolAsync(count, cancellationToken);

        public async Task MaintainPoolAsync(int desiredSize, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCancellation.Token);
            cancellationToken = stopping.Token;
            if (desiredSize < 0) throw new ArgumentOutOfRangeException(nameof(desiredSize));
            desiredSize = Math.Min(desiredSize, _settings.MaxDegreeOfParallelism);
            await _creationLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                var stalePages = new List<IPage>();
                lock (_poolStateLock)
                {
                    int idlePages = _pool.Count;
                    for (int index = 0; index < idlePages && _pool.TryDequeue(out var idlePage); index++)
                    {
                        if (IsPageReusable(idlePage)) _pool.Enqueue(idlePage);
                        else
                        {
                            _pageCount--;
                            stalePages.Add(idlePage);
                        }
                    }
                }
                foreach (var stalePage in stalePages) await ClosePageContextBestEffortAsync(stalePage);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    lock (_poolStateLock)
                    {
                        if (_pageCount >= desiredSize) return;
                    }
                    var page = await CreatePageAsync(cancellationToken);
                    await ValidateCreatedPageAsync(page, cancellationToken);
                    lock (_poolStateLock)
                    {
                        _pageCount++;
                        _pool.Enqueue(page);
                    }
                }
            }
            finally { _creationLock.Release(); }
        }

        // Initialization owns this task; disposal cancels and awaits it before closing the browser.
        internal void StartMaintenance()
        {
            lock (_poolStateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _maintenanceTask ??= MaintainContinuouslyAsync(_maintenanceCancellation.Token);
            }
        }

        private async Task MaintainContinuouslyAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    // Invalid releases wake this immediately; the timer also detects pages closed while idle.
                    await _maintenanceSignal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                    try { await MaintainPoolAsync(_settings.MaxDegreeOfParallelism, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        if (IsDisposed) return;
                        _logger.Warn($"브라우저 풀 보충 실패. 다음 유지보수 주기에 재시도합니다: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private void SignalMaintenance()
        {
            lock (_poolStateLock)
            {
                if (!_disposed && _maintenanceSignal.CurrentCount == 0) _maintenanceSignal.Release();
            }
        }

        private bool IsDisposed { get { lock (_poolStateLock) return _disposed; } }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

        public void HandleBrowserCrash(Exception ex)
        {
            _logger.Error("브라우저 풀에서 예기치 못한 오류", ex);
            while (_pool.TryDequeue(out var page))
            {
                lock (_poolStateLock) { _pageCount--; }
                ClosePageContextBestEffortAsync(page).GetAwaiter().GetResult();
            }
        }

        public async Task<IPage> RenewAsync(IPage oldPage, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _maintenanceCancellation.Token);
            cancellationToken = stopping.Token;
            await _creationLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                _logger.Info("Playwright 페이지 갱신");
                if (oldPage != null) await ClosePageContextBestEffortAsync(oldPage);

                // The caller retains its leased slot and must release it even when renewal fails.
                var replacement = await CreatePageAsync(cancellationToken);
                await ValidateCreatedPageAsync(replacement, cancellationToken);
                return replacement;
            }
            catch (Exception ex)
            {
                _logger.Error("새 페이지 생성 실패", ex);
                throw;
            }
            finally { _creationLock.Release(); }
        }

        private async Task ValidateCreatedPageAsync(IPage page, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || IsDisposed)
            {
                await ClosePageContextBestEffortAsync(page);
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            Task abort;
            lock (_poolStateLock)
            {
                if (_disposeTask?.IsCompleted == true) return _disposeTask.WaitAsync(cancellationToken);
                _disposed = true;
                _storageState = null;
                abort = _abortTask ??= AbortCoreAsync();
            }
            return abort.WaitAsync(cancellationToken);
        }

        private async Task AbortCoreAsync()
        {
            try { _maintenanceCancellation.Cancel(); }
            catch (ObjectDisposedException) { /* A concurrent disposal already stopped maintenance. */ }
            // Deliberately bypass the creation gate: the operation holding it may be
            // blocked in Playwright and requires browser/driver shutdown to unblock.
            Task close = Task.CompletedTask;
            Exception? driverError = null;
            try
            {
                if (_browser.IsConnected) close = _browser.CloseAsync();
            }
            finally
            {
                try { _abortDriver?.Invoke(); }
                catch (Exception ex) { driverError = ex; }
            }
            try { await close; }
            catch (Exception ex) when (driverError == null && IsBrowserTargetClosed(ex))
            {
                // Closing the driver may complete the concurrent browser close with
                // TargetClosed; the owned browser is already stopped in that case.
            }
            catch (Exception ex) when (driverError != null) { throw new AggregateException(ex, driverError); }
            if (driverError != null) throw driverError;
        }

        public ValueTask DisposeAsync()
        {
            lock (_poolStateLock)
            {
                _disposed = true;
                _storageState = null;
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            _maintenanceCancellation.Cancel();
            if (_maintenanceTask != null) await _maintenanceTask;
            await _creationLock.WaitAsync();
            try { await ClosePooledPagesAsync(); }
            finally { _creationLock.Release(); }
            _maintenanceCancellation.Dispose();
            _maintenanceSignal.Dispose();

            if (_abortTask != null) await _abortTask;
            else if (_browser.IsConnected) await _browser.CloseAsync();

            _storageState = null;
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
