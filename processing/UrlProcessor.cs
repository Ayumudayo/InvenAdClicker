using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.@struct;
using InvenAdClicker.helper;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;

namespace InvenAdClicker.processing
{
    public class UrlProcessor
    {
        private readonly AppSettings _appSettings;
        private readonly JobManager _jobManager;
        private readonly ProgressTracker _progressTracker;

        public UrlProcessor()
        {
            _appSettings = SettingsManager.LoadSettings();
            _jobManager = new JobManager();
            _progressTracker = ProgressTracker.Instance;
            Logger.Info($"UrlProcessor Initialized. Iteration: {_appSettings.MaxIter} / Set: {_appSettings.MaxSet} / Worker: {_appSettings.MaxWorker}");
        }

        public async Task StartProcessing()
        {
            StartProgressDisplay();
            await ProcessUrlsAsync();
        }

        private void StartProgressDisplay() => Task.Run(() => _progressTracker.PrintProgress());

        private async Task ProcessUrlsAsync()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (var maxDegreeOfParallelism = new SemaphoreSlim(_appSettings.MaxWorker))
            {
                List<Task> tasks = new List<Task>();

                while (!_jobManager.IsEmpty() || tasks.Any(t => !t.IsCompleted))
                {
                    if (_jobManager.Dequeue(out Job job))
                    {
                        await maxDegreeOfParallelism.WaitAsync();

                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                ProcessSingleUrlAsync(job);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"ProcessUrlsAsync Error: {ex}");
                            }
                            finally
                            {
                                maxDegreeOfParallelism.Release();
                            }
                        }));
                    }
                    else
                    {
                        // Remove completed tasks
                        tasks.RemoveAll(t => t.IsCompleted);
                        await Task.Delay(100);
                    }
                }

                await Task.WhenAll(tasks);
            }

            PrintCompletionLog(stopwatch);
        }

        private void ProcessSingleUrlAsync(Job job)
        {
            _progressTracker.UpdateProgress(job.Url, false, false, 1);

            using (var driverService = new WebDriverService())
            {
                if (driverService.SetupAndLogin(out IWebDriver driver))
                {
                    using (driver)
                    {
                        try
                        {
                            ProcessJob(driver, job);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"ProcessSingleUrlAsync Error: {ex}");
                        }
                        finally
                        {
                            _progressTracker.UpdateProgress(job.Url, false, false, -1);
                        }
                    }
                }
                else
                {
                    _progressTracker.UpdateProgress(job.Url, false, false, -1);
                    Logger.Error("Driver setup failed.");
                }
            }

            Logger.Debug($"WebDriverService Disposed | {job.Url} | {job.Iteration}");
        }

        private void ProcessJob(IWebDriver driver, Job job)
        {
            string originalWindow = driver.CurrentWindowHandle;
            driver.Navigate().GoToUrl(job.Url);

            for (int iteration = 0; iteration < job.Iteration; iteration++)
            {
                if (!TryProcessIteration(driver, job.Url, originalWindow))
                {
                    int remaining = job.Iteration - iteration - 1;
                    _jobManager.AppendJob(job.Url, remaining);
                    Logger.Info($"Remaining Job appended. {job.Url} | Remaining: {remaining}");
                    return;
                }
            }
        }

        private bool TryProcessIteration(IWebDriver driver, string url, string originalWindow)
        {
            try
            {
                FindAndClickAds(driver, url);
                Thread.Sleep(_appSettings.IterationInterval); // IterationInterval
                CloseTabs(driver, originalWindow);

                _progressTracker.UpdateProgress(url, true);
                driver.Navigate().Refresh();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Iteration Error: {ex}");
                _progressTracker.UpdateProgress(url, false, true);
                return false;
            }
        }

        private void FindAndClickAds(IWebDriver driver, string url)
        {
            var iframes = WaitForIframes(driver);
            foreach (var iframe in iframes)
            {
                TryClickAdsInIframe(driver, iframe, url);
            }
        }

        private ReadOnlyCollection<IWebElement> WaitForIframes(IWebDriver driver)
        {
            return new WebDriverWait(driver, TimeSpan.FromSeconds(60))
                .Until(ExpectedConditions.PresenceOfAllElementsLocatedBy(By.CssSelector("iframe[id^='comAd']")));
        }

        private void TryClickAdsInIframe(IWebDriver driver, IWebElement iframe, string url)
        {
            try
            {
                driver.SwitchTo().Frame(iframe);
                ClickAds(driver);
                driver.SwitchTo().DefaultContent();
                Thread.Sleep(_appSettings.ClickIframeInterval); // ClickIframeInterval
            }
            catch (Exception e)
            {
                Logger.Error($"Error clicking ads: {url}\n{e}");
                driver.SwitchTo().DefaultContent();
                Thread.Sleep(200);
            }
        }

        private void ClickAds(IWebDriver driver)
        {
            var adLinks = driver.FindElements(By.TagName("a"))
                .Where(link => link.GetAttribute("href") != null);

            foreach (var link in adLinks)
            {
                ((IJavaScriptExecutor)driver).ExecuteScript($"window.open('{link.GetAttribute("href")}');");
            }
        }

        private void CloseTabs(IWebDriver driver, string originalWindow)
        {
            var extraHandles = driver.WindowHandles.Where(handle => handle != originalWindow);
            foreach (var handle in extraHandles)
            {
                driver.SwitchTo().Window(handle).Close();
            }
            driver.SwitchTo().Window(originalWindow);
        }

        private void PrintCompletionLog(Stopwatch stopwatch)
        {
            stopwatch.Stop();
            TimeSpan elapsed = stopwatch.Elapsed;
            Console.WriteLine($"All tasks are completed.\nRunTime: {elapsed.Minutes}min {elapsed.Seconds}.{elapsed.Milliseconds}sec");
            Logger.Info($"All tasks are completed. RunTime: {elapsed.Minutes}min {elapsed.Seconds}.{elapsed.Milliseconds}sec");
        }
    }
}
