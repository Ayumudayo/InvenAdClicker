using InvenAdClicker.Models;
using Microsoft.Playwright;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public static class LoginVerifier
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task VerifyPlaywrightAsync(IBrowser browser, AppSettings settings, IAppLogger logger, Encryption encryption, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var page = await PlaywrightLoginWorkflow.CreateAndLoginAsync(browser, settings, encryption, cts.Token);
            try
            {
                logger.Info("로그인 검증 성공");
            }
            finally
            {
                await page.Context.CloseAsync();
            }
        }
    }
}
