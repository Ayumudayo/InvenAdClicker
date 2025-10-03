using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumWebBrowser : IDisposable
    {
        private readonly ChromeDriver _driver;
        private readonly ChromeDriverService _service;
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;
        private readonly Encryption _encryption;
        private readonly string _loginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";
        private short _instanceId;
        private int? _browserProcessId = null;

        public SeleniumWebBrowser(AppSettings settings, IAppLogger logger, Encryption encryption)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;

            _service = ChromeDriverService.CreateDefaultService();
            _service.SuppressInitialDiagnosticInformation = true;
            _service.HideCommandPromptWindow = true;

            var options = new ChromeOptions();
            options.AddArgument("--headless");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");

            _driver = new ChromeDriver(_service, options, TimeSpan.FromMilliseconds(settings.CommandTimeoutMilliSeconds));
            _browserProcessId = GetProcessIdByPort(GetDebuggerPort());
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public async Task LoginAsync(CancellationToken cancellationToken = default)
        {
            const string loginErrorMessage = "로그인 정보가 일치하지 않습니다.";

            _encryption.LoadAndValidateCredentials(out var id, out var pw);
            await Task.Run(() => _driver.Navigate().GoToUrl(_loginUrl), cancellationToken);

            var wait = new WebDriverWait(_driver, TimeSpan.FromMilliseconds(_settings.IframeTimeoutMilliSeconds));

            await Task.Run(() =>
            {
                wait.Until(ExpectedConditions.ElementIsVisible(By.Id("user_id"))).SendKeys(id);
                wait.Until(ExpectedConditions.ElementIsVisible(By.Id("password"))).SendKeys(pw);
                wait.Until(ExpectedConditions.ElementToBeClickable(By.Id("loginBtn"))).Click();
            }, cancellationToken);

            var loginWait = new WebDriverWait(_driver, TimeSpan.FromMilliseconds(_settings.PageLoadTimeoutMilliseconds));

            string loginState;
            try
            {
                loginState = loginWait.Until(driver =>
                {
                    var notice = driver.FindElements(By.CssSelector("#notice")).FirstOrDefault();
                    if (notice != null && notice.Text.Contains(loginErrorMessage, StringComparison.Ordinal))
                    {
                        return "invalid_credentials";
                    }

                    if (!driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return "redirected";
                    }

                    var modal = driver.FindElements(By.CssSelector(".modal-dialog")).FirstOrDefault();
                    if (modal != null)
                    {
                        return "modal";
                    }

                    return null;
                });
            }
            catch (WebDriverTimeoutException ex)
            {
                if (_driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ApplicationException("로그인에 실패했습니다. 자격증명을 확인해 주세요.", ex);
                }

                throw new ApplicationException("로그인 검증이 시간 초과로 실패했습니다.", ex);
            }

            if (loginState == "invalid_credentials")
            {
                throw new ApplicationException("로그인에 실패했습니다. 자격증명을 확인해 주세요.");
            }

            if (loginState == "modal")
            {
                DismissLoginModal();
                return;
            }

            if (loginState == "redirected")
            {
                return;
            }

            throw new ApplicationException("로그인 상태를 판별하지 못했습니다.");

            void DismissLoginModal()
            {
                if (TryClick("#btn-ok")) return;
                if (TryClick(".modal-footer .btn-ok")) return;
                if (TryClick(".modal-footer button")) return;
                if (TryClick(".modal-backdrop")) return;
                if (TryClick(".modal-dialog")) return;

                if (!TryClickBody())
                {
                    throw new ApplicationException("로그인 모달을 닫지 못했습니다.");
                }

                bool TryClick(string cssSelector)
                {
                    try
                    {
                        var element = _driver.FindElements(By.CssSelector(cssSelector)).FirstOrDefault();
                        if (element == null)
                        {
                            return false;
                        }
                        element.Click();
                        return true;
                    }
                    catch (WebDriverException)
                    {
                        try
                        {
                            ((IJavaScriptExecutor)_driver).ExecuteScript("document.querySelector(arguments[0])?.click();", cssSelector);
                            return true;
                        }
                        catch (WebDriverException)
                        {
                            return false;
                        }
                    }
                }

                bool TryClickBody()
                {
                    try
                    {
                        _driver.FindElement(By.TagName("body")).Click();
                        return true;
                    }
                    catch (WebDriverException)
                    {
                        try
                        {
                            ((IJavaScriptExecutor)_driver).ExecuteScript("document.body.click();");
                            return true;
                        }
                        catch (WebDriverException)
                        {
                            return false;
                        }
                    }
                }
            }

        }

        private int GetDebuggerPort()
        {
            if (_driver == null) return -1;
            var capabilities = (Dictionary<string, object>)_driver.Capabilities["goog:chromeOptions"];
            if (capabilities.TryGetValue("debuggerAddress", out var address) && address != null)
            {
                var portString = address?.ToString()?.Split(':').Last();
                if (portString != null && int.TryParse(portString, out var port))
                {
                    return port;
                }
            }
            return -1;
        }

        public void SetInstanceId(short id)
        {
            _instanceId = id;
        }

        public short InstanceId => _instanceId;

        public IWebDriver Driver => _driver;

        private int? GetProcessIdByPort(int port)
        {
            if (port <= 0) return null;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netstat.exe",
                        Arguments = "-a -n -o",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                string pattern = $@"TCP\s+.*?:{port}\s+.*?LISTENING\s+(\d+)";
                Match match = Regex.Match(output, pattern);

                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"포트 {port}의 프로세스 ID를 가져오지 못했습니다: {ex.Message}");
            }
            return null;
        }

        private bool _disposed = false;

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

            if (_browserProcessId.HasValue)
            {
                try
                {
                    var p = Process.GetProcessById(_browserProcessId.Value);
                    if (!p.HasExited)
                    {
                        p.Kill();
                    }
                }
                catch { /* 무시 */ }
            }

            _driver?.Quit();
            _driver?.Dispose();
            _service?.Dispose();
            _disposed = true;
        }
    }
}
