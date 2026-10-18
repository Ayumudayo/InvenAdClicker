using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Moq;
using NUnit.Framework;
using System;

namespace InvenAdClicker.Tests.Utils
{
    [TestFixture]
    public class SettingsManagerTests
    {
        private Mock<IAppLogger> _loggerMock = null!;

        [SetUp]
        public void SetUp()
        {
            _loggerMock = new Mock<IAppLogger>();
        }

        [Test]
        public void ValidateRuntimeSettings_WhenClickDelayLessThan200_ThrowsInvalidOperationException()
        {
            var settings = new AppSettings
            {
                ClickDelayMilliseconds = 199,
                MaxDegreeOfParallelism = 2
            };
            var manager = new SettingsManager(settings, _loggerMock.Object);

            var ex = Assert.Throws<InvalidOperationException>(() => manager.ValidateRuntimeSettings());
            Assert.That(ex!.Message, Does.Contain("200ms 이상이어야 합니다"));
        }

        [Test]
        public void ValidateRuntimeSettings_WhenClickDelayIs200OrMore_DoesNotThrow()
        {
            var settings = new AppSettings
            {
                ClickDelayMilliseconds = 250,
                MaxDegreeOfParallelism = 2
            };
            var manager = new SettingsManager(settings, _loggerMock.Object);

            Assert.DoesNotThrow(() => manager.ValidateRuntimeSettings());
        }

        [Test]
        public void ValidateRuntimeSettings_WhenMaxDegreeOfParallelismGreaterThan10_ClampsTo10()
        {
            var settings = new AppSettings
            {
                ClickDelayMilliseconds = 300,
                MaxDegreeOfParallelism = 20
            };
            var manager = new SettingsManager(settings, _loggerMock.Object);

            manager.ValidateRuntimeSettings();

            Assert.That(settings.MaxDegreeOfParallelism, Is.EqualTo(10));
            _loggerMock.Verify(l => l.Warn(It.Is<string>(msg => msg.Contains("10으로 제한"))), Times.Once);
        }

        [Test]
        public void ValidateRuntimeSettings_WhenValuesBelowMinimum_ClampsToMinimums()
        {
            var settings = new AppSettings
            {
                MaxDegreeOfParallelism = 0,
                IframeTimeoutMilliseconds = 50,
                PageLoadTimeoutMilliseconds = 500,
                CommandTimeoutMilliseconds = 500,
                PostMessageBufferMilliseconds = 100,
                CollectionAttempts = 0,
                MaxClickAttempts = 0,
                ClickDelayMilliseconds = 300
            };
            var manager = new SettingsManager(settings, _loggerMock.Object);

            manager.ValidateRuntimeSettings();

            Assert.That(settings.MaxDegreeOfParallelism, Is.EqualTo(1));
            Assert.That(settings.IframeTimeoutMilliseconds, Is.EqualTo(100));
            Assert.That(settings.PageLoadTimeoutMilliseconds, Is.EqualTo(1000));
            Assert.That(settings.CommandTimeoutMilliseconds, Is.EqualTo(1000));
            Assert.That(settings.PostMessageBufferMilliseconds, Is.EqualTo(200));
            Assert.That(settings.CollectionAttempts, Is.EqualTo(1));
            Assert.That(settings.MaxClickAttempts, Is.EqualTo(1));
        }

        [Test]
        public void ValidateRuntimeSettings_FiltersInvalidUrls_AndRemovesDuplicates()
        {
            var settings = new AppSettings
            {
                ClickDelayMilliseconds = 300,
                TargetUrls = new[]
                {
                    "https://www.inven.co.kr/",
                    "   ",
                    "not-a-valid-url",
                    "https://m.inven.co.kr/",
                    "https://www.inven.co.kr/" // duplicate
                }
            };
            var manager = new SettingsManager(settings, _loggerMock.Object);

            manager.ValidateRuntimeSettings();

            Assert.That(settings.TargetUrls, Has.Length.EqualTo(2));
            Assert.That(settings.TargetUrls, Is.EquivalentTo(new[] { "https://www.inven.co.kr/", "https://m.inven.co.kr/" }));
            _loggerMock.Verify(l => l.Warn(It.Is<string>(msg => msg.Contains("유효하지 않은 URL"))), Times.Once);
            _loggerMock.Verify(l => l.Warn(It.Is<string>(msg => msg.Contains("중복 URL"))), Times.Once);
        }
    }
}
