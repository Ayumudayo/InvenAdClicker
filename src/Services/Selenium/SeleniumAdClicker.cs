using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using System;
using System.Threading;
using System.Threading.Tasks;

public class SeleniumAdClicker : IAdClicker<SeleniumWebBrowser>
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public SeleniumAdClicker(AppSettings settings, ILogger logger)
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
            _logger.Error($"WebDriver error during click of '{link}': {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected error during ClickWithBrowserAsync: {ex.Message}");
            throw;
        }
    }
}
