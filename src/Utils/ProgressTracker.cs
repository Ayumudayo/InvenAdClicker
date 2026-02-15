using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace InvenAdClicker.Utils
{
    public enum ProgressStatus
    {
        Waiting,
        Collecting,
        Collected,
        NoAds,
        Clicking,
        Finished,
        Error
    }

    public class ProgressInfo
    {
        public ProgressStatus Status { get; set; }
        public int Iteration { get; set; } = 0;
        public int TotalAds { get; set; } = 0;
        public int ClickedAds { get; set; } = 0;
        public int PendingClicks { get; set; } = 0;
        public int Errors { get; set; } = 0;
        public int Threads { get; set; } = 0;

        public DateTime? FirstUpdateUtc { get; set; }
        public DateTime? FirstClickUtc { get; set; }
        public DateTime? LastUpdateUtc { get; set; }

        public double? CompletedCps { get; set; }
    }

    public class ProgressTracker
    {
        private static readonly Lazy<ProgressTracker> _lazy =
            new Lazy<ProgressTracker>(() => new ProgressTracker());
        public static ProgressTracker Instance => _lazy.Value;

        private readonly ConcurrentDictionary<string, ProgressInfo> _map =
            new ConcurrentDictionary<string, ProgressInfo>();
        private volatile bool _shouldStopProgress = false;
        
        // Refresh rate control (max 10fps)
        private const int RefreshRateMs = 100;

        private ProgressTracker() { }

        public void Initialize(IEnumerable<string> urls)
        {
            foreach (var url in urls)
            {
                _map.TryAdd(url, new ProgressInfo { Status = ProgressStatus.Waiting });
            }
        }

        public void StopProgress() => _shouldStopProgress = true;

        public void Update(
            string url,
            ProgressStatus? status = null,
            int iterDelta = 0,
            int adsDelta = 0,
            int clickDelta = 0,
            int errDelta = 0,
            int threadDelta = 0,
            int pendingClicksDelta = 0)
        {
            var info = _map.GetOrAdd(url, _ => new ProgressInfo { Status = status ?? ProgressStatus.Waiting });

            lock (info)
            {
                var now = DateTime.UtcNow;
                var prevStatus = info.Status;
                if (status.HasValue)
                    info.Status = status.Value;

                info.Iteration += iterDelta;
                info.TotalAds += adsDelta;
                info.ClickedAds += clickDelta;
                info.Errors += errDelta;
                info.Threads = Math.Max(0, info.Threads + threadDelta);
                info.PendingClicks += pendingClicksDelta;

                info.LastUpdateUtc = now;
                if (!info.FirstUpdateUtc.HasValue)
                    info.FirstUpdateUtc = now;
                if (clickDelta > 0 && !info.FirstClickUtc.HasValue)
                    info.FirstClickUtc = now;

                bool becameFinished = false;
                if (info.Status == ProgressStatus.Clicking && info.PendingClicks <= 0)
                {
                    info.Status = ProgressStatus.Finished;
                    becameFinished = true;
                }
                if (status.HasValue && status.Value == ProgressStatus.Finished && prevStatus != ProgressStatus.Finished)
                {
                    becameFinished = true;
                }

                if (becameFinished && info.FirstClickUtc.HasValue && !info.CompletedCps.HasValue)
                {
                    var elapsedSec = Math.Max(1.0, (now - info.FirstClickUtc.Value).TotalSeconds);
                    info.CompletedCps = info.ClickedAds / elapsedSec;
                }
            }
        }

        public void PrintProgress()
        {
            bool redirected = Console.IsOutputRedirected;
            
            if (_map.IsEmpty)
            {
                lock (ConsoleLocker.Lock)
                {
                    Console.WriteLine("No target URLs. Exiting.");
                }
                return;
            }

            int statusW = Math.Max("Status".Length, Enum.GetNames(typeof(ProgressStatus)).Max(s => s.Length)) + 2;
            int rateW = 8;
            int succW = 8;
            int etaW = 10;
            int thrdW = 6;
            var urls = _map.Keys.OrderBy(k => k).ToList();
            var startUtc = DateTime.UtcNow;
            var renderState = new RenderState();

            bool useAnsi = false;
            uint originalMode = 0;
            if (!redirected)
            {
                useAnsi = ConsoleVirtualTerminal.TryEnable(out originalMode);
                lock (ConsoleLocker.Lock)
                {
                    if (useAnsi)
                    {
                        Console.Write(Ansi.EnterAltBuffer);
                        Console.Write(Ansi.HideCursor);
                        Console.Write(Ansi.Home);
                        Console.Write(Ansi.ClearScreen);
                    }
                    else
                    {
                        try { Console.CursorVisible = false; } catch { }
                        try { Console.Clear(); } catch { }
                    }
                }
            }

            try
            {
                while (true)
                {
                    bool done = _map.Values.All(info =>
                        info.Status == ProgressStatus.Finished || info.Status == ProgressStatus.Error || info.Status == ProgressStatus.NoAds);

                    if (!redirected)
                    {
                        lock (ConsoleLocker.Lock)
                        {
                            RenderFrame(urls, startUtc, statusW, succW, rateW, etaW, thrdW, useAnsi, done, renderState);
                        }
                    }

                    if (_shouldStopProgress || done)
                    {
                        break;
                    }

                    Thread.Sleep(RefreshRateMs);
                }
            }
            finally
            {
                if (!redirected)
                {
                    lock (ConsoleLocker.Lock)
                    {
                        if (useAnsi)
                        {
                            // Leave the alternate buffer, then print a final snapshot to the normal buffer
                            // so the URL list remains visible after completion.
                            Console.Write(Ansi.ShowCursor);
                            Console.Write(Ansi.ExitAltBuffer);

                            try
                            {
                                bool finalDone = _map.Values.All(info =>
                                    info.Status == ProgressStatus.Finished || info.Status == ProgressStatus.Error || info.Status == ProgressStatus.NoAds);

                                int winW = 80;
                                int winH = 25;
                                try
                                {
                                    winW = Console.WindowWidth;
                                    winH = Console.WindowHeight;
                                }
                                catch { }

                                // Reserve a few lines for Program.cs final messages (runtime/prompt).
                                int reservedBottomLines = 4;
                                int reportH = Math.Max(5, winH - reservedBottomLines);

                                var finalFrame = BuildFrame(urls, startUtc, statusW, succW, rateW, etaW, thrdW, winW, reportH, finalDone);
                                for (int i = 0; i < finalFrame.Length; i++)
                                {
                                    Console.ForegroundColor = finalFrame[i].Color;
                                    Console.WriteLine(finalFrame[i].Text);
                                }
                                Console.ResetColor();
                            }
                            catch
                            {
                                // Best-effort: never fail the app on UI snapshot.
                                try { Console.ResetColor(); } catch { }
                            }
                        }
                        else
                        {
                            try { Console.ResetColor(); } catch { }
                            try { Console.CursorVisible = true; } catch { }
                        }
                    }
                }

                if (useAnsi)
                {
                    ConsoleVirtualTerminal.TryRestore(originalMode);
                }
            }
        }

        private sealed class RenderState
        {
            public int Width = -1;
            public int Height = -1;
            public FrameLine[]? LastFrame;
        }

        private readonly struct FrameLine
        {
            public FrameLine(string text, ConsoleColor color)
            {
                Text = text;
                Color = color;
            }

            public string Text { get; }
            public ConsoleColor Color { get; }
        }

        private sealed class ProgressRowSnapshot
        {
            public string Url = string.Empty;
            public ProgressStatus Status;
            public int Iteration;
            public int TotalAds;
            public int ClickedAds;
            public int PendingClicks;
            public int Errors;
            public int Threads;
            public double? CompletedCps;
            public DateTime? FirstClickUtc;
        }

        private static int GetStatusGroup(ProgressStatus status)
        {
            return status switch
            {
                ProgressStatus.Collecting => 0,
                ProgressStatus.Clicking => 0,
                ProgressStatus.Collected => 1,
                ProgressStatus.Waiting => 1,
                ProgressStatus.Error => 2,
                ProgressStatus.Finished => 3,
                ProgressStatus.NoAds => 3,
                _ => 3,
            };
        }

        private void RenderFrame(
            List<string> urls,
            DateTime startUtc,
            int statusW,
            int succW,
            int rateW,
            int etaW,
            int thrdW,
            bool useAnsi,
            bool done,
            RenderState state)
        {
            int winW = 80;
            int winH = 25;
            try
            {
                winW = Console.WindowWidth;
                winH = Console.WindowHeight;
            }
            catch
            {
                // Non-interactive or window size unavailable.
            }

            if (winW <= 0 || winH <= 0)
            {
                return;
            }

            bool sizeChanged = winW != state.Width || winH != state.Height;
            if (sizeChanged)
            {
                state.Width = winW;
                state.Height = winH;
                state.LastFrame = null;

                // Resizing causes console line reflow; do a one-time clear on size change.
                FullClear(useAnsi);
            }

            var frame = BuildFrame(urls, startUtc, statusW, succW, rateW, etaW, thrdW, winW, winH, done);
            ApplyDiff(frame, state.LastFrame, useAnsi);
            state.LastFrame = frame;
        }

        private static void FullClear(bool useAnsi)
        {
            try { Console.SetCursorPosition(0, 0); } catch { }
            if (useAnsi)
            {
                Console.Write(Ansi.Home);
                Console.Write(Ansi.ClearScreen);
            }
            else
            {
                try { Console.Clear(); } catch { }
            }
        }

        private FrameLine[] BuildFrame(
            List<string> urls,
            DateTime startUtc,
            int statusW,
            int succW,
            int rateW,
            int etaW,
            int thrdW,
            int winW,
            int winH,
            bool done)
        {
            var frame = new FrameLine[winH];
            string blank = winW > 0 ? new string(' ', winW) : string.Empty;
            var defaultColor = ConsoleColor.Gray;

            for (int i = 0; i < winH; i++)
            {
                frame[i] = new FrameLine(blank, defaultColor);
            }

            if (winW <= 0 || winH <= 0)
            {
                return frame;
            }

            // If the window is extremely small, show a minimal view.
            if (winW < 40 || winH < 5)
            {
                var msg = $"Console window too small ({winW}x{winH}). Resize to view progress.";
                frame[0] = new FrameLine(FitWithEllipsis(msg, winW), defaultColor);
                if (winH > 1)
                {
                    var runStateText = done ? "Done" : "Running";
                    frame[1] = new FrameLine(FitWithEllipsis($"URLs: {urls.Count} | {runStateText}", winW), defaultColor);
                }
                return frame;
            }

            // Build per-URL snapshots (single pass, consistent frame)
            var rows = new List<ProgressRowSnapshot>(urls.Count);
            int totalAds = 0, clicked = 0, pending = 0, errors = 0;
            var cpsList = new List<double>(urls.Count);

            foreach (var url in urls)
            {
                if (!_map.TryGetValue(url, out var info))
                {
                    continue;
                }

                var row = new ProgressRowSnapshot { Url = url };
                lock (info)
                {
                    row.Status = info.Status;
                    row.Iteration = info.Iteration;
                    row.TotalAds = info.TotalAds;
                    row.ClickedAds = info.ClickedAds;
                    row.PendingClicks = info.PendingClicks;
                    row.Errors = info.Errors;
                    row.Threads = info.Threads;
                    row.CompletedCps = info.CompletedCps;
                    row.FirstClickUtc = info.FirstClickUtc;

                    totalAds += info.TotalAds;
                    clicked += info.ClickedAds;
                    pending += info.PendingClicks;
                    errors += info.Errors;

                    double cps = 0.0;
                    if (info.Status == ProgressStatus.Finished)
                    {
                        cps = info.CompletedCps ?? 0.0;
                    }
                    else if (info.FirstClickUtc.HasValue)
                    {
                        var elapsedSec = Math.Max(1.0, (DateTime.UtcNow - info.FirstClickUtc.Value).TotalSeconds);
                        cps = info.ClickedAds / elapsedSec;
                    }
                    if (cps > 0) cpsList.Add(cps);
                }

                rows.Add(row);
            }

            rows.Sort((a, b) =>
            {
                int ga = GetStatusGroup(a.Status);
                int gb = GetStatusGroup(b.Status);
                int g = ga.CompareTo(gb);
                if (g != 0) return g;
                return string.Compare(a.Url, b.Url, StringComparison.Ordinal);
            });

            int headerLines = 2;
            int footerLines = 2;
            int maxRows = Math.Max(0, winH - headerLines - footerLines);
            int shown = Math.Min(rows.Count, maxRows);
            int hidden = rows.Count - shown;

            int sepW = 1;
            int baseWidth = sepW + statusW + 8 + 8 + 8 + succW + rateW + 9 + etaW + 6 + thrdW;
            int urlW = Math.Max(8, winW - baseWidth);

            string header =
                "URL".PadRight(urlW) +
                new string(' ', sepW) +
                "Status".PadRight(statusW) +
                "Iter".PadRight(8) +
                "Ads".PadRight(8) +
                "Clicked".PadRight(8) +
                "Success".PadRight(succW) +
                "Speed".PadRight(rateW) +
                "Pending".PadRight(9) +
                "ETA".PadRight(etaW) +
                "Err".PadRight(6) +
                "Thrd".PadRight(thrdW);

            frame[0] = new FrameLine(FitExact(header, winW), defaultColor);
            frame[1] = new FrameLine(new string('-', winW), defaultColor);

            for (int i = 0; i < shown; i++)
            {
                var (color, line) = FormatProgressLine(rows[i], urlW, statusW, succW, rateW, etaW, thrdW, winW);
                frame[headerLines + i] = new FrameLine(line, color);
            }

            // Fill remaining row slots to keep footer anchored.
            for (int i = shown; i < maxRows; i++)
            {
                frame[headerLines + i] = new FrameLine(blank, defaultColor);
            }

            var avgCps = cpsList.Count > 0 ? cpsList.Average() : 0.0;
            string rate = avgCps > 0 ? $"{avgCps:0.00}c/s" : "-";
            string succ = totalAds > 0 ? ($"{(clicked * 100.0 / Math.Max(1, totalAds)):0.#}%") : "-";
            string eta = "-";
            if (avgCps > 0 && pending > 0)
            {
                var sec = pending / avgCps;
                var ts = TimeSpan.FromSeconds(sec);
                eta = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
            }

            string summary = $"Summary | Ads:{totalAds} Clicked:{clicked} Pending:{pending} Err:{errors} | Success:{succ} Speed:{rate} ETA:{eta}";
            int summaryRow = headerLines + maxRows;
            frame[summaryRow] = new FrameLine(FitWithEllipsis(summary, winW), defaultColor);

            string elapsed = (DateTime.UtcNow - startUtc).TotalSeconds >= 0
                ? TimeSpan.FromSeconds((DateTime.UtcNow - startUtc).TotalSeconds).ToString(@"mm\:ss")
                : "--:--";

            string hint;
            if (hidden > 0)
            {
                hint = $"Showing {shown}/{rows.Count} URLs (+{hidden} hidden) | Elapsed {elapsed}";
            }
            else
            {
                hint = $"Showing {shown}/{rows.Count} URLs | Elapsed {elapsed}";
            }
            if (done)
            {
                hint = hint + " | Done";
            }

            frame[summaryRow + 1] = new FrameLine(FitWithEllipsis(hint, winW), defaultColor);
            return frame;
        }

        private static void ApplyDiff(FrameLine[] frame, FrameLine[]? last, bool useAnsi)
        {
            int h = frame.Length;
            for (int i = 0; i < h; i++)
            {
                bool dirty = last == null || i >= last.Length ||
                             !string.Equals(frame[i].Text, last[i].Text, StringComparison.Ordinal) ||
                             frame[i].Color != last[i].Color;

                if (!dirty)
                {
                    continue;
                }

                if (!TrySetCursor(0, i))
                {
                    // If cursor positioning fails (e.g. user resized mid-frame), skip and retry next tick.
                    continue;
                }

                Console.ForegroundColor = frame[i].Color;
                Console.Write(frame[i].Text);
                Console.ResetColor();
            }
        }

        private static bool TrySetCursor(int left, int top)
        {
            try
            {
                Console.SetCursorPosition(left, top);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FitExact(string s, int width)
        {
            if (width <= 0) return string.Empty;
            if (s.Length == width) return s;
            if (s.Length > width) return s.Substring(0, width);
            return s.PadRight(width);
        }

        private static (ConsoleColor Color, string Line) FormatProgressLine(
            ProgressRowSnapshot row,
            int urlW,
            int statusW,
            int succW,
            int rateW,
            int etaW,
            int thrdW,
            int tableWidth)
        {
            var status = row.Status;
            int total = row.TotalAds;
            int clicked = row.ClickedAds;
            int pending = row.PendingClicks;
            int errors = row.Errors;
            int threads = row.Threads;
            int iter = row.Iteration;
            double? completedCps = row.CompletedCps;
            DateTime? firstClick = row.FirstClickUtc;

            var color = status switch
            {
                ProgressStatus.Collecting => ConsoleColor.Yellow,
                ProgressStatus.Collected => ConsoleColor.Magenta,
                ProgressStatus.Clicking => ConsoleColor.Green,
                ProgressStatus.Finished => ConsoleColor.Cyan,
                ProgressStatus.Error => ConsoleColor.Red,
                _ => ConsoleColor.DarkGray,
            };

            string statusText = status.ToString();
            string succ = total > 0 ? ($"{(clicked * 100.0 / Math.Max(1, total)):0.#}%") : "-";

            double cps = 0.0;
            if (status == ProgressStatus.Finished)
            {
                cps = completedCps ?? 0.0;
            }
            else if (firstClick.HasValue)
            {
                var elapsedSec = Math.Max(1.0, (DateTime.UtcNow - firstClick.Value).TotalSeconds);
                cps = clicked / elapsedSec;
            }

            string rate = cps > 0 ? $"{cps:0.00}c/s" : "-";
            string eta = "-";
            if (cps > 0 && pending > 0)
            {
                var sec = pending / cps;
                var ts = TimeSpan.FromSeconds(sec);
                eta = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
            }

            string urlCell = FitWithEllipsis(row.Url, urlW);
            string line =
                $"{urlCell} " +
                $"{statusText.PadRight(statusW)}" +
                $"{iter.ToString().PadRight(8)}" +
                $"{total.ToString().PadRight(8)}" +
                $"{clicked.ToString().PadRight(8)}" +
                $"{succ.PadRight(succW)}" +
                $"{rate.PadRight(rateW)}" +
                $"{pending.ToString().PadRight(9)}" +
                $"{eta.PadRight(etaW)}" +
                $"{errors.ToString().PadRight(6)}" +
                $"{threads.ToString().PadRight(thrdW)}";

            if (line.Length >= tableWidth)
            {
                line = line.Substring(0, Math.Max(0, tableWidth));
            }
            else
            {
                line = line.PadRight(tableWidth);
            }

            return (color, line);
        }

        private static string FitWithEllipsis(string s, int width)
        {
            if (width <= 0) return string.Empty;
            if (s.Length <= width) return s.PadRight(width);
            if (width == 1) return "…";
            return s.Substring(0, Math.Max(0, width - 1)) + "…";
        }
    }
}
