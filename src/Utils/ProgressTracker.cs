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

        private static bool TrySetCursor(int left, int top)
        {
            try
            {
                if (left >= 0 && top >= 0 && top < Console.BufferHeight)
                {
                    Console.SetCursorPosition(left, top);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
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
            int headerLines = 2;
            var startUtc = DateTime.UtcNow;

            if (!redirected)
            {
                lock (ConsoleLocker.Lock)
                {
                    Console.Clear();
                    int sepW = 1;
                    int baseWidth = sepW + statusW + 8 + 8 + 8 + succW + rateW + 9 + etaW + 6 + thrdW;
                    int urlW = Math.Max(8, Console.WindowWidth - baseWidth);
                    
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
                    
                    Console.WriteLine(FitWithEllipsis(header, Console.WindowWidth));
                    Console.WriteLine(new string('-', Console.WindowWidth));
                    
                    for (int i = 0; i < urls.Count + 2; i++) Console.WriteLine();
                }
            }

            while (true)
            {
                if (!redirected)
                {
                    int winW = 80;
                    try { winW = Console.WindowWidth; } catch { }

                    int sepW = 1;
                    int baseWidth = sepW + statusW + 8 + 8 + 8 + succW + rateW + 9 + etaW + 6 + thrdW;
                    int urlW = Math.Max(8, winW - baseWidth);

                    lock (ConsoleLocker.Lock)
                    {
                        for (int i = 0; i < urls.Count; i++)
                        {
                            var url = urls[i];
                            if (_map.TryGetValue(url, out var info))
                            {
                                TrySetCursor(0, headerLines + i);
                                PrintProgressLine(url, info, urlW, statusW, succW, rateW, etaW, thrdW, winW);
                            }
                        }

                        int summaryRow = headerLines + urls.Count;
                        TrySetCursor(0, summaryRow);
                        PrintSummary(urls, urlW, statusW, succW, rateW, etaW, startUtc, winW);
                    }
                }

                bool done = _map.Values.All(info =>
                    info.Status == ProgressStatus.Finished || info.Status == ProgressStatus.Error || info.Status == ProgressStatus.NoAds);
                
                if (_shouldStopProgress || done)
                {
                   if (done)
                   {
                        Thread.Sleep(RefreshRateMs);
                        lock (ConsoleLocker.Lock)
                        {
                            Console.WriteLine();
                            Console.WriteLine("All tasks completed.");
                        }
                   }
                   break;
                }

                Thread.Sleep(RefreshRateMs);
            }
        }

        private void PrintProgressLine(string url, ProgressInfo info, int urlW, int statusW, int succW, int rateW, int etaW, int thrdW, int tableWidth)
        {
            ProgressStatus status;
            int total, clicked, pending, errors, threads, iter;
            double? completedCps;
            DateTime? firstClick;

            lock (info)
            {
                status = info.Status;
                total = info.TotalAds;
                clicked = info.ClickedAds;
                pending = info.PendingClicks;
                errors = info.Errors;
                threads = info.Threads;
                iter = info.Iteration;
                completedCps = info.CompletedCps;
                firstClick = info.FirstClickUtc;
            }

            Console.ForegroundColor = status switch
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

            string urlCell = FitWithEllipsis(url, urlW);
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

            int w = tableWidth;
            if (line.Length >= w)
                Console.Write(line.Substring(0, Math.Max(0, w)));
            else
                Console.Write(line.PadRight(w));

            Console.ResetColor();
        }

        private void PrintSummary(List<string> urls, int urlW, int statusW, int succW, int rateW, int etaW, DateTime startUtc, int tableWidth)
        {
            int totalAds = 0, clicked = 0, pending = 0, errors = 0;
            var cpsList = new List<double>();
            
            foreach (var u in urls)
            {
                if (_map.TryGetValue(u, out var info))
                {
                    lock (info)
                    {
                        totalAds += info.TotalAds;
                        clicked += info.ClickedAds;
                        pending += info.PendingClicks;
                        errors += info.Errors;

                        double cps = 0.0;
                        if (info.Status == ProgressStatus.Finished)
                            cps = info.CompletedCps ?? 0.0;
                        else if (info.FirstClickUtc.HasValue)
                        {
                            var elapsedSec = Math.Max(1.0, (DateTime.UtcNow - info.FirstClickUtc.Value).TotalSeconds);
                            cps = info.ClickedAds / elapsedSec;
                        }

                        if (cps > 0) cpsList.Add(cps);
                    }
                }
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
            if (summary.Length >= tableWidth)
                Console.Write(summary.Substring(0, Math.Max(0, tableWidth)));
            else
                Console.Write(summary.PadRight(tableWidth));
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
