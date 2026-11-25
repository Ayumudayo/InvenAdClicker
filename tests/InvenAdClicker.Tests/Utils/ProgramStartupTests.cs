using NUnit.Framework;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Utils
{
    public class ProgramStartupTests
    {
        [TestCase("--data-directory")]
        [TestCase("--data-directory", "")]
        [TestCase("--data-directory", "--smoke-test")]
        public async Task InvalidDataDirectoryOption_FailsBeforeStartingApplication(params string[] arguments)
        {
            Assert.That(await Program.Main(arguments), Is.EqualTo(2));
        }
    }
}
