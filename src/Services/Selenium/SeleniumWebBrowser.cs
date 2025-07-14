using InvenAdClicker.Config;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumWebBrowser : IDisposable
    {
        private readonly ChromeDriver _driver;
        private readonly ChromeDriverService _service;
        private readonly ILogger _logger;
        private readonly string _loginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";        
        private short _instanceId;

        public SeleniumWebBrowser(AppSettings settings, ILogger logger, Encryption encryption)
        {
            try
            {
                _logger = logger;

                // ChromeDriverService 설정
                _service = ChromeDriverService.CreateDefaultService();
                _service.SuppressInitialDiagnosticInformation = true;  // 초기 진단 메시지 억제
                _service.HideCommandPromptWindow = true;
                _service.LogPath = "NUL";
                _service.EnableVerboseLogging = false;

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

                options.AddUserProfilePreference("profile.default_content_setting_values.sound", 2);

                if (settings.DisableImages)
                    options.AddUserProfilePreference(
                        "profile.managed_default_content_settings.images", 2);
                if (settings.DisableCss)
                    options.AddUserProfilePreference(
                        "profile.managed_default_content_settings.stylesheets", 2);
                if (settings.DisableFonts)
                    options.AddUserProfilePreference(
                        "profile.managed_default_content_settings.fonts", 2);

                _driver = new ChromeDriver(
                    _service,
                    options,
                    TimeSpan.FromMilliseconds(settings.CommandTimeoutMilliSeconds));

                encryption.LoadAndValidateCredentials(out var id, out var pw);
                _driver.Navigate().GoToUrl(_loginUrl);

                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(settings.IframeTimeoutSeconds));

                wait.Until(ExpectedConditions.ElementExists(By.Id("user_id")));
                wait.Until(ExpectedConditions.ElementExists(By.Id("password")));
                wait.Until(ExpectedConditions.ElementToBeClickable(By.Id("loginBtn")));

                _driver.FindElement(By.Id("user_id")).SendKeys(id);
                _driver.FindElement(By.Id("password")).SendKeys(pw);
                _driver.FindElement(By.Id("loginBtn")).Click();

                // 로그인 성공 또는 실패 판정
                try
                {
                    wait.Until(drv =>
                    {
                        // (A) 로그인 페이지에서 벗어나면 성공
                        if (!drv.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                            return true;

                        // (B) 실패 알림 텍스트 확인
                        var notice = drv.FindElement(By.Id("notice"));
                        return notice.Text.Contains("로그인 정보가 일치하지 않습니다.");
                    });
                }
                catch (WebDriverTimeoutException ex)
                {
                    _logger.Error("로그인 결과 확인 타임아웃", ex);
                    throw;
                }

                // 성공 vs 실패 후처리
                if (!_driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info("로그인 성공: 페이지 리다이렉트로 확인됨.");
                }
                else
                {
                    var noticeText = _driver.FindElement(By.Id("notice")).Text.Trim();
                    _logger.Error($"Instance #{_instanceId} | 로그인 실패: {noticeText}");
                    throw new InvalidOperationException($"Instance #{_instanceId} | Login failed: {noticeText}");
                }
            }
            catch (Exception)
            {
                // 생성자에서 예외 발생 시, 생성된 리소스를 즉시 정리
                _driver?.Quit();
                _service?.Dispose();
                // 예외를 다시 던져서 생성 실패를 알림
                throw;
            }
        }

        public void SetInstanceId(short id)
        {
            _instanceId = id;
        }

        public short InstanceId => _instanceId;

        public IWebDriver Driver => _driver;

        private bool _disposed = false;

        // Dtor 추가
        ~SeleniumWebBrowser()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // 관리 리소스 정리
            }

            // 비관리 리소스 정리
            try
            {
                _driver?.Quit(); // Quit이 모든 창을 닫고 드라이버 프로세스를 종료하므로 더 안전함
                _logger.Info($"Instance #{_instanceId} | Driver Quit.");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Instance #{_instanceId} | Exception during driver Quit: {ex.Message}");
            }
            finally
            {
                _driver?.Dispose();
            }

            try
            {
                _service?.Dispose();
                _logger.Info($"Instance #{_instanceId} | Service Disposed.");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Instance #{_instanceId} | Exception during service Dispose: {ex.Message}");
            }

            _disposed = true;
        }
    }
}
