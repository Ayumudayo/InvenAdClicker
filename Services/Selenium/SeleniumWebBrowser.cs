using InvenAdClicker.Config;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumWebBrowser : IDisposable
    {
        private readonly ChromeDriver _driver;
        private readonly ILogger _logger;
        private readonly string _loginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";

        public SeleniumWebBrowser(AppSettings settings, ILogger logger, Encryption encryption)
        {
            _logger = logger;

            // ChromeDriverService 설정 ─────────────────────────
            var service = ChromeDriverService.CreateDefaultService();
            service.SuppressInitialDiagnosticInformation = true;  // 초기 진단 메시지 억제
            service.HideCommandPromptWindow = true;  // 드라이버 창 숨김
            service.LogPath = "NUL"; // Windows의 null 디바이스
            service.EnableVerboseLogging = false; // verbose 로깅도 끔

            // ChromeOptions 설정
            var options = new ChromeOptions();
            options.AddArguments(
                "--headless",
                "--incognito",
                "--disable-extensions",
                "--disable-gpu",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-browser-side-navigation",
                "--disable-infobars",
                "--disable-notifications",
                "--disable-popup-blocking",
                "--blink-settings=imagesEnabled=false",
                "--proxy-server='direct://'",
                "--proxy-bypass-list=*",
                "--disable-blink-features=AutomationControlled",
                "--log-level=3",
                "--disable-logging"
            );
            options.AddExcludedArgument("enable-logging");      // 브라우저/CEF 로그 제거
            options.AddExcludedArgument("enable-automation");   // 자동화 경고 제거
            options.AddArgument("--silent");                    // 추가 Silent 모드

            if (settings.DisableImages)
                options.AddUserProfilePreference(
                    "profile.managed_default_content_settings.images", 2);
            if (settings.DisableCss)
                options.AddUserProfilePreference(
                    "profile.managed_default_content_settings.stylesheets", 2);
            if (settings.DisableFonts)
                options.AddUserProfilePreference(
                    "profile.managed_default_content_settings.fonts", 2);

            //  ChromeDriver 인스턴스 생성
            _driver = new ChromeDriver(service, options);

            try
            {
                _driver.Navigate().GoToUrl(_loginUrl);

                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(settings.IframeTimeoutSeconds));
                wait.Until(ExpectedConditions.ElementExists(By.Id("user_id")));
                wait.Until(ExpectedConditions.ElementExists(By.Id("password")));
                wait.Until(ExpectedConditions.ElementExists(By.Id("loginBtn")));

                encryption.LoadAndValidateCredentials(out var id, out var pw);
                var idInput = _driver.FindElement(By.Id("user_id"));
                var pwInput = _driver.FindElement(By.Id("password"));
                idInput.Clear();
                idInput.SendKeys(id);
                pwInput.Clear();
                pwInput.SendKeys(pw);

                _driver.FindElement(By.Id("loginBtn")).Click();

                // 성공/실패 판정
                bool done = wait.Until(driver =>
                {
                    // A) URL 변경 → 로그인 성공
                    if (!driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // B) 실패 알림 확인: div#notice 안 텍스트
                    var notice = driver.FindElement(By.Id("notice"));
                    if (!string.IsNullOrEmpty(notice.Text)
                        && notice.Text.Contains("로그인 정보가 일치하지 않습니다."))
                    {
                        return true;
                    }

                    return false;
                });

                // 결과 처리
                if (!_driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info("로그인 성공: 페이지 리다이렉트로 확인됨.");
                }
                else
                {
                    var notice = _driver.FindElement(By.Id("notice"));
                    var msg = !string.IsNullOrEmpty(notice.Text)
                              ? notice.Text.Trim()
                              : "알 수 없는 로그인 오류";
                    _logger.Error($"로그인 실패: {msg}");
                    throw new InvalidOperationException($"Login failed: {msg}");
                }
            }
            catch (WebDriverTimeoutException ex)
            {
                _logger.Error("로그인 결과 확인 타임아웃", ex);
                throw;
            }
            finally
            {
            }
        }

        public IWebDriver Driver => _driver;

        public void Dispose()
        {
            try
            {
                _driver.Quit();
                _driver.Dispose();
                _logger.Info("Browser disposed.");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during browser dispose: {ex.Message}");
            }
        }
    }
}
