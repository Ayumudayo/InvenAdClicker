using InvenAdClicker.Models;
using Microsoft.Playwright;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public static class PlaywrightLoginWorkflow
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task<IPage> CreateAndLoginAsync(IBrowser browser, AppSettings settings, Encryption encryption, CancellationToken cancellationToken)
        {
            PlaywrightLoginHelper.LoadCredentials(encryption, out var id, out var pw);

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders
            });
            var page = await context.NewPageAsync();
            try
            {
                await LoginAsync(page, settings, id, pw, cancellationToken);
                return page;
            }
            catch
            {
                await page.Context.CloseAsync();
                throw;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task LoginAsync(IPage page, AppSettings settings, Encryption encryption, CancellationToken cancellationToken)
        {
            PlaywrightLoginHelper.LoadCredentials(encryption, out var id, out var pw);
            await LoginAsync(page, settings, id, pw, cancellationToken);
        }

        private static async Task LoginAsync(IPage page, AppSettings settings, string id, string pw, CancellationToken cancellationToken)
        {
            int attempts = 0;
            const int maxLoginAttempts = 3;
            var loginStateWaitScript = PlaywrightLoginHelper.LoginStateWaitScript;
            var loginStateEvaluationScript = PlaywrightLoginHelper.LoginStateEvaluationScript;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            while (true)
            {
                attempts++;
                try
                {
                    await page.GotoAsync(PlaywrightLoginHelper.LoginUrl, new PageGotoOptions { Timeout = settings.PageLoadTimeoutMilliseconds, WaitUntil = WaitUntilState.DOMContentLoaded });
                    await page.FillAsync("#user_id", id, new PageFillOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });
                    await page.FillAsync("#password", pw, new PageFillOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });
                    await page.ClickAsync("#loginBtn", new PageClickOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });

                    await page.WaitForFunctionAsync(loginStateWaitScript, null, new PageWaitForFunctionOptions { Timeout = (float)settings.PageLoadTimeoutMilliseconds });
                    var loginState = await page.EvaluateAsync<string>(loginStateEvaluationScript);

                    switch (loginState)
                    {
                        case "invalid_credentials":
                            throw new ApplicationException("로그인에 실패했습니다. 자격증명을 확인해 주세요.");
                        case "modal":
                            await PlaywrightLoginHelper.DismissLoginModalAsync(page, settings.CommandTimeoutMilliSeconds);
                            await page.WaitForFunctionAsync($"() => window.location.href !== '{PlaywrightLoginHelper.LoginUrl}'", null, new PageWaitForFunctionOptions { Timeout = (float)settings.PageLoadTimeoutMilliseconds });
                            return;
                        case "redirected":
                            return;
                        default:
                            throw new ApplicationException("로그인 상태를 판별하지 못했습니다.");
                    }
                }
                catch (TimeoutException ex)
                {
                    if (attempts >= maxLoginAttempts)
                    {
                        throw new ApplicationException("로그인 검증이 시간 초과로 실패했습니다.", ex);
                    }
                    await Task.Delay(1000, cts.Token);
                }
            }
        }
    }
}
