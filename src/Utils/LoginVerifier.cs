using InvenAdClicker.Models;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public static class LoginVerifier
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task VerifyPlaywrightAsync(IBrowser browser, AppSettings settings, IAppLogger logger, Encryption encryption, CancellationToken cancellationToken)
        {
            encryption.LoadAndValidateCredentials(out var id, out var pw);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = new[] { new KeyValuePair<string, string>("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8") }
            });
            try
            {
                var page = await context.NewPageAsync();
                int attempts = 0;
                const int maxLoginAttempts = 3;
                var loginStateWaitScript = PlaywrightLoginHelper.LoginStateWaitScript;
                var loginStateEvaluationScript = PlaywrightLoginHelper.LoginStateEvaluationScript;
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
                                logger.Info("로그인 검증 성공");
                                return;
                            case "redirected":
                                logger.Info("로그인 검증 성공");
                                return;
                            default:
                                throw new ApplicationException("로그인 상태를 판별하지 못했습니다.");
                        }
                    }
                    catch (TimeoutException ex)
                    {
                        if (attempts >= maxLoginAttempts)
                        {
                            logger.Error("로그인 검증 시간 초과", ex);
                            throw new ApplicationException("로그인 검증이 시간 초과로 실패했습니다.", ex);
                        }
                        await Task.Delay(1000, cts.Token);
                    }
                }
            }
            finally
            {
                await context.CloseAsync();
            }
        }
    }
}
