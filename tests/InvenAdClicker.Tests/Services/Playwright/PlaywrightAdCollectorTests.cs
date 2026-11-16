using InvenAdClicker.Models;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [TestFixture]
    public class PlaywrightAdCollectorTests
    {
        private Mock<IAppLogger> _loggerMock = null!;
        private ProgressTracker _progress = null!;
        private AppSettings _settings = null!;

        [SetUp]
        public void SetUp()
        {
            _loggerMock = new Mock<IAppLogger>();
            _progress = ProgressTracker.Instance;
            _settings = new AppSettings
            {
                CollectionAttempts = 1,
                PageLoadTimeoutMilliseconds = 5000,
                IframeTimeoutMilliseconds = 1000,
                PostMessageBufferMilliseconds = 0
            };
            _progress.Initialize(new[] { "https://test.inven.co.kr/" });
        }

        [Test]
        public async Task CollectLinksAsync_WhenFramesContainAllowedAds_ReturnsAllowedLinksOnly()
        {
            var pageMock = new Mock<IPage>();
            var parentFrameMock = new Mock<IFrame>();
            var childFrameMock = new Mock<IFrame>();

            childFrameMock.SetupGet(f => f.ParentFrame).Returns(parentFrameMock.Object);
            childFrameMock.SetupGet(f => f.Url).Returns("https://zicf.inven.co.kr/RealMedia/ads/frame.html");

            var extractedHrefs = new[]
            {
                "https://zicf.inven.co.kr/RealMedia/ads/click_lx.ads/test1",
                "https://otherdomain.com/tracker?ad=1",
                "//zicf.inven.co.kr/RealMedia/ads/click_lx.ads/test2"
            };

            childFrameMock.Setup(f => f.EvaluateAsync<string[]>(It.IsAny<string>(), null))
                .ReturnsAsync(extractedHrefs);

            pageMock.SetupGet(p => p.Frames).Returns(new List<IFrame> { parentFrameMock.Object, childFrameMock.Object });
            pageMock.Setup(p => p.EvaluateAsync<string[]>(It.Is<string>(s => s.Contains("__ad_links")), null))
                .ReturnsAsync(Array.Empty<string>());
            pageMock.Setup(p => p.EvaluateAsync<string[]>(It.Is<string>(s => s.Contains("__ad_logs")), null))
                .ReturnsAsync(Array.Empty<string>());

            var collector = new PlaywrightAdCollector(_settings, _loggerMock.Object, _progress);

            var links = await collector.CollectLinksAsync(pageMock.Object, "https://test.inven.co.kr/", CancellationToken.None);

            Assert.That(links, Has.Count.EqualTo(2));
            Assert.That(links, Does.Contain("https://zicf.inven.co.kr/RealMedia/ads/click_lx.ads/test1"));
            Assert.That(links, Does.Contain("https://zicf.inven.co.kr/RealMedia/ads/click_lx.ads/test2"));
            Assert.That(links, Does.Not.Contain("https://otherdomain.com/tracker?ad=1"));

            pageMock.Verify(p => p.GotoAsync("https://test.inven.co.kr/", It.IsAny<PageGotoOptions>()), Times.Once);
        }

        [Test]
        public async Task CollectLinksAsync_MergesPostMessageHookLinks()
        {
            var pageMock = new Mock<IPage>();
            pageMock.SetupGet(p => p.Frames).Returns(new List<IFrame>());

            var hookLinks = new[]
            {
                "https://zicf.inven.co.kr/RealMedia/ads/click_hook1",
                "//zicf.inven.co.kr/RealMedia/ads/click_hook2"
            };

            pageMock.Setup(p => p.EvaluateAsync<string[]>(It.Is<string>(s => s.Contains("__ad_links")), null))
                .ReturnsAsync(hookLinks);
            pageMock.Setup(p => p.EvaluateAsync<string[]>(It.Is<string>(s => s.Contains("__ad_logs")), null))
                .ReturnsAsync(Array.Empty<string>());

            var collector = new PlaywrightAdCollector(_settings, _loggerMock.Object, _progress);

            var links = await collector.CollectLinksAsync(pageMock.Object, "https://test.inven.co.kr/", CancellationToken.None);

            Assert.That(links, Has.Count.EqualTo(2));
            Assert.That(links, Does.Contain("https://zicf.inven.co.kr/RealMedia/ads/click_hook1"));
            Assert.That(links, Does.Contain("https://zicf.inven.co.kr/RealMedia/ads/click_hook2"));
        }

        [Test]
        public async Task CollectLinksAsync_WhenIframeThrowsTimeout_DoesNotRethrowAndReturnsAvailableLinks()
        {
            var pageMock = new Mock<IPage>();
            pageMock.Setup(p => p.WaitForSelectorAsync("iframe", It.IsAny<PageWaitForSelectorOptions>()))
                .ThrowsAsync(new TimeoutException("iframe not found"));

            pageMock.SetupGet(p => p.Frames).Returns(new List<IFrame>());
            pageMock.Setup(p => p.EvaluateAsync<string[]>(It.Is<string>(s => s.Contains("__ad_links")), null))
                .ReturnsAsync(new[] { "https://zicf.inven.co.kr/RealMedia/ads/click_from_hook" });

            var collector = new PlaywrightAdCollector(_settings, _loggerMock.Object, _progress);

            var links = await collector.CollectLinksAsync(pageMock.Object, "https://test.inven.co.kr/", CancellationToken.None);

            Assert.That(links, Has.Count.EqualTo(1));
            Assert.That(links[0], Is.EqualTo("https://zicf.inven.co.kr/RealMedia/ads/click_from_hook"));
            _loggerMock.Verify(l => l.Debug(It.Is<string>(msg => msg.Contains("iframe 미검출"))), Times.Once);
        }

        [Test]
        public void CollectLinksAsync_WhenCancelled_ThrowsOperationCanceledException()
        {
            var pageMock = new Mock<IPage>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var collector = new PlaywrightAdCollector(_settings, _loggerMock.Object, _progress);

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await collector.CollectLinksAsync(pageMock.Object, "https://test.inven.co.kr/", cts.Token));
        }

        [Test]
        public async Task CollectLinksAsync_ResolvesFrameRelativeLinksAndRejectsEmbeddedAllowedUrls()
        {
            var page = new Mock<IPage>();
            var parent = new Mock<IFrame>();
            var frame = new Mock<IFrame>();
            frame.SetupGet(f => f.ParentFrame).Returns(parent.Object);
            frame.SetupGet(f => f.Url).Returns("https://zicf.inven.co.kr/RealMedia/ads/frame.html");
            frame.Setup(f => f.EvaluateAsync<string[]>(It.IsAny<string>(), null)).ReturnsAsync(new[]
            {
                "click_lx.ads/relative", "../click_parent", "/RealMedia/click_root",
                "//zicf.inven.co.kr/RealMedia/click_scheme", " https://zicf.inven.co.kr/RealMedia/click_root ",
                "#", "javascript:alert(1)", "/other/click", "https://zicf.inven.co.kr/RealMediaEvil/click",
                "https://evil.example/?url=https://zicf.inven.co.kr/RealMedia/click_fake",
                "https://zicf.inven.co.kr:444/RealMedia/click_fake",
                "https://user@zicf.inven.co.kr/RealMedia/click_fake"
            });
            page.SetupGet(p => p.Frames).Returns(new[] { parent.Object, frame.Object });
            var collector = new PlaywrightAdCollector(_settings, _loggerMock.Object, _progress);
            var links = await collector.CollectLinksAsync(page.Object, "https://test.inven.co.kr/", CancellationToken.None);
            Assert.That(links, Is.EquivalentTo(new[]
            {
                "https://zicf.inven.co.kr/RealMedia/ads/click_lx.ads/relative",
                "https://zicf.inven.co.kr/RealMedia/click_parent",
                "https://zicf.inven.co.kr/RealMedia/click_root",
                "https://zicf.inven.co.kr/RealMedia/click_scheme"
            }));
        }

        [Test]
        public async Task CollectLinksAsync_WhenHookHasRelativeLinks_UsesPageOriginAndDeduplicates()
        {
            var page = new Mock<IPage>();
            page.SetupGet(p => p.Frames).Returns(Array.Empty<IFrame>());
            page.Setup(p => p.EvaluateAsync<string[]>(It.Is<string>(script => script.Contains("__ad_links")), null))
                .ReturnsAsync(new[] { "/RealMedia/click_relative", "//zicf.inven.co.kr/RealMedia/click_absolute", "https://zicf.inven.co.kr/RealMedia/click_absolute" });
            var collector = new PlaywrightAdCollector(_settings, _loggerMock.Object, _progress);
            var links = await collector.CollectLinksAsync(page.Object, "https://test.inven.co.kr/", CancellationToken.None);
            Assert.That(links, Is.EqualTo(new[] { "https://zicf.inven.co.kr/RealMedia/click_absolute" }));
        }
    }
}
