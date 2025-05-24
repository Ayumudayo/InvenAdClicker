using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System.Collections.Concurrent;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumAdClicker : IAdClicker
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly Encryption _encryption;

        public SeleniumAdClicker(
            AppSettings settings,
            ILogger logger,
            Encryption encryption)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
        }

        public async Task ClickAsync(
            Dictionary<string, IEnumerable<string>> pageToLinks,
            CancellationToken cancellationToken = default)
        {
            // 브라우저 풀 생성
            var browserPool = new ConcurrentQueue<SeleniumWebBrowser>();
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                browserPool.Enqueue(new SeleniumWebBrowser(_settings, _logger, _encryption));
            }

            try
            {
                // 페이지별 병렬 처리
                var po = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                };

                await Task.Run(() =>
                {
                    Parallel.ForEach(pageToLinks, po, kv =>
                    {
                        po.CancellationToken.ThrowIfCancellationRequested();

                        string pageUrl = kv.Key;
                        var links = kv.Value.ToArray();

                        // 클릭 단계 진입: Pending 초기화 + 스레드 카운트
                        ProgressTracker.Instance.Update(
                            pageUrl,
                            status: ProgressStatus.Clicking,
                            pendingClicksDelta: links.Length,
                            threadDelta: +1);

                        // 풀에서 브라우저 꺼내기
                        if (!browserPool.TryDequeue(out var browser))
                            throw new InvalidOperationException("No available browser in pool");

                        try
                        {
                            foreach (var link in links)
                            {
                                try
                                {
                                    po.CancellationToken.ThrowIfCancellationRequested();

                                    browser.Driver.Navigate().GoToUrl(link);
                                    _logger.Info($"Clicked ad: {link}");

                                    ProgressTracker.Instance.Update(
                                        pageUrl,
                                        clickDelta: 1,
                                        pendingClicksDelta: -1);

                                    // 고정 Delay
                                    Thread.Sleep(_settings.ClickDelayMilliseconds);
                                }
                                catch (Exception exLink)
                                {
                                    _logger.Warn($"[{pageUrl}] 클릭 오류: {exLink.Message}");
                                    ProgressTracker.Instance.Update(
                                        pageUrl,
                                        errDelta: 1);
                                }
                            }
                        }
                        finally
                        {
                            // 스레드 감소 + 브라우저 반납
                            ProgressTracker.Instance.Update(
                                pageUrl,
                                threadDelta: -1);
                            browserPool.Enqueue(browser);
                        }
                    });
                }, cancellationToken);
            }
            finally
            {
                // 풀에 남은 브라우저 모두 종료
                while (browserPool.TryDequeue(out var browser))
                {
                    browser.Dispose();
                }
            }
        }
    }
}
