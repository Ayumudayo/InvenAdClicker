using InvenAdClicker.Models;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class BrowserPool : IDisposable
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

    private async Task CreateAndPoolBrowserAsync(CancellationToken cancellationToken)
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

        return CreateNewBrowser();
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

    private SeleniumWebBrowser CreateNewBrowser()
    {
        var browser = new SeleniumWebBrowser(_settings, _logger, _encryption);
        Interlocked.Increment(ref _createdInstances);
        lock (_allBrowsers) { _allBrowsers.Add(browser); }
        _logger.Info($"Creating new browser instance #{_allBrowsers.Count}");
        return browser;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 흐름 상 Lock이 필요한 구간에서 호출되진 않지만,
        // 혹시 모르는 만약을 위해 Lock 사용
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
}