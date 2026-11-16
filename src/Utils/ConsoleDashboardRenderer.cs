using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    internal sealed class ConsoleDashboardRenderer
    {
        private const int RefreshRateMs = 100;

        public async Task RunAsync(Func<ProgressMetrics> capture, Func<bool> shouldStop, CancellationToken cancellationToken = default)
        {
            bool redirected = Console.IsOutputRedirected;

            if (capture().Rows.Count == 0)
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
                    var metrics = capture();
                    bool done = metrics.IsComplete;

                    if (!redirected)
                    {
                        lock (ConsoleLocker.Lock)
                        {
                            RenderFrame(metrics, startUtc, statusW, succW, rateW, etaW, thrdW, useAnsi, done, renderState);
                        }
                    }

                    if (shouldStop() || done || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(RefreshRateMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
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
                                var finalMetrics = capture();
                                bool finalDone = finalMetrics.IsComplete;

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

                                var finalFrame = BuildFrame(finalMetrics, startUtc, statusW, succW, rateW, etaW, thrdW, winW, reportH, finalDone);
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

        internal readonly struct FrameLine
        {
            public FrameLine(string text, ConsoleColor color)
            {
                Text = text;
                Color = color;
            }

            public string Text { get; }
            public ConsoleColor Color { get; }
        }

        private void RenderFrame(
            ProgressMetrics metrics,
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

            var frame = BuildFrame(metrics, startUtc, statusW, succW, rateW, etaW, thrdW, winW, winH, done);
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

        internal FrameLine[] BuildFrame(
            ProgressMetrics metrics,
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
                    frame[1] = new FrameLine(FitWithEllipsis($"URLs: {metrics.Rows.Count} | {runStateText}", winW), defaultColor);
                }
                return frame;
            }

            var rows = metrics.Rows;
            int totalAds = metrics.TotalAds, clicked = metrics.ClickedAds;
            int pending = metrics.PendingClicks, errors = metrics.Errors;

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

            var avgCps = metrics.AverageClicksPerSecond;
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

            string elapsed = (metrics.CapturedUtc - startUtc).TotalSeconds >= 0
                ? TimeSpan.FromSeconds((metrics.CapturedUtc - startUtc).TotalSeconds).ToString(@"mm\:ss")
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

            double cps = row.ClicksPerSecond;

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
