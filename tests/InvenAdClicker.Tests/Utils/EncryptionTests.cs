using InvenAdClicker.Utils;
using NUnit.Framework;
using System;
using System.IO;

namespace InvenAdClicker.Tests.Utils
{
    [TestFixture]
    public class EncryptionTests
    {
        private string _tempFile = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"enc_test_{Guid.NewGuid():N}.dat");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
            }
        }

        [Test]
        public void TryLoadCredentials_WhenFileDoesNotExist_ReturnsFalse()
        {
            var enc = new Encryption(_tempFile);

            var result = enc.TryLoadCredentials(out var id, out var pw);

            Assert.That(result, Is.False);
            Assert.That(id, Is.Empty);
            Assert.That(pw, Is.Empty);
        }

        [Test]
        public void SaveAndLoadCredentials_RoundTrip_RestoresIdAndPassword()
        {
            var enc = new Encryption(_tempFile);
            const string expectedId = "testUser123";
            const string expectedPw = "P@ssw0rd!#$";

            enc.SaveCredentials(expectedId, expectedPw);
            var result = enc.TryLoadCredentials(out var id, out var pw);

            Assert.That(result, Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
            Assert.That(pw, Is.EqualTo(expectedPw));
        }

        [Test]
        public void TryLoadCredentials_WhenFileContentIsCorrupted_ReturnsFalseWithoutThrowing()
        {
            var enc = new Encryption(_tempFile);
            File.WriteAllBytes(_tempFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            var result = enc.TryLoadCredentials(out var id, out var pw);

            Assert.That(result, Is.False);
            Assert.That(id, Is.Empty);
            Assert.That(pw, Is.Empty);
        }

        [Test]
        public void SaveAndLoadCredentials_WhenPasswordContainsColon_SplitsAtFirstColon()
        {
            var enc = new Encryption(_tempFile);
            const string expectedId = "user";
            const string expectedPw = "pass:with:colons";

            enc.SaveCredentials(expectedId, expectedPw);
            var result = enc.TryLoadCredentials(out var id, out var pw);

            Assert.That(result, Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
            Assert.That(pw, Is.EqualTo(expectedPw));
        }
    }
}
