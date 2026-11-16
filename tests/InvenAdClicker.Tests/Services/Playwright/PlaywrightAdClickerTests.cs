using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [TestFixture]
    public class PlaywrightAdClickerTests
    {
        [Test]
        public void ClickAdAsync_PropagatesCancellationWithoutRenewingPage()
        {
            var page = new Mock<IPage>(MockBehavior.Strict);
            page.Setup(item => item.GotoAsync(
                    "https://example.test/ad",
                    It.IsAny<PageGotoOptions>()))
                .ReturnsAsync((IResponse?)null);

            var browserPool = new Mock<IBrowserPool<IPage>>(MockBehavior.Strict);
            browserPool.Setup(item => item.RenewAsync(page.Object, It.IsAny<CancellationToken>()))
                .ReturnsAsync(page.Object);

            var clicker = new PlaywrightAdClicker(
                new AppSettings
                {
                    MaxClickAttempts = 2,
                    ClickDelayMilliseconds = 1000,
                    PageLoadTimeoutMilliseconds = 1000
                },
                new TestLogger(),
                browserPool.Object);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync(
                Is.InstanceOf<OperationCanceledException>(),
                () => clicker.ClickAdAsync(page.Object, "https://example.test/ad", clickerId: 0, cts.Token));

            browserPool.Verify(
                item => item.RenewAsync(It.IsAny<IPage>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Test]
        public async Task ClickAdAsync_RenewsFailedPageThenReturnsSuccessfulReplacement()
        {
            var original = new Mock<IPage>(MockBehavior.Strict);
            original.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
                .ThrowsAsync(new PlaywrightException("Timeout exceeded"));
            var replacement = new Mock<IPage>(MockBehavior.Strict);
            replacement.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
                .ReturnsAsync((IResponse?)null);
            var pool = new Mock<IBrowserPool<IPage>>(MockBehavior.Strict);
            pool.Setup(item => item.RenewAsync(original.Object, It.IsAny<CancellationToken>())).ReturnsAsync(replacement.Object);
            var clicker = new PlaywrightAdClicker(new AppSettings { MaxClickAttempts = 2, ClickDelayMilliseconds = 0 }, new TestLogger(), pool.Object);

            var result = await clicker.ClickAdAsync(original.Object, "https://example.test/ad", 0, CancellationToken.None);

            Assert.That(result, Is.SameAs(replacement.Object));
            original.Verify(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Once);
            replacement.Verify(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Once);
            pool.Verify(item => item.RenewAsync(original.Object, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void ClickAdAsync_ExhaustedRetriesCloseReplacementUnknownToCaller()
        {
            var original = new Mock<IPage>(MockBehavior.Strict);
            original.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
                .ThrowsAsync(new PlaywrightException("first failure"));
            var replacement = new Mock<IPage>(MockBehavior.Strict);
            var context = new Mock<IBrowserContext>(MockBehavior.Strict);
            replacement.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
                .ThrowsAsync(new PlaywrightException("final failure"));
            replacement.SetupGet(item => item.Context).Returns(context.Object);
            context.Setup(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>())).Returns(Task.CompletedTask);
            var pool = new Mock<IBrowserPool<IPage>>(MockBehavior.Strict);
            pool.Setup(item => item.RenewAsync(original.Object, It.IsAny<CancellationToken>())).ReturnsAsync(replacement.Object);
            var clicker = new PlaywrightAdClicker(new AppSettings { MaxClickAttempts = 2 }, new TestLogger(), pool.Object);

            var exception = Assert.ThrowsAsync<PlaywrightException>(() => clicker.ClickAdAsync(original.Object,
                "https://example.test/ad", 0, CancellationToken.None));

            Assert.That(exception!.Message, Is.EqualTo("final failure"));
            context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
            pool.Verify(item => item.RenewAsync(It.IsAny<IPage>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        private sealed class TestLogger : IAppLogger
        {
            public void Debug(string msg)
            {
            }

            public void Error(string msg, Exception? ex = null)
            {
            }

            public void Fatal(string msg, Exception? ex = null)
            {
            }

            public void Info(string msg)
            {
            }

            public void Warn(string msg)
            {
            }
        }
    }
}
