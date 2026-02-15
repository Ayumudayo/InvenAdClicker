using Microsoft.Playwright;
using System.Runtime.CompilerServices;
using System.Threading;

namespace InvenAdClicker.Services.Playwright
{
    internal static class PlaywrightPageTelemetry
    {
        private static readonly ConditionalWeakTable<IPage, RouteStats> RouteStatsByPage = new();

        internal sealed class RouteStats
        {
            public long Continued;
            public long Aborted;

            public long ContinuedDocument;
            public long ContinuedScript;
            public long ContinuedXhr;
            public long ContinuedFetch;

            public long AbortedImage;
            public long AbortedStylesheet;
            public long AbortedFont;
            public long AbortedMedia;
            public long AbortedOther;
            public long AbortedWebSocket;
            public long AbortedEventSource;
            public long AbortedManifest;

            public long ContinuedUnknown;
            public long AbortedUnknown;
        }

        internal readonly struct RouteStatsSnapshot
        {
            public readonly long Continued;
            public readonly long Aborted;

            public readonly long ContinuedDocument;
            public readonly long ContinuedScript;
            public readonly long ContinuedXhr;
            public readonly long ContinuedFetch;

            public readonly long AbortedImage;
            public readonly long AbortedStylesheet;
            public readonly long AbortedFont;
            public readonly long AbortedMedia;
            public readonly long AbortedOther;
            public readonly long AbortedWebSocket;
            public readonly long AbortedEventSource;
            public readonly long AbortedManifest;

            public readonly long ContinuedUnknown;
            public readonly long AbortedUnknown;

            public RouteStatsSnapshot(RouteStats stats)
            {
                Continued = Interlocked.Read(ref stats.Continued);
                Aborted = Interlocked.Read(ref stats.Aborted);

                ContinuedDocument = Interlocked.Read(ref stats.ContinuedDocument);
                ContinuedScript = Interlocked.Read(ref stats.ContinuedScript);
                ContinuedXhr = Interlocked.Read(ref stats.ContinuedXhr);
                ContinuedFetch = Interlocked.Read(ref stats.ContinuedFetch);

                AbortedImage = Interlocked.Read(ref stats.AbortedImage);
                AbortedStylesheet = Interlocked.Read(ref stats.AbortedStylesheet);
                AbortedFont = Interlocked.Read(ref stats.AbortedFont);
                AbortedMedia = Interlocked.Read(ref stats.AbortedMedia);
                AbortedOther = Interlocked.Read(ref stats.AbortedOther);
                AbortedWebSocket = Interlocked.Read(ref stats.AbortedWebSocket);
                AbortedEventSource = Interlocked.Read(ref stats.AbortedEventSource);
                AbortedManifest = Interlocked.Read(ref stats.AbortedManifest);

                ContinuedUnknown = Interlocked.Read(ref stats.ContinuedUnknown);
                AbortedUnknown = Interlocked.Read(ref stats.AbortedUnknown);
            }

            public string FormatCompact()
            {
                // Keep this short; it appears in per-attempt perf logs.
                return
                    $"cont={Continued} abort={Aborted} " +
                    $"doc={ContinuedDocument} script={ContinuedScript} xhr={ContinuedXhr} fetch={ContinuedFetch} " +
                    $"img={AbortedImage} css={AbortedStylesheet} font={AbortedFont} media={AbortedMedia} other={AbortedOther} " +
                    $"ws={AbortedWebSocket} es={AbortedEventSource} mf={AbortedManifest} " +
                    $"u(cont={ContinuedUnknown},abort={AbortedUnknown})";
            }
        }

        internal static void ResetRouteStats(IPage page)
        {
            var stats = RouteStatsByPage.GetOrCreateValue(page);

            Interlocked.Exchange(ref stats.Continued, 0);
            Interlocked.Exchange(ref stats.Aborted, 0);

            Interlocked.Exchange(ref stats.ContinuedDocument, 0);
            Interlocked.Exchange(ref stats.ContinuedScript, 0);
            Interlocked.Exchange(ref stats.ContinuedXhr, 0);
            Interlocked.Exchange(ref stats.ContinuedFetch, 0);

            Interlocked.Exchange(ref stats.AbortedImage, 0);
            Interlocked.Exchange(ref stats.AbortedStylesheet, 0);
            Interlocked.Exchange(ref stats.AbortedFont, 0);
            Interlocked.Exchange(ref stats.AbortedMedia, 0);
            Interlocked.Exchange(ref stats.AbortedOther, 0);
            Interlocked.Exchange(ref stats.AbortedWebSocket, 0);
            Interlocked.Exchange(ref stats.AbortedEventSource, 0);
            Interlocked.Exchange(ref stats.AbortedManifest, 0);

            Interlocked.Exchange(ref stats.ContinuedUnknown, 0);
            Interlocked.Exchange(ref stats.AbortedUnknown, 0);
        }

        internal static RouteStatsSnapshot SnapshotRouteStats(IPage page)
        {
            var stats = RouteStatsByPage.GetOrCreateValue(page);
            return new RouteStatsSnapshot(stats);
        }

        internal static void RecordRouteDecision(IPage page, string? resourceType, bool continued)
        {
            var stats = RouteStatsByPage.GetOrCreateValue(page);
            if (continued)
            {
                Interlocked.Increment(ref stats.Continued);
            }
            else
            {
                Interlocked.Increment(ref stats.Aborted);
            }

            switch (resourceType)
            {
                case "document":
                    if (continued) Interlocked.Increment(ref stats.ContinuedDocument);
                    else Interlocked.Increment(ref stats.AbortedUnknown);
                    break;
                case "script":
                    if (continued) Interlocked.Increment(ref stats.ContinuedScript);
                    else Interlocked.Increment(ref stats.AbortedUnknown);
                    break;
                case "xhr":
                    if (continued) Interlocked.Increment(ref stats.ContinuedXhr);
                    else Interlocked.Increment(ref stats.AbortedUnknown);
                    break;
                case "fetch":
                    if (continued) Interlocked.Increment(ref stats.ContinuedFetch);
                    else Interlocked.Increment(ref stats.AbortedUnknown);
                    break;

                case "image":
                    if (!continued) Interlocked.Increment(ref stats.AbortedImage);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "stylesheet":
                    if (!continued) Interlocked.Increment(ref stats.AbortedStylesheet);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "font":
                    if (!continued) Interlocked.Increment(ref stats.AbortedFont);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "media":
                    if (!continued) Interlocked.Increment(ref stats.AbortedMedia);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "other":
                    if (!continued) Interlocked.Increment(ref stats.AbortedOther);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "websocket":
                    if (!continued) Interlocked.Increment(ref stats.AbortedWebSocket);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "eventsource":
                    if (!continued) Interlocked.Increment(ref stats.AbortedEventSource);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;
                case "manifest":
                    if (!continued) Interlocked.Increment(ref stats.AbortedManifest);
                    else Interlocked.Increment(ref stats.ContinuedUnknown);
                    break;

                default:
                    if (continued) Interlocked.Increment(ref stats.ContinuedUnknown);
                    else Interlocked.Increment(ref stats.AbortedUnknown);
                    break;
            }
        }
    }
}
