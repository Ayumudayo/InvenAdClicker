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
            encryption.LoadAndValidateCredentials(out var id, out var pw);
            cancellationToken.ThrowIfCancellationRequested();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = PlaywrightLoginHelper.AcceptLanguageHeaders,
                JavaScriptEnabled = settings.Debug.Enabled ? settings.Debug.JavaScriptEnabled : true
            });
            try
            {
                var page = await context.NewPageAsync();
                await LoginAsync(page, settings, id, pw, cancellationToken);
                return page;
            }
            catch (Exception ex)
            {
                await ExceptionCleanup.CaptureAsync(ex, () => context.CloseAsync());
                throw;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task LoginAsync(IPage page, AppSettings settings, Encryption encryption, CancellationToken cancellationToken)
        {
            encryption.LoadAndValidateCredentials(out var id, out var pw);
            await LoginAsync(page, settings, id, pw, cancellationToken);
        }

        internal static async Task LoginAsync(IPage page, AppSettings settings, string id, string pw,
            CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            try
            {
                await AsyncRetry.ExecuteAsync(async _ =>
                {
                    await page.GotoAsync(PlaywrightLoginHelper.LoginUrl, new PageGotoOptions { Timeout = settings.PageLoadTimeoutMilliseconds, WaitUntil = WaitUntilState.DOMContentLoaded });
                    cancellationToken.ThrowIfCancellationRequested();
                    await page.FillAsync("#user_id", id, new PageFillOptions { Timeout = settings.CommandTimeoutMilliseconds });
                    await page.FillAsync("#password", pw, new PageFillOptions { Timeout = settings.CommandTimeoutMilliseconds });
                    await page.ClickAsync("#loginBtn", new PageClickOptions { Timeout = settings.CommandTimeoutMilliseconds });
                    await page.WaitForFunctionAsync(PlaywrightLoginHelper.LoginStateWaitScript, null, new PageWaitForFunctionOptions { Timeout = settings.PageLoadTimeoutMilliseconds });
                    var loginState = await page.EvaluateAsync<string>(PlaywrightLoginHelper.LoginStateEvaluationScript);
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (loginState)
                    {
                        case "invalid_credentials":
                            throw new ApplicationException("로그인에 실패했습니다. 자격증명을 확인해 주세요.");
                        case "modal":
                            await PlaywrightLoginHelper.DismissLoginModalAsync(page, settings.CommandTimeoutMilliseconds);
                            await page.WaitForFunctionAsync($"() => window.location.href !== '{PlaywrightLoginHelper.LoginUrl}'", null, new PageWaitForFunctionOptions { Timeout = settings.PageLoadTimeoutMilliseconds });
                            break;
                        case "redirected":
                            break;
                        default:
                            throw new ApplicationException("로그인 상태를 판별하지 못했습니다.");
                    }
                    return true;
                }, 3, TimeSpan.FromSeconds(1), IsTimeout, cancellationToken, delay: delay);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                throw new ApplicationException("로그인 검증이 시간 초과로 실패했습니다.", ex);
            }
        }

        private static bool IsTimeout(Exception ex) => ex is TimeoutException
            || (ex is PlaywrightException && ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase));
    }
}
