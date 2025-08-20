using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumAdCollector : IAdCollector<SeleniumWebBrowser>
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;

        public SeleniumAdCollector(AppSettings settings, IAppLogger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public Task<List<string>> CollectLinksAsync(SeleniumWebBrowser browser, string url, CancellationToken cancellationToken)
        {
            var driver = browser.Driver;
            var allLinks = new HashSet<string>();

            for (int i = 0; i < _settings.CollectionAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                driver.Navigate().GoToUrl(url);
                WaitForPageLoad(driver, TimeSpan.FromMilliseconds(_settings.PageLoadTimeoutMilliseconds));

                foreach (var iframe in driver.FindElements(By.TagName("iframe")))
                {
                    try
                    {
                        driver.SwitchTo().Frame(iframe);
                        var linksInFrame = driver.FindElements(By.TagName("a"))
                            .Select(e => e.GetAttribute("href"))
                            .Where(h => !string.IsNullOrEmpty(h));
                        foreach (var link in linksInFrame)
                        {
                            allLinks.Add(link);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[Collector] iframe fail {url}: {ex.Message}");
                    }
                    finally
                    {
                        driver.SwitchTo().DefaultContent();
                    }
                }

                if (i < _settings.CollectionAttempts - 1)
                {
                    driver.Navigate().Refresh();
                }
            }

            return Task.FromResult(allLinks.ToList());
        }

        private void WaitForPageLoad(IWebDriver driver, TimeSpan timeout)
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                try
                {
                    var state = ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState")?.ToString();
                    if (state == "complete" || state == "interactive") return;
                }
                catch { }
                Thread.Sleep(100);
            }
            _logger.Warn($"[Collector] 페이지 로드 타임아웃({timeout.TotalMilliseconds}ms). 현재 상태로 진행합니다.");
        }
    }
}
