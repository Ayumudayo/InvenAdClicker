using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [TestFixture, Category("BrowserIntegration")]
    public class ChromiumWorkflowIntegrationTests
    {
        [TestCase(false), TestCase(true)]
        public async Task PoolLogin_IsVerifiedOnce_AndWorkersCollectBothDynamicSamples(bool validCredentials)
        {
            if (Environment.GetEnvironmentVariable("INVEN_TEST_CHROMIUM") != "1")
                Assert.Ignore("Set INVEN_TEST_CHROMIUM=1 after installing this project's Chromium.");
            using var sandbox = new Sandbox();
            var encryption = new Encryption(Path.Combine(sandbox.Path, "credentials.dat"));
            encryption.SaveCredentials("synthetic-user", "synthetic-password");
            var settings = new AppSettings { MaxDegreeOfParallelism = 2, CollectionAttempts = 2, PostMessageBufferMilliseconds = 300 };
            await using var session = new PlaywrightSession(settings);
            await session.InitializeAsync(true, CancellationToken.None);
            const string home = "https://www.inven.co.kr/__iac_workflow_fixture";
            int loginRequests = 0, homeRequests = 0, contexts = 0;
            var createdContexts = new List<IBrowserContext>();
            async Task Serve(IRoute route)
            {
                string html;
                if (route.Request.Url == PlaywrightLoginHelper.LoginUrl)
                {
                    Interlocked.Increment(ref loginRequests);
                    string action = validCredentials
                        ? "document.cookie='synthetic_auth=yes;domain=.inven.co.kr;path=/;secure';location.href='" + home + "'"
                        : "document.querySelector('#notice').textContent=" + JsonSerializer.Serialize(PlaywrightLoginHelper.LoginErrorMessage);
                    html = "<input id='user_id'><input id='password' type='password'><div id='notice'></div><button id='loginBtn' onclick=\"" + WebUtility.HtmlEncode(action) + "\">Login</button>";
                }
                else if (route.Request.Url == home)
                {
                    int visit = Interlocked.Increment(ref homeRequests);
                    html = "<a href='/logout'>로그아웃</a><iframe src='https://zicf.inven.co.kr/RealMedia/__iac_fixture?visit=" + visit + "'></iframe>";
                }
                else if (route.Request.Url.StartsWith("https://zicf.inven.co.kr/RealMedia/__iac_fixture?visit=", StringComparison.Ordinal))
                {
                    string visit = new Uri(route.Request.Url).Query["?visit=".Length..];
                    html = $$"""
                        <body><a href='/RealMedia/click_static_{{visit}}'>static</a><script>
                        setTimeout(()=>parent.postMessage({clickurl:'https://zicf.inven.co.kr/RealMedia/click_message_{{visit}}'},'*'),30);
                        setTimeout(()=>{const a=document.createElement('a');a.href='/RealMedia/click_dynamic_{{visit}}';document.body.appendChild(a)},50);
                        </script>
                        """;
                }
                else { await route.AbortAsync(); return; }
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = "<!doctype html>" + html });
            }
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(() => session.Browser.IsConnected);
            browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions>())).Returns(async (BrowserNewContextOptions options) =>
            {
                Interlocked.Increment(ref contexts);
                var context = await session.Browser.NewContextAsync(options);
                createdContexts.Add(context);
                await context.RouteAsync("**/*", Serve);
                return context;
            });
            await using var pool = new PlaywrightBrowserPool(browser.Object, settings, new QuietLogger(), encryption);
            if (!validCredentials)
            {
                Assert.ThrowsAsync<ApplicationException>(() => pool.InitializePoolAsync());
                Assert.That(loginRequests, Is.EqualTo(1));
                Assert.That(contexts, Is.EqualTo(1), "A rejected login must not create worker contexts.");
                Assert.That(createdContexts[0].Pages, Is.Empty);
                return;
            }
            await pool.InitializePoolAsync();
            Assert.That(loginRequests, Is.EqualTo(1));
            Assert.That(contexts, Is.EqualTo(3));
            homeRequests = 0; // Ignore the successful login redirect.
            var page = await pool.AcquireAsync();
            try
            {
                Assert.That((await page.Context.CookiesAsync()).Any(c => c.Name == "synthetic_auth" && c.Value == "yes"), Is.True);
                // Fixture fulfillment must precede the production page's continue route.
                await page.RouteAsync("**/*", Serve);
                var progress = new ProgressTracker(); progress.Initialize(new[] { home });
                var links = await new PlaywrightAdCollector(settings, new QuietLogger(), progress).CollectLinksAsync(page, home, CancellationToken.None);
                Assert.That(homeRequests, Is.EqualTo(2));
                Assert.That(links, Is.EquivalentTo(from visit in new[] { 1, 2 } from kind in new[] { "static", "dynamic", "message" }
                    select $"https://zicf.inven.co.kr/RealMedia/click_{kind}_{visit}"));
            }
            finally { pool.Release(page); }
        }

        [Test]
        public async Task ProductionOrchestrator_LiveDryRun_RetainsAuthenticationAndSavedUserData()
        {
            string? data = Environment.GetEnvironmentVariable("INVEN_LIVE_DATA_DIRECTORY");
            if (string.IsNullOrEmpty(data)) Assert.Ignore("Set INVEN_LIVE_DATA_DIRECTORY to opt into a real Inven DryRun.");
            string sourceSettings = System.IO.Path.Combine(data!, "appsettings.json"), sourceCredentials = System.IO.Path.Combine(data!, "credentials.dat");
            var originalSettings = File.ReadAllBytes(sourceSettings); var originalCredentials = File.ReadAllBytes(sourceCredentials);
            var plain = ProtectedData.Unprotect(originalCredentials, null, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plain);
            using var sandbox = new Sandbox();
            var paths = new AppPaths(AppContext.BaseDirectory, sandbox.Path);
            var settings = JsonSerializer.Deserialize<AppSettings>(JsonDocument.Parse(originalSettings).RootElement.GetProperty("AppSettings").GetRawText())!;
            settings.DryRun = true;
            settings.TargetUrls = settings.TargetUrls.Take(5).ToArray();
            File.WriteAllBytes(paths.CredentialFilePath, originalCredentials);
            File.WriteAllText(paths.SettingsFilePath, JsonSerializer.Serialize(new { AppSettings = settings }));
            var rows = new ConcurrentBag<Observation>();
            var browsers = new ConcurrentBag<IBrowser>();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddInvenAdClicker(settings, paths: paths);
            services.AddSingleton<IAppLogger>(new QuietLogger());
            services.AddScoped<IAdCollector<IPage>>(p => new ObservingCollector(new PlaywrightAdCollector(settings,
                p.GetRequiredService<IAppLogger>(), p.GetRequiredService<ProgressTracker>()), rows, browsers));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var clock = Stopwatch.StartNew();
            int exitCode = await provider.GetRequiredService<AppOrchestrator>().RunAsync(stop.Token);
            bool unchanged = originalSettings.SequenceEqual(File.ReadAllBytes(sourceSettings)) && originalCredentials.SequenceEqual(File.ReadAllBytes(sourceCredentials));
            string report = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("INVEN_LIVE_REPORT") ?? "artifacts/chromium-optimization/live.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { ExitCode = exitCode, DurationMilliseconds = clock.ElapsedMilliseconds,
                settings.DryRun, settings.MaxDegreeOfParallelism, settings.CollectionAttempts, settings.PostMessageBufferMilliseconds,
                UserDataUnchanged = unchanged, BrowsersDisconnected = browsers.All(b => !b.IsConnected), Collections = rows.OrderBy(r => r.Url) }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(report);
            Assert.That(exitCode, Is.Zero);
            Assert.That(rows.Count, Is.EqualTo(settings.TargetUrls.Length));
            Assert.That(rows.All(row => row.Cookies > 0 && row.LogoutControls > 0), Is.True);
            Assert.That(unchanged, Is.True);
            Assert.That(browsers.All(b => !b.IsConnected), Is.True);
        }

        private sealed record Observation(string Url, int Links, int Cookies, int LogoutControls);
        private sealed class ObservingCollector(IAdCollector<IPage> inner, ConcurrentBag<Observation> rows, ConcurrentBag<IBrowser> browsers) : IAdCollector<IPage>
        {
            public async Task<List<string>> CollectLinksAsync(IPage page, string url, CancellationToken token)
            {
                browsers.Add(page.Context.Browser!);
                var links = await inner.CollectLinksAsync(page, url, token);
                int logout = 0;
                foreach (var frame in page.Frames)
                {
                    try { logout += await frame.EvaluateAsync<int>("() => Array.from(document.querySelectorAll('a,button')).filter(a=>/logout|로그아웃/i.test((a.getAttribute('href')||'')+' '+(a.getAttribute('onclick')||'')+' '+a.textContent+' '+Array.from(a.querySelectorAll('img')).map(i=>i.alt).join(' '))).length"); }
                    catch (PlaywrightException) { }
                }
                rows.Add(new Observation(url, links.Count, (await page.Context.CookiesAsync()).Count, logout));
                return links;
            }
        }
        private sealed class QuietLogger : IAppLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Debug(string message) { }
            public void Error(string message, Exception? error = null) { }
            public void Fatal(string message, Exception? error = null) { }
        }
        private sealed class Sandbox : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InvenAdClicker-chromium-test-" + Guid.NewGuid().ToString("N"));
            public Sandbox() => Directory.CreateDirectory(Path);
            public void Dispose()
            {
                string resolved = System.IO.Path.GetFullPath(Path);
                if (System.IO.Path.GetDirectoryName(resolved) != System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar)
                    || !System.IO.Path.GetFileName(resolved).StartsWith("InvenAdClicker-chromium-test-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the test sandbox.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
