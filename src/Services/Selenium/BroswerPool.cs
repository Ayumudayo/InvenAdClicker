using InvenAdClicker.Config;
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

    public BrowserPool(AppSettings settings, ILogger logger, Encryption encryption)
    {
        _settings = settings;
        _logger = logger;
        _encryption = encryption;
        _maxInstances = settings.MaxDegreeOfParallelism;
        _availableBrowsers = new ConcurrentQueue<SeleniumWebBrowser>();
        _semaphore = new SemaphoreSlim(_maxInstances, _maxInstances);

        Console.WriteLine("Browser Pool 생성 중...");
        // 미리 브라우저 인스턴스 생성
        // 추가 1개 인스턴스는 계정 정보 체크용 브라우저로 소비
        for (int i = 0; i < _maxInstances + 1; i++)
        {
            var browser = CreateNewBrowser();
            browser.SetInstanceId((short)(_createdInstances));
            _availableBrowsers.Enqueue(browser);
        }
        Console.WriteLine("Browser Pool 생성 완료");
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