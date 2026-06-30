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
