using InvenAdClicker.Utils;
using InvenAdClicker.Models;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace InvenAdClicker.Tests.Utils
{
    [TestFixture]
    public class AppPathsTests
    {
        private string _root = null!;
        private string _deployment = null!;
        private string _working = null!;
        private AppPaths _paths = null!;
        private Mock<IAppLogger> _logger = null!;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "inven-path-tests-" + Guid.NewGuid().ToString("N"));
            _deployment = Path.Combine(_root, "deploy");
            _working = Path.Combine(_root, "working");
            Directory.CreateDirectory(_deployment);
            Directory.CreateDirectory(_working);
            _paths = new AppPaths(_deployment, Path.Combine(_root, "user"));
            _logger = new Mock<IAppLogger>();
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        [Test]
        public void Migration_PrefersDeploymentAndPreservesSourceBytesAndExistingTarget()
        {
            var original = new byte[] { 0, 255, 10, 13, 80 };
            File.WriteAllBytes(Path.Combine(_deployment, "credentials.dat"), original);
            File.WriteAllText(Path.Combine(_working, "credentials.dat"), "conflict");
            AppDataMigration.Prepare(_paths, _working, _logger.Object);
            Assert.That(File.ReadAllBytes(_paths.CredentialFilePath), Is.EqualTo(original));
            Assert.That(File.ReadAllBytes(Path.Combine(_deployment, "credentials.dat")), Is.EqualTo(original));
            _logger.Verify(logger => logger.Warn(It.Is<string>(message => message.Contains("충돌"))), Times.Once);
            File.WriteAllText(Path.Combine(_deployment, "credentials.dat"), "changed");
            AppDataMigration.Prepare(_paths, _working, _logger.Object);
            Assert.That(File.ReadAllBytes(_paths.CredentialFilePath), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(_paths.DataDirectory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void Migration_FallsBackToWorkingDirectoryAndNeverCopiesLogs()
        {
            File.WriteAllText(Path.Combine(_working, "appsettings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(_working, "logs"));
            File.WriteAllText(Path.Combine(_working, "logs", "old.log"), "original");
            AppDataMigration.Prepare(_paths, _working, _logger.Object);
            Assert.That(File.ReadAllText(_paths.SettingsFilePath), Is.EqualTo("{}"));
            Assert.That(Directory.GetFiles(_paths.LogDirectory), Is.Empty);
        }

        [Test]
        public void Migration_BlockedDestinationReportsPathWithoutFallback()
        {
            File.WriteAllText(_paths.DataDirectory, "blocked");
            var failure = Assert.Throws<IOException>(() => AppDataMigration.Prepare(_paths, _working, _logger.Object));
            Assert.That(failure!.Message, Does.Contain(_paths.DataDirectory));
            Assert.That(File.ReadAllText(_paths.DataDirectory), Is.EqualTo("blocked"));
        }

        [Test]
        public void Migration_LockedSourceFailsAndRetainsOriginal()
        {
            var source = Path.Combine(_deployment, "credentials.dat");
            File.WriteAllText(source, "protected");
            using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failure = Assert.Throws<IOException>(() => AppDataMigration.Prepare(_paths, _working, _logger.Object));
                Assert.That(failure!.Message, Does.Contain(source));
            }
            Assert.That(File.ReadAllText(source), Is.EqualTo("protected"));
            Assert.That(File.Exists(_paths.CredentialFilePath), Is.False);
        }

        [Test]
        public void Schema_ReadOnlyTargetReportsFailureAndRetainsBytes()
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            File.WriteAllText(_paths.SettingsFilePath, "{}");
            File.SetAttributes(_paths.SettingsFilePath, FileAttributes.ReadOnly);
            var manager = new SettingsManager(new AppSettings(), _logger.Object, _paths.SettingsFilePath);
            var failure = Assert.Throws<IOException>(manager.UpdateSchemaFile);
            Assert.That(failure!.Message, Does.Contain(_paths.SettingsFilePath));
            Assert.That(File.ReadAllText(_paths.SettingsFilePath), Is.EqualTo("{}"));
            Assert.That(Directory.GetFiles(_paths.DataDirectory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void Schema_LockedTargetReportsReadFailureWithoutBackupOrOverwrite()
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            File.WriteAllText(_paths.SettingsFilePath, "{}");
            using (File.Open(_paths.SettingsFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => new SettingsManager(new AppSettings(), _logger.Object, _paths.SettingsFilePath).UpdateSchemaFile());
            Assert.That(File.ReadAllText(_paths.SettingsFilePath), Is.EqualTo("{}"));
            Assert.That(Directory.GetFiles(_paths.DataDirectory, "*.bak.*"), Is.Empty);
        }

        [Test]
        public void Migration_DpapiCiphertextRemainsReadable()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("DPAPI requires Windows.");
            var source = Path.Combine(_deployment, "credentials.dat");
            new Encryption(source).SaveCredentials("user", "secret");
            AppDataMigration.Prepare(_paths, _working, _logger.Object);
            Assert.That(new Encryption(_paths.CredentialFilePath).TryLoadCredentials(out var id, out var password), Is.True);
            Assert.That(id, Is.EqualTo("user"));
            Assert.That(password, Is.EqualTo("secret"));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(File.ReadAllBytes(_paths.CredentialFilePath)));
        }

        [Test]
        public void ExplicitConfiguration_BindsUserFileWhenDeploymentTemplateIsMalformed()
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            File.WriteAllText(_paths.SettingsTemplatePath, "{malformed");
            File.WriteAllText(_paths.SettingsFilePath, "{\"AppSettings\":{\"ClickDelayMilliseconds\":456}}");
            var config = Program.BuildConfiguration(_paths.SettingsFilePath);
            Assert.That(config.GetSection("AppSettings").Get<AppSettings>()!.ClickDelayMilliseconds, Is.EqualTo(456));
        }
    }
}
