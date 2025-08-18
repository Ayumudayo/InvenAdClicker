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
        private readonly ILogger _logger;
        private readonly AppSettings _settings;
        private readonly Encryption _encryption;
        private readonly string _loginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";
        private short _instanceId;
        private int? _browserProcessId = null;

        public SeleniumWebBrowser(AppSettings settings, ILogger logger, Encryption encryption)
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

        public async Task LoginAsync(CancellationToken cancellationToken = default)
        {
            _encryption.LoadAndValidateCredentials(out var id, out var pw);
            await Task.Run(() => _driver.Navigate().GoToUrl(_loginUrl), cancellationToken);

            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(_settings.IframeTimeoutSeconds));

            await Task.Run(() => {
                wait.Until(ExpectedConditions.ElementIsVisible(By.Id("user_id"))).SendKeys(id);
                wait.Until(ExpectedConditions.ElementIsVisible(By.Id("password"))).SendKeys(pw);
                wait.Until(ExpectedConditions.ElementToBeClickable(By.Id("loginBtn"))).Click();
            }, cancellationToken);

            try
            {
                wait.Until(drv => !drv.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase));
            }
            catch (WebDriverTimeoutException)
            {
                if (_driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ApplicationException("Login failed. Please check your credentials.");
                }
            }
        }

        private int GetDebuggerPort()
        {
            var capabilities = (Dictionary<string, object>)_driver.Capabilities["goog:chromeOptions"];
            if (capabilities.TryGetValue("debuggerAddress", out var address))
            {
                var portString = address.ToString().Split(':').Last();
                if (int.TryParse(portString, out var port))
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
                _logger.Error($"Failed to get process ID by port {port}: {ex.Message}");
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
                // managed resources
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
                catch { /* ignore */ }
            }

            _driver?.Quit();
            _driver?.Dispose();
            _service?.Dispose();
            _disposed = true;
        }
    }
}
