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
                while (true)
                {
                    attempts++;
                    try
                    {
                        await page.GotoAsync("https://member.inven.co.kr/user/scorpio/mlogin", new PageGotoOptions { Timeout = settings.PageLoadTimeoutMilliseconds, WaitUntil = WaitUntilState.DOMContentLoaded });
                        await page.FillAsync("#user_id", id, new PageFillOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });
                        await page.FillAsync("#password", pw, new PageFillOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });
                        await page.ClickAsync("#loginBtn", new PageClickOptions { Timeout = (float)settings.CommandTimeoutMilliSeconds });

                        await page.WaitForFunctionAsync(@"() => {
                            const notice = document.querySelector('#notice');
                            return window.location.href !== 'https://member.inven.co.kr/user/scorpio/mlogin' || (notice && notice.textContent.includes('로그인 정보가 일치하지 않습니다.'));
                        }", new PageWaitForFunctionOptions { Timeout = (float)settings.PageLoadTimeoutMilliseconds });

                        if (page.Url.Contains("member.inven.co.kr"))
                            throw new ApplicationException("로그인에 실패했습니다. 자격증명을 확인해 주세요.");

                        logger.Info("로그인 검증 성공");
                        break;
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task VerifySeleniumAsync(AppSettings settings, IAppLogger logger, Encryption encryption, CancellationToken cancellationToken)
        {
            // SeleniumWebBrowser는 생성자에서 드라이버를 띄우고 LoginAsync에서 로그인하므로 이를 재사용
            var browser = new Services.Selenium.SeleniumWebBrowser(settings, logger, encryption);
            try
            {
                await browser.LoginAsync(cancellationToken);
                logger.Info("로그인 검증 성공");
            }
            finally
            {
                browser.Dispose();
            }
        }
    }
}

