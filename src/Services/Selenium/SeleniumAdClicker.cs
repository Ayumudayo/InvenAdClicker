using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using OpenQA.Selenium;
using System;
using System.Threading;
using System.Threading.Tasks;
namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumAdClicker : IAdClicker<SeleniumWebBrowser>
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;

        public SeleniumAdClicker(AppSettings settings, IAppLogger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public async Task<SeleniumWebBrowser> ClickAdAsync(SeleniumWebBrowser browser, string link, CancellationToken cancellationToken)
        {
            try
            {
                browser.Driver.Navigate().GoToUrl(link);
                await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
                return browser;
            }
            catch (WebDriverException ex)
            {
                _logger.Error($"클릭 처리 중 WebDriver 오류('{link}'): {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"클릭 처리 중 예기치 못한 오류: {ex.Message}");
                throw;
            }
        }
    }
}
