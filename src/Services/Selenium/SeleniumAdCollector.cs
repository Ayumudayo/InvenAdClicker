using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using System.Collections.Concurrent;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumAdCollector : IAdCollector
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly Encryption _encryption;

        public SeleniumAdCollector(
            AppSettings settings,
            ILogger logger,
            Encryption encryption)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
        }

        public async Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
            string[] urls,
            CancellationToken cancellationToken = default)
        {
            // 브라우저 풀 생성 (MaxDegreeOfParallelism 개수만큼)
            var browserPool = new ConcurrentQueue<SeleniumWebBrowser>();
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                // 생성자에서 로그인까지 완료됨
                browserPool.Enqueue(new SeleniumWebBrowser(_settings, _logger, _encryption));
            }

            try
            {
                var result = new ConcurrentDictionary<string, IEnumerable<string>>();
                var po = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                };

                await Task.Run(() =>
                {
                    Parallel.ForEach(urls, po, pageUrl =>
                    {
                        po.CancellationToken.ThrowIfCancellationRequested();

                        // 스레드 카운트 +1
                        ProgressTracker.Instance.Update(
                            pageUrl,
                            threadDelta: +1,
                            status: ProgressStatus.Collecting);

                        // 풀에서 브라우저 꺼내기
                        if (!browserPool.TryDequeue(out var browser))
                            throw new InvalidOperationException("No available browser in pool");

                        try
                        {
                            browser.Driver.Navigate().GoToUrl(pageUrl);

                            // iframe 순회하여 광고 링크 수집
                            var frames = browser.Driver
                                               .FindElements(By.TagName("iframe"))
                                               .ToList();
                            _logger.Info($"[{pageUrl}] Found {frames.Count} iframes");

                            var links = new List<string>();
                            foreach (var frame in frames)
                            {
                                try
                                {
                                    browser.Driver.SwitchTo().Frame(frame);
                                    links.AddRange(
                                        browser.Driver.FindElements(By.TagName("a"))
                                            .Select(a => a.GetAttribute("href"))
                                            .Where(h => !string.IsNullOrEmpty(h)));
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn($"[{pageUrl}] iframe 오류: {ex.Message}");
                                    ProgressTracker.Instance.Update(
                                        pageUrl,
                                        errDelta: 1);
                                }
                                finally
                                {
                                    browser.Driver.SwitchTo().DefaultContent();
                                }
                            }

                            var distinct = links.Distinct().ToArray();
                            result[pageUrl] = distinct;

                            // 수집 통계 반영
                            ProgressTracker.Instance.Update(
                                pageUrl,
                                status: ProgressStatus.Collected,
                                adsDelta: distinct.Length);
                        }
                        finally
                        {
                            // 스레드 카운트 -1
                            ProgressTracker.Instance.Update(
                                pageUrl,
                                threadDelta: -1);

                            // 브라우저 반납
                            browserPool.Enqueue(browser);
                        }
                    });
                }, cancellationToken);

                return result.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            finally
            {
                // 풀에 남은 브라우저 전부 종료
                while (browserPool.TryDequeue(out var browser))
                {
                    browser.Dispose();
                }
            }
        }
    }
}
