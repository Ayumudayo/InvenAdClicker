using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace InvenAdClicker.Services.Selenium
{
    public class BrowserPool : IBrowserPool<SeleniumWebBrowser>
    {
        private readonly List<SeleniumWebBrowser> _allBrowsers = new();
        private readonly ConcurrentQueue<SeleniumWebBrowser> _availableBrowsers;
        private readonly SemaphoreSlim _semaphore;
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly Encryption _encryption;
        private readonly int _maxInstances;
        private int _createdInstances;
        private bool _disposed;
        private bool _isInitialized = false;

        public BrowserPool(AppSettings settings, ILogger logger, Encryption encryption)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
            _maxInstances = settings.MaxDegreeOfParallelism;
            _availableBrowsers = new ConcurrentQueue<SeleniumWebBrowser>();
            _semaphore = new SemaphoreSlim(_maxInstances, _maxInstances);
        }

        public async Task InitializePoolAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized) return;

            _logger.Info("Initializing Selenium Browser Pool...");
            var initialTasks = new Task[_maxInstances];
            for (int i = 0; i < _maxInstances; i++)
            {
                initialTasks[i] = CreateAndPoolBrowserAsync(cancellationToken);
            }
            await Task.WhenAll(initialTasks);
            _isInitialized = true;
            _logger.Info($"Selenium Browser Pool initialized with {_availableBrowsers.Count} browser instances.");
        }

        #pragma warning disable CA1416
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task CreateAndPoolBrowserAsync(CancellationToken cancellationToken)
        #pragma warning restore CA1416
        {
            var browser = new SeleniumWebBrowser(_settings, _logger, _encryption);
            await browser.LoginAsync(cancellationToken);
            Interlocked.Increment(ref _createdInstances);
            browser.SetInstanceId((short)_createdInstances);
            lock (_allBrowsers) { _allBrowsers.Add(browser); }
            _availableBrowsers.Enqueue(browser);
        }

        public async Task<SeleniumWebBrowser> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);

            if (_availableBrowsers.TryDequeue(out var browser))
            {
                try
                {
                    _ = browser.Driver.CurrentWindowHandle;
                    return browser;
                }
                catch
                {
                    browser.Dispose();
                }
            }

            var newBrowser = await CreateNewBrowserAsync(cancellationToken);
            return newBrowser;
        }

        public void Release(SeleniumWebBrowser? browser)
        {
            if (browser == null)
            {
                _semaphore.Release();
                return;
            }

            try
            {
                // 유효성 검사: 내부 WebDriver가 살아있는지 확인
                _ = browser.Driver.CurrentWindowHandle;

                // 살아 있으면 정상적으로 풀에 반환
                _availableBrowsers.Enqueue(browser);
            }
            catch
            {
                // 죽어 있으면 즉시 폐기
                // 풀 크기 보전을 위해
                // 첫 Acquire 때 새로 생성되도록 semaphore만 풀어줌
                browser.Dispose();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<SeleniumWebBrowser> RenewAsync(SeleniumWebBrowser oldBrowser, CancellationToken cancellationToken = default)
        {
            _logger.Info("Renewing a Selenium browser.");
            if (oldBrowser != null)
            {
                oldBrowser.Dispose();
            }

            return await CreateNewBrowserAsync(cancellationToken);
        }

        #pragma warning disable CA1416
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<SeleniumWebBrowser> CreateNewBrowserAsync(CancellationToken cancellationToken)
        #pragma warning restore CA1416
        {
            var browser = new SeleniumWebBrowser(_settings, _logger, _encryption);
            await browser.LoginAsync(cancellationToken);
            Interlocked.Increment(ref _createdInstances);
            browser.SetInstanceId((short)_createdInstances);
            lock (_allBrowsers) { _allBrowsers.Add(browser); }
            _logger.Info($"Creating new browser instance #{_allBrowsers.Count}");
            return browser;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                // managed resources
            }

            lock (_allBrowsers)
            {
                foreach (var browser in _allBrowsers)
                {
                    try
                    {
                        browser.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Error disposing browser: {ex.Message}");
                    }
                }
                _allBrowsers.Clear();
            }

            _semaphore.Dispose();
            _logger.Info($"Browser pool disposed. Total instances created: {_createdInstances}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            // 비동기 정리 로직 (필요한 경우)
            await Task.Run(() => Dispose(false));

            GC.SuppressFinalize(this);
        }
    }
}
