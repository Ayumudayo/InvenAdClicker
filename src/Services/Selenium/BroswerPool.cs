using InvenAdClicker.Config;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

public class BrowserPool : IDisposable
{
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
        for (int i = 0; i < _maxInstances; i++)
        {
            var browser = CreateNewBrowser();
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

    public void Release(SeleniumWebBrowser browser)
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
        Interlocked.Increment(ref _createdInstances);
        _logger.Info($"Creating browser instance #{_createdInstances}");
        return new SeleniumWebBrowser(_settings, _logger, _encryption);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_availableBrowsers.TryDequeue(out var browser))
            browser.Dispose();

        _semaphore.Dispose();
        _logger.Info($"BrowserPool disposed. Created: {_createdInstances}");
    }
}