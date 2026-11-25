using InvenAdClicker.Models;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [Category("BrowserIntegration")]
    public class AdWaitBrowserTests
    {
        private const string Home = "https://test.inven.co.kr/__iac_wait_fixture";
        private const string FixtureHtml = "<!doctype html><html><head><meta charset='utf-8'></head><body><p>Delayed advertisement fixture</p></body></html>";

        [TestCase(200, true), TestCase(410, true), TestCase(410, false)]
        public async Task LateAds_KeepBothSamples_EvenWhenScriptRemovesItself(int status, bool iframe)
        {
            RequireBrowser();
            var settings = new AppSettings { CollectionAttempts = 2, IframeTimeoutMilliseconds = 1200, PostMessageBufferMilliseconds = 250 };
            await using var session = new PlaywrightSession(settings);
            await session.InitializeAsync(true, CancellationToken.None);
            await using var context = await session.Browser.NewContextAsync();
            int visits = 0;
            await context.AddInitScriptAsync("window.__ad_links=[];addEventListener('message',e=>{if(e.origin==='https://zicf.inven.co.kr'&&e.data.clickurl)window.__ad_links.push(e.data.clickurl)})");
            await context.RouteAsync("**/*", async r =>
            {
                string html;
                if (r.Request.Url == Home)
                {
                    int visit = ++visits;
                    string action = iframe
                        ? $"const f=document.createElement('iframe');f.src='https://zicf.inven.co.kr/RealMedia/frame?visit={visit}';document.body.appendChild(f)"
                        : $"window.__ad_links.push('https://zicf.inven.co.kr/RealMedia/late_{visit}')";
                    // The scheduling script disappears before its late ad arrives.
                    html = FixtureHtml.Replace("</body>", $"<script>setTimeout(()=>{{{action}}},800);document.currentScript.remove()</script></body>");
                }
                else if (r.Request.Url.StartsWith("https://zicf.inven.co.kr/RealMedia/frame?visit="))
                {
                    string visit = new Uri(r.Request.Url).Query["?visit=".Length..];
                    html = $$"""
                        <body><a href='/RealMedia/static_{{visit}}'>ad</a><script>
                        setTimeout(()=>{let a=document.createElement('a');a.href='/RealMedia/dynamic_{{visit}}';document.body.appendChild(a)},60);
                        setTimeout(()=>parent.postMessage({clickurl:'https://zicf.inven.co.kr/RealMedia/message_{{visit}}'},'*'),100);
                        </script></body>
                        """;
                }
                else { await r.AbortAsync(); return; }
                await r.FulfillAsync(new() { Status = r.Request.Url == Home ? status : 200, ContentType = "text/html", Body = html });
            });
            var page = await context.NewPageAsync();
            var progress = new ProgressTracker(); progress.Initialize(new[] { Home });
            var links = await new PlaywrightAdCollector(settings, Mock.Of<IAppLogger>(), progress).CollectLinksAsync(page, Home, CancellationToken.None);
            var expected = from visit in new[] { 1, 2 } from kind in (iframe ? new[] { "static", "dynamic", "message" } : new[] { "late" })
                           select $"https://zicf.inven.co.kr/RealMedia/{kind}_{visit}";
            Assert.That(visits, Is.EqualTo(2));
            Assert.That(links, Is.EquivalentTo(expected));
        }

        private static void RequireBrowser()
        {
            if (Environment.GetEnvironmentVariable("INVEN_TEST_CHROMIUM") != "1")
                Assert.Ignore("Set INVEN_TEST_CHROMIUM=1 after installing this project's Chromium.");
        }
    }
}
