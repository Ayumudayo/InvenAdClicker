using InvenAdClicker.Models;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Selenium
{
    // 남는 워커가 클릭 작업으로 전환되는 수집/클릭 파이프라인 실행기
    public class SeleniumPipelineRunner
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly BrowserPool _browserPool;
        private readonly ProgressTracker _progress;

        public SeleniumPipelineRunner(AppSettings settings, ILogger logger,
            BrowserPool browserPool, ProgressTracker progress)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
            _progress = progress;
        }

        public async Task RunAsync(string[] urls, CancellationToken cancellationToken = default)
        {
            var urlChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false
            });

            var clickChannel = Channel.CreateUnbounded<(string page, string link)>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            });

            // 남은 URL 수와 현재 수집 중 워커 수를 추적하여
            // 남는 워커가 클릭 작업으로 전환되도록 제어
            int urlsRemaining = urls.Length; // 아직 작업에 착수하지 않은 URL 수
            int activeCollectors = 0;        // 현재 수집 중 워커 수
            var clickWriter = clickChannel.Writer;

            // URL 공급
            _ = Task.Run(async () =>
            {
                foreach (var url in urls)
                {
                    await urlChannel.Writer.WriteAsync(url, cancellationToken);
                    _progress.Update(url, ProgressStatus.Waiting, iterDelta: 1);
                }
                urlChannel.Writer.Complete();
            }, cancellationToken);

            // 클릭 채널 완료 감시자: 모든 URL 수집 착수 완료(urlsRemaining==0)이고
            // 활성 수집기(activeCollectors==0)이며 더 이상 쓸 항목이 없다면 Complete
            _ = Task.Run(async () =>
            {
                // urlChannel 완료까지 대기
                await urlChannel.Reader.Completion;

                // 남은 수집 작업이 끝날 때까지 폴링
                while (Volatile.Read(ref activeCollectors) > 0)
                {
                    await Task.Delay(50, cancellationToken);
                }
                clickWriter.TryComplete();
            }, cancellationToken);

            var workers = new Task[_settings.MaxDegreeOfParallelism];
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    var browser = await _browserPool.AcquireAsync(cancellationToken);
                    _logger.Info($"PipelineWorker {workerId} started");

                    try
                    {
                        var urlReader = urlChannel.Reader;
                        var linkReader = clickChannel.Reader;

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            bool didWork = false;

                            // 1) 수집 슬롯이 남아있고, 아직 착수하지 않은 URL이 있는 경우 우선 수집 시도
                            if (Volatile.Read(ref urlsRemaining) > 0 &&
                                Volatile.Read(ref activeCollectors) < Volatile.Read(ref urlsRemaining))
                            {
                                // 수집 슬롯 예약
                                Interlocked.Increment(ref activeCollectors);

                                try
                                {
                                    if (urlReader.TryRead(out var url))
                                    {
                                        // 해당 URL을 이제 착수했으므로 남은 수를 감소
                                        Interlocked.Decrement(ref urlsRemaining);

                                        await CollectOneAsync(browser, url, clickWriter, cancellationToken);
                                        didWork = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Error($"[Collector{workerId}] 수집 처리 중 예외", ex);
                                    try { browser.Dispose(); } catch { }
                                    _browserPool.Release(browser);
                                    browser = await _browserPool.AcquireAsync(cancellationToken);
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref activeCollectors);
                                }
                            }

                            if (!didWork)
                            {
                                // 2) 클릭 작업 처리 시도
                                if (linkReader.TryRead(out var work))
                                {
                                    try
                                    {
                                        await ClickOneAsync(browser, work.page, work.link, cancellationToken);
                                        didWork = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Warn($"[Clicker{workerId}] 클릭 중 예외: {ex.Message}");
                                        try { browser.Dispose(); } catch { }
                                        _browserPool.Release(browser);
                                        browser = await _browserPool.AcquireAsync(cancellationToken);
                                    }
                                }
                            }

                            if (!didWork)
                            {
                                // 더 이상 할 일이 없으면 대기/종료 판정
                                var urlWaitTask = urlReader.WaitToReadAsync(cancellationToken).AsTask();
                                var linkWaitTask = linkReader.WaitToReadAsync(cancellationToken).AsTask();
                                await Task.WhenAny(urlWaitTask, linkWaitTask);

                                bool urlHasData = urlWaitTask.IsCompletedSuccessfully && urlWaitTask.Result;
                                bool linkHasData = linkWaitTask.IsCompletedSuccessfully && linkWaitTask.Result;

                                // 두 채널 모두 더 이상 읽을 수 없고, URL 채널은 완료됨 => 종료
                                if (!urlHasData && urlReader.Completion.IsCompleted && !linkHasData)
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        _browserPool.Release(browser);
                        _logger.Info($"PipelineWorker {workerId} stopped");
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(workers);
            _logger.Info("파이프라인 작업이 완료되었습니다.");
        }

        private async Task CollectOneAsync(
            SeleniumWebBrowser browser,
            string url,
            ChannelWriter<(string page, string link)> clickWriter,
            CancellationToken cancellationToken)
        {
            _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);

            List<string> links = new();
            try
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
                                allLinks.Add(link);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"[Collector] iframe 처리 실패 {url}: {ex.Message}");
                        }
                        finally
                        {
                            driver.SwitchTo().DefaultContent();
                        }
                    }

                    if (i < _settings.CollectionAttempts - 1)
                        driver.Navigate().Refresh();
                }

                links = allLinks.ToList();
            }
            catch (Exception ex)
            {
                _logger.Error($"[Collector] 수집 중 예외: {url}", ex);
                _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                throw;
            }
            finally
            {
                _progress.Update(url, threadDelta: -1);
            }

            var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
            _progress.Update(url, status, adsDelta: links.Count);

            if (links.Count > 0)
            {
                // 클릭 대기 수 갱신 후 채널에 투입
                _progress.Update(url, pendingClicksDelta: links.Count);
                foreach (var link in links)
                    await clickWriter.WriteAsync((url, link), cancellationToken);
            }
        }

        private async Task ClickOneAsync(
            SeleniumWebBrowser browser,
            string page,
            string link,
            CancellationToken cancellationToken)
        {
            _progress.Update(page, ProgressStatus.Clicking, threadDelta: +1);
            try
            {
                browser.Driver.Navigate().GoToUrl(link);
                await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
                _progress.Update(page, clickDelta: 1);
            }
            catch (WebDriverException ex)
            {
                _logger.Error($"WebDriver 오류 클릭 '{link}': {ex.Message}");
                _progress.Update(page, errDelta: 1);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"ClickOneAsync 예외: {ex.Message}");
                _progress.Update(page, errDelta: 1);
                throw;
            }
            finally
            {
                _progress.Update(page, pendingClicksDelta: -1, threadDelta: -1);
            }
        }

        private void WaitForPageLoad(IWebDriver driver, TimeSpan timeout)
            => new OpenQA.Selenium.Support.UI.WebDriverWait(driver, timeout)
                .Until(d => ((IJavaScriptExecutor)d)
                    .ExecuteScript("return document.readyState").Equals("complete"));
    }
}
