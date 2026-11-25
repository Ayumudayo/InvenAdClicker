using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Utils
{
    [TestFixture]
    public class PlaywrightLoginWorkflowTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task LoginAsync_RetriesTimeoutsWithExponentialDelay(bool playwrightTimeout)
        {
            var page = new Mock<IPage>(MockBehavior.Loose);
            Exception timeout = playwrightTimeout ? new PlaywrightException("Timeout 1000ms exceeded") : new TimeoutException();
            page.SetupSequence(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
                .ThrowsAsync(timeout).ThrowsAsync(timeout).ReturnsAsync((IResponse?)null);
            page.Setup(item => item.EvaluateAsync<string>(It.IsAny<string>(), It.IsAny<object>())).ReturnsAsync("redirected");
            var delays = new List<TimeSpan>();
            await PlaywrightLoginWorkflow.LoginAsync(page.Object, new AppSettings(), "id", "pw", CancellationToken.None,
                (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
            Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }));
            page.Verify(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Exactly(3));
        }

        [Test]
        public void LoginAsync_StopsAfterThreeTimeoutsAndRetainsCause()
        {
            var page = new Mock<IPage>();
            var timeout = new PlaywrightException("Timeout exceeded");
            page.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>())).ThrowsAsync(timeout);
            var error = Assert.ThrowsAsync<ApplicationException>(() => PlaywrightLoginWorkflow.LoginAsync(
                page.Object, new AppSettings(), "id", "pw", CancellationToken.None, (_, _) => Task.CompletedTask));
            Assert.That(error!.InnerException, Is.SameAs(timeout));
            page.Verify(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Exactly(3));
        }

        [TestCase("invalid_credentials")]
        [TestCase("pending")]
        public void LoginAsync_DoesNotRetryPermanentLoginFailures(string state)
        {
            var page = new Mock<IPage>();
            page.Setup(item => item.EvaluateAsync<string>(It.IsAny<string>(), It.IsAny<object>())).ReturnsAsync(state);
            Assert.ThrowsAsync<ApplicationException>(() => PlaywrightLoginWorkflow.LoginAsync(
                page.Object, new AppSettings(), "id", "pw", CancellationToken.None,
                (_, _) => throw new AssertionException("Permanent errors must not wait for a retry")));
            page.Verify(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Once);
        }

        [Test]
        public void LoginAsync_CancellationDuringBackoffStopsBeforeNextNavigation()
        {
            var page = new Mock<IPage>();
            page.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>())).ThrowsAsync(new TimeoutException());
            using var cancellation = new CancellationTokenSource();
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => PlaywrightLoginWorkflow.LoginAsync(
                page.Object, new AppSettings(), "id", "pw", cancellation.Token,
                (_, token) => { cancellation.Cancel(); return Task.Delay(1, token); }));
            page.Verify(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Once);
        }

        [Test]
        public void LoginAsync_CancelledTokenSkipsNavigation()
        {
            var page = new Mock<IPage>(MockBehavior.Strict);
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => PlaywrightLoginWorkflow.LoginAsync(
                page.Object, new AppSettings(), "id", "pw", new CancellationToken(true)));
            page.VerifyNoOtherCalls();
        }
    }
}
