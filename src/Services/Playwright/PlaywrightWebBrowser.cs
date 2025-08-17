using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using System;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightWebBrowser : IAsyncDisposable
    {
        private readonly IBrowser _browser;
        private readonly IPage _page;
        private readonly ILogger _logger;
        private readonly string _loginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";
        private short _instanceId;

        private PlaywrightWebBrowser(IBrowser browser, IPage page, ILogger logger)
        {
            _browser = browser;
            _page = page;
            _logger = logger;
        }

        public static async Task<PlaywrightWebBrowser> CreateAsync(IPlaywright playwright, AppSettings settings, ILogger logger, Encryption encryption)
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true // Hardcode headless mode to match Selenium implementation
            });

            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            var webBrowser = new PlaywrightWebBrowser(browser, page, logger);
            await webBrowser.Login(settings, encryption);

            return webBrowser;
        }

        private async Task Login(AppSettings settings, Encryption encryption)
        {
            _logger.Info("Attempting to log in with Playwright...");
            Console.WriteLine("Attempting to log in with Playwright...");

            encryption.LoadAndValidateCredentials(out var id, out var pw);

            try
            {
                _logger.Info("Navigating to login page...");
                Console.WriteLine("Navigating to login page...");
                await _page.GotoAsync(_loginUrl, new PageGotoOptions { Timeout = settings.PageLoadTimeoutMilliseconds });

                _logger.Info("Filling out login form...");
                Console.WriteLine("Filling out login form...");
                await _page.FillAsync("#user_id", id, new PageFillOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });
                await _page.FillAsync("#password", pw, new PageFillOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });

                _logger.Info("Clicking login button...");
                Console.WriteLine("Clicking login button...");
                await _page.ClickAsync("#loginBtn", new PageClickOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });

                _logger.Info("Waiting for navigation or login result...");
                Console.WriteLine("Waiting for navigation or login result...");
                await _page.WaitForFunctionAsync(@"() => {
                    const notice = document.querySelector('#notice');
                    return window.location.href !== 'https://member.inven.co.kr/user/scorpio/mlogin' || (notice && notice.textContent.includes('로그인 정보가 일치하지 않습니다.'));
                }", new PageWaitForFunctionOptions { Timeout = (float)settings.PageLoadTimeoutMilliseconds });
            }
            catch (TimeoutException ex)
            {
                _logger.Error("Timeout during Playwright login process.", ex);
                throw new ApplicationException("Login failed due to a timeout. Check credentials and network.");
            }
            catch (Exception ex)
            {
                _logger.Error("An unexpected error occurred during Playwright login.", ex);
                throw;
            }

            if (_page.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
            {
                var noticeText = await _page.Locator("#notice").TextContentAsync();
                var errorMessage = $"Instance #{_instanceId} | 로그인 실패: {noticeText}";
                _logger.Error(errorMessage);
                throw new ApplicationException(errorMessage);
            }

            _logger.Info("Login successful.");
            Console.WriteLine("Login successful.");
        }

        public void SetInstanceId(short id)
        {
            _instanceId = id;
        }

        public short InstanceId => _instanceId;
        public IPage Page => _page;

        public async ValueTask DisposeAsync()
        {
            await _browser.CloseAsync();
        }
    }
}