using InvenAdClicker.Utils;
using NUnit.Framework;
using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

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

        [TestCase("", "")]
        [TestCase("사용자", "암호🔐:한글")]
        public void SaveAndLoadCredentials_PreservesEmptyAndUnicodeValues(string expectedId, string expectedPassword)
        {
            var encryption = new Encryption(_tempFile);
            encryption.SaveCredentials(expectedId, expectedPassword);
            Assert.That(encryption.TryLoadCredentials(out var id, out var password), Is.True);
            Assert.That(id, Is.EqualTo(expectedId));
            Assert.That(password, Is.EqualTo(expectedPassword));
        }

        [Test]
        public void TryLoadCredentials_WhenDecryptedPayloadHasNoSeparator_ReturnsFalse()
        {
            File.WriteAllBytes(_tempFile, ProtectedData.Protect(Encoding.UTF8.GetBytes("no-separator"), null, DataProtectionScope.CurrentUser));
            var result = new Encryption(_tempFile).TryLoadCredentials(out var id, out var password);
            Assert.That(result, Is.False);
            Assert.That(id, Is.Empty);
            Assert.That(password, Is.Empty);
        }

        [Test]
        public void ReadPasswordMasked_HandlesBackspaceControlsAndEnterWithoutEchoingPassword()
        {
            var keys = new Queue<ConsoleKeyInfo>(new[]
            {
                new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false),
                new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false),
                new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
                new ConsoleKeyInfo('c', ConsoleKey.C, false, false, false),
                new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)
            });
            var output = new StringBuilder();
            var password = Encryption.ReadPasswordMasked(keys.Dequeue, value => output.Append(value));
            Assert.That(password, Is.EqualTo("ac"));
            Assert.That(output.ToString(), Is.EqualTo("**\b \b*" + Environment.NewLine));
        }
    }
}
