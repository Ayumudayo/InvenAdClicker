using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    internal static class PlaywrightLoginHelper
    {
        internal const string LoginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";
        internal const string LoginErrorMessage = "로그인 정보가 일치하지 않습니다.";

        internal static readonly string EscapedLoginErrorMessage = LoginErrorMessage.Replace("'", "\\'");
        internal static readonly KeyValuePair<string, string>[] AcceptLanguageHeaders =
        {
            new("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8")
        };

        internal static void LoadCredentials(Encryption encryption, out string id, out string password) =>
            encryption.LoadAndValidateCredentials(out id, out password);

        internal static readonly string LoginStateWaitScript = $@"() => {{
            const notice = document.querySelector('#notice');
            if (notice && notice.textContent.includes('{EscapedLoginErrorMessage}')) return 'invalid_credentials';
            if (window.location.href !== '{LoginUrl}') return 'redirected';
            if (document.querySelector('.modal-dialog')) return 'modal';
            return false;
        }}";

        internal static readonly string LoginStateEvaluationScript = $@"() => {{
            const notice = document.querySelector('#notice');
            if (notice && notice.textContent.includes('{EscapedLoginErrorMessage}')) return 'invalid_credentials';
            if (window.location.href !== '{LoginUrl}') return 'redirected';
            if (document.querySelector('.modal-dialog')) return 'modal';
            return 'pending';
        }}";

        internal static async Task DismissLoginModalAsync(IPage page, int commandTimeoutMilliseconds)
        {
            var clickOptions = new PageClickOptions { Timeout = (float)commandTimeoutMilliseconds };
            var elementClickOptions = new ElementHandleClickOptions { Timeout = (float)commandTimeoutMilliseconds };

            foreach (var selector in new[] { "#btn-ok", ".modal-footer .btn-ok", ".modal-footer button" })
            {
                try
                {
                    var handle = await page.QuerySelectorAsync(selector);
                    if (handle == null)
                    {
                        continue;
                    }

                    await handle.ClickAsync(elementClickOptions);
                    return;
                }
                catch (PlaywrightException)
                {
                    // 다음 후보 시도
                }
            }

            foreach (var selector in new[] { ".modal-backdrop", ".modal-dialog" })
            {
                try
                {
                    await page.ClickAsync(selector, clickOptions);
                    return;
                }
                catch (PlaywrightException)
                {
                    // 다음 후보 시도
                }
            }

            try
            {
                await page.ClickAsync("body", clickOptions);
            }
            catch (PlaywrightException ex)
            {
                throw new ApplicationException("로그인 모달을 닫지 못했습니다.", ex);
            }
        }
    }
}
