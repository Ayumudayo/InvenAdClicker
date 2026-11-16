using InvenAdClicker.Models;
using Microsoft.Playwright;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    // One asynchronous DI scope owns the driver, browser, pool and pipeline.
    public sealed class PlaywrightSession : IAsyncDisposable
    {
        private readonly AppSettings _settings;
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private bool _headless;
        private int _aborted;

        public PlaywrightSession(AppSettings settings) => _settings = settings;

        public IBrowser Browser => _browser ?? throw new InvalidOperationException("Playwright session must be initialized before resolving the browser pool.");

        public async Task InitializeAsync(bool smokeTest, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _aborted) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_playwright != null)
                throw new InvalidOperationException("Playwright session is already initialized.");

            _headless = smokeTest || !_settings.Debug.Enabled || _settings.Debug.Headless;
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _browser = await LaunchBrowserAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task<IBrowser> LaunchBrowserAsync()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _aborted) != 0, this);
            var playwright = _playwright ?? throw new InvalidOperationException("Playwright driver has not been initialized.");
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                Timeout = 30000,
                Args = !_settings.Debug.Enabled
                    ? new[] { "--blink-settings=imagesEnabled=false", "--disable-remote-fonts" }
                    : null
            });
            if (Volatile.Read(ref _aborted) != 0)
            {
                await browser.CloseAsync();
                throw new ObjectDisposedException(nameof(PlaywrightSession));
            }
            return browser;
        }

        // The driver transport must be disposed without awaiting a potentially stuck
        // browser command. Pending Playwright calls then fault and can be observed.
        public void Abort()
        {
            Interlocked.Exchange(ref _aborted, 1);
            Interlocked.Exchange(ref _playwright, null)?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_browser?.IsConnected == true)
                    await _browser.CloseAsync();
            }
            finally
            {
                _browser = null;
                Abort();
            }
        }
    }
}
