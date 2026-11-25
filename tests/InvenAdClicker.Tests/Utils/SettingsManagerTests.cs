using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Moq;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace InvenAdClicker.Tests.Utils
{
    [TestFixture]
    public class SettingsManagerTests
    {
        private Mock<IAppLogger> _loggerMock = null!;
        private string _directory = null!;
        private string SettingsPath => Path.Combine(_directory, "appsettings.json");

        [SetUp]
        public void SetUp()
        {
            _loggerMock = new Mock<IAppLogger>();
            _directory = Path.Combine(Path.GetTempPath(), "inven-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void ValidateRuntimeSettings_WhenClickDelayLessThan200_ThrowsInvalidOperationException()
        {
            var settings = new AppSettings
            {
                ClickDelayMilliseconds = 199,
                MaxDegreeOfParallelism = 2
            };
            var manager = new SettingsManager(settings, _loggerMock.Object);

            var ex = Assert.Throws<SettingsValidationException>(() => manager.ValidateRuntimeSettings());
            Assert.That(ex!.Message, Does.Contain("200ms 이상이어야 합니다"));
        }

        [Test]
        public void ValidateAndUpdateSettings_WhenClickDelayInvalid_DoesNotSwallowValidationFailure()
        {
            var manager = new SettingsManager(new AppSettings { ClickDelayMilliseconds = 100 }, _loggerMock.Object, SettingsPath);
            Assert.Throws<SettingsValidationException>(manager.ValidateAndUpdateSettings);
        }

        [TestCase("{invalid-json")]
        [TestCase("[]")]
        public void UpdateSchemaFile_WhenJsonInvalid_BacksUpOriginalAndRestoresDefaults(string original)
        {
            File.WriteAllText(SettingsPath, original);
            new SettingsManager(new AppSettings(), _loggerMock.Object, SettingsPath).UpdateSchemaFile();
            var backup = Directory.GetFiles(_directory, "appsettings.json.bak.*").Single();
            Assert.That(File.ReadAllText(backup), Is.EqualTo(original));
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
            Assert.That(root["AppSettings"]!["ClickDelayMilliseconds"]!.GetValue<int>(), Is.EqualTo(300));
        }

        [Test]
        public void UpdateSchemaFile_WhenMissing_CreatesDefaultsWithoutMutatingRuntimeSettings()
        {
            var settings = new AppSettings();
            new SettingsManager(settings, _loggerMock.Object, SettingsPath).UpdateSchemaFile();
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
            Assert.That(root["AppSettings"]!["TargetUrls"]!.AsArray(), Has.Count.EqualTo(3));
            Assert.That(settings.TargetUrls, Is.Empty);
        }

        [Test]
        public void UpdateSchemaFile_MigratesLegacyCasingPreservesValuesAndSynchronizesNestedKeys()
        {
            File.WriteAllText(SettingsPath, """
                {"appsettings":{"IframeTimeoutMilliSeconds":1234,"CommandTimeoutMilliSeconds":4321,
                "Debug":{"Headless":false,"Obsolete":true},"Obsolete":7},"Logging":{"LogLevel":"Debug"}}
                """);
            var manager = new SettingsManager(new AppSettings(), _loggerMock.Object, SettingsPath);
            manager.UpdateSchemaFile();
            var canonical = File.ReadAllText(SettingsPath);
            var root = JsonNode.Parse(canonical)!;
            var app = root["AppSettings"]!.AsObject();
            Assert.That(root.AsObject().ContainsKey("appsettings"), Is.False);
            Assert.That(app.ContainsKey("IframeTimeoutMilliSeconds"), Is.False);
            Assert.That(app["IframeTimeoutMilliseconds"]!.GetValue<int>(), Is.EqualTo(1234));
            Assert.That(app["CommandTimeoutMilliseconds"]!.GetValue<int>(), Is.EqualTo(4321));
            Assert.That(app.ContainsKey("Obsolete"), Is.False);
            Assert.That(app["Debug"]!.AsObject().ContainsKey("Obsolete"), Is.False);
            Assert.That(app["Debug"]!["Headless"]!.GetValue<bool>(), Is.False);
            Assert.That(app["Debug"]!["AllowFonts"]!.GetValue<bool>(), Is.False);
            Assert.That(root["Logging"]!["LogLevel"]!.GetValue<string>(), Is.EqualTo("Debug"));

            // Exercise the same configuration binder used at application startup.
            var bound = new ConfigurationBuilder().AddJsonFile(SettingsPath).Build().GetSection("AppSettings").Get<AppSettings>()!;
            Assert.That(bound.IframeTimeoutMilliseconds, Is.EqualTo(1234));
            Assert.That(bound.CommandTimeoutMilliseconds, Is.EqualTo(4321));
            manager.UpdateSchemaFile();
            Assert.That(File.ReadAllText(SettingsPath), Is.EqualTo(canonical));
        }

        [Test]
        public void UpdateSchemaFile_WhenBothLegacyAndCanonicalKeysExist_PrefersCanonicalValue()
        {
            File.WriteAllText(SettingsPath, """
                {"AppSettings":{"IframeTimeoutMilliSeconds":1234,"IframeTimeoutMilliseconds":2345}}
                """);
            new SettingsManager(new AppSettings(), _loggerMock.Object, SettingsPath).UpdateSchemaFile();
            var app = JsonNode.Parse(File.ReadAllText(SettingsPath))!["AppSettings"]!.AsObject();
            Assert.That(app["IframeTimeoutMilliseconds"]!.GetValue<int>(), Is.EqualTo(2345));
            Assert.That(app.ContainsKey("IframeTimeoutMilliSeconds"), Is.False);
        }

        [Test]
        public void ValidateRuntimeSettings_WhenDebugNull_ThrowsRequiredValidationError()
        {
            var settings = new AppSettings { Debug = null! };
            var error = Assert.Throws<SettingsValidationException>(() => new SettingsManager(settings, _loggerMock.Object).ValidateRuntimeSettings());
            Assert.That(error!.Message, Does.Contain("Debug"));
        }

        [Test]
        public void ValidateRuntimeSettings_NormalizesNullUrlsAndRejectsNonWebSchemes()
        {
            var settings = new AppSettings { TargetUrls = null! };
            var manager = new SettingsManager(settings, _loggerMock.Object);
            manager.ValidateRuntimeSettings();
            Assert.That(settings.TargetUrls, Is.Empty);
            settings.TargetUrls = new[] { "file:///C:/test", "javascript:alert(1)", " https://www.inven.co.kr/ ", "https://www.inven.co.kr/" };
            manager.ValidateRuntimeSettings();
            Assert.That(settings.TargetUrls, Is.EqualTo(new[] { "https://www.inven.co.kr/" }));
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
