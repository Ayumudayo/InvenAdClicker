using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        // 진행표시 강화용 타임스탬프(속도/ETA 계산용)
        public DateTime? FirstUpdateUtc { get; set; }
        public DateTime? FirstClickUtc { get; set; }
        public DateTime? LastUpdateUtc { get; set; }
    }

    public class ProgressTracker
    {
        private static readonly Lazy<ProgressTracker> _lazy =
            new Lazy<ProgressTracker>(() => new ProgressTracker());
        public static ProgressTracker Instance => _lazy.Value;

        private readonly ConcurrentDictionary<string, ProgressInfo> _map =
            new ConcurrentDictionary<string, ProgressInfo>();
        private volatile bool _shouldStopProgress = false;

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
                if (status.HasValue)
                    info.Status = status.Value;

                info.Iteration += iterDelta;
                info.TotalAds += adsDelta;
                info.ClickedAds += clickDelta;
                info.Errors += errDelta;
                info.Threads = Math.Max(0, info.Threads + threadDelta);
                info.PendingClicks += pendingClicksDelta;

                // 타임스탬프 갱신
                info.LastUpdateUtc = now;
                if (!info.FirstUpdateUtc.HasValue)
                    info.FirstUpdateUtc = now;
                if (clickDelta > 0 && !info.FirstClickUtc.HasValue)
                    info.FirstClickUtc = now;

                if (info.Status == ProgressStatus.Clicking && info.PendingClicks <= 0)
                    info.Status = ProgressStatus.Finished;
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

        public void PrintProgress()
        {
            bool redirected = Console.IsOutputRedirected;
            bool isWindowsTerminal = !redirected && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));
            if (_map.IsEmpty)
            {
                lock (ConsoleLocker.Lock)
                {
                    Console.WriteLine("No target URLs. Exiting.");
                }
                return;
            }
            int statusW = Math.Max("Status".Length, Enum.GetNames(typeof(ProgressStatus)).Max(s => s.Length)) + 2;
            int rateW = 8;   // Speed(c/s)
            int succW = 8;   // Success(%)
            int etaW = 10;   // ETA
            int thrdW = 6;   // Threads
            var urls = _map.Keys.OrderBy(k => k).ToList();
            int headerLines = 2; // header + separator
            var startUtc = DateTime.UtcNow;

            // 화면 출력용 폭 계산: 창 너비에 맞춰 URL 열 폭을 조정
            int baseWidth = statusW + 8 + 8 + 8 + succW + rateW + 9 + etaW + 6 + thrdW;
            int urlW = redirected ? Math.Max(20, _map.Keys.Max(k => k.Length) + 2)
                                   : Math.Max(8, Console.WindowWidth - baseWidth);
            int tableWidth = urlW + baseWidth;
            if (!redirected && !isWindowsTerminal)
            {
                // 고전 콘솔의 경우 가로 스크롤 허용(가능 시)
                EnsureBufferWidth(tableWidth);
            }

            if (!redirected)
            {
                lock (ConsoleLocker.Lock)
                {
                    string header =
                        "URL".PadRight(urlW) +
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
                    WriteLineFitting(header);
                    WriteLineFitting(new string('-', tableWidth));

                    for (int i = 0; i < urls.Count; i++)
                    {
                        PrintProgressLine(urls[i], _map[urls[i]], urlW, statusW, succW, rateW, etaW, thrdW, tableWidth);
                        Console.WriteLine();
                    }
                }
            }

            while (true)
            {
                lock (ConsoleLocker.Lock)
                {
                    if (!redirected)
                    {
                        // 창 크기 변경 시 폭 재계산 및 필요 시 버퍼 확장
                        baseWidth = statusW + 8 + 8 + 8 + succW + rateW + 9 + etaW + 6 + thrdW;
                        urlW = Math.Max(8, Console.WindowWidth - baseWidth);
                        tableWidth = urlW + baseWidth;
                        if (!isWindowsTerminal)
                            EnsureBufferWidth(tableWidth);
                        for (int i = 0; i < urls.Count; i++)
                        {
                            var url = urls[i];
                            var info = _map[url];

                            lock (info)
                            {
                                TrySetCursor(0, headerLines + i);
                                PrintProgressLine(url, info, urlW, statusW, succW, rateW, etaW, thrdW, tableWidth);
                            }
                        }

                        // Summary 갱신 위치로 이동(표 바로 아래)
                        int summaryRow = headerLines + urls.Count;
                        TrySetCursor(0, summaryRow);
                        PrintSummary(urls, urlW, statusW, succW, rateW, etaW, startUtc, tableWidth);
                    }
                }

                bool done = _map.Values.All(info =>
                    info.Status == ProgressStatus.Finished || info.Status == ProgressStatus.Error || info.Status == ProgressStatus.NoAds);
                if (done || _shouldStopProgress)
                {
                    Thread.Sleep(150); // 마지막 갱신 반영 여유
                    lock (ConsoleLocker.Lock)
                    {
                        if (redirected)
                        {
                            // 최종 테이블 및 요약 1회 출력
                            // 리다이렉트 시에는 전체 길이 기준으로 열 너비 확보
                            int urlWFull = Math.Max(20, _map.Keys.Max(k => k.Length) + 2);
                            int tableWidthFull = urlWFull + statusW + 8 + 8 + 8 + succW + rateW + 9 + etaW + 6 + thrdW;
                            Console.WriteLine(
                                "URL".PadRight(urlWFull) +
                                "Status".PadRight(statusW) +
                                "Iter".PadRight(8) +
                                "Ads".PadRight(8) +
                                "Clicked".PadRight(8) +
                                "Success".PadRight(succW) +
                                "Speed".PadRight(rateW) +
                                "Pending".PadRight(9) +
                                "ETA".PadRight(etaW) +
                                "Err".PadRight(6) +
                                "Thrd".PadRight(thrdW));
                            Console.WriteLine(new string('-', tableWidthFull));
                            for (int i = 0; i < urls.Count; i++)
                            {
                                var info = _map[urls[i]];
                                lock (info)
                                {
                                    PrintProgressLine(urls[i], info, urlWFull, statusW, succW, rateW, etaW, thrdW, tableWidthFull);
                                }
                                Console.WriteLine();
                            }
                            Console.WriteLine(new string('-', tableWidthFull));
                            PrintSummary(urls, urlWFull, statusW, succW, rateW, etaW, startUtc, tableWidthFull);
                            Console.WriteLine();
                            Console.WriteLine("All tasks completed!");
                            try { Console.Out.Flush(); } catch { }
                        }
                        else
                        {
                            TrySetCursor(0, headerLines + urls.Count + 1);
                            Console.WriteLine();
                        }
                    }
                    break;
                }
                Thread.Sleep(500);
            }
        }

        private void PrintProgressLine(string url, ProgressInfo info, int urlW, int statusW, int succW, int rateW, int etaW, int thrdW, int tableWidth)
        {
            Console.ForegroundColor = info.Status switch
            {
                ProgressStatus.Collecting => ConsoleColor.Yellow,
                ProgressStatus.Collected => ConsoleColor.Magenta,
                ProgressStatus.Clicking => ConsoleColor.Green,
                ProgressStatus.Finished => ConsoleColor.Cyan,
                ProgressStatus.Error => ConsoleColor.Red,
                _ => ConsoleColor.DarkGray,
            };

            string statusText = info.Status.ToString();
            // 성공률
            string succ = info.TotalAds > 0 ? ($"{(info.ClickedAds * 100.0 / Math.Max(1, info.TotalAds)):0.#}%") : "-";
            // 속도(클릭/초)
            double cps = 0.0;
            if (info.FirstClickUtc.HasValue)
            {
                var elapsedSec = Math.Max(1.0, (DateTime.UtcNow - info.FirstClickUtc.Value).TotalSeconds);
                cps = info.ClickedAds / elapsedSec;
            }
            string rate = cps > 0 ? $"{cps:0.##}c/s" : "-";
            // ETA
            string eta = "-";
            if (cps > 0 && info.PendingClicks > 0)
            {
                var sec = info.PendingClicks / cps;
                var ts = TimeSpan.FromSeconds(sec);
                eta = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
            }

            string urlCell = FitWithEllipsis(url, urlW);
            string line =
                $"{urlCell}" +
                $"{statusText.PadRight(statusW)}" +
                $"{info.Iteration.ToString().PadRight(8)}" +
                $"{info.TotalAds.ToString().PadRight(8)}" +
                $"{info.ClickedAds.ToString().PadRight(8)}" +
                $"{succ.PadRight(succW)}" +
                $"{rate.PadRight(rateW)}" +
                $"{info.PendingClicks.ToString().PadRight(9)}" +
                $"{eta.PadRight(etaW)}" +
                $"{info.Errors.ToString().PadRight(6)}" +
                $"{info.Threads.ToString().PadRight(thrdW)}";

            if (!Console.IsOutputRedirected)
            {
                // 창 너비에 맞춰 정확히 덮어쓰기(래핑 방지)
                int w = Console.WindowWidth;
                if (line.Length >= w)
                    Console.Write(line.Substring(0, Math.Max(0, w)));
                else
                    Console.Write(line.PadRight(w));
            }
            else
            {
                // 리다이렉트 시 전체 행 출력
                if (line.Length < tableWidth)
                    line = line.PadRight(tableWidth);
                Console.Write(line);
            }

            Console.ResetColor();
        }

        private void PrintSummary(List<string> urls, int urlW, int statusW, int succW, int rateW, int etaW, DateTime startUtc, int tableWidth)
        {
            int totalAds = 0, clicked = 0, pending = 0, errors = 0;
            foreach (var u in urls)
            {
                var info = _map[u];
                lock (info)
                {
                    totalAds += info.TotalAds;
                    clicked += info.ClickedAds;
                    pending += info.PendingClicks;
                    errors += info.Errors;
                }
            }
            var elapsed = Math.Max(1.0, (DateTime.UtcNow - startUtc).TotalSeconds);
            var cps = clicked / elapsed; // 전체 클릭/초
            string rate = cps > 0 ? $"{cps:0.##}c/s" : "-";
            string succ = totalAds > 0 ? ($"{(clicked * 100.0 / Math.Max(1, totalAds)):0.#}%") : "-";
            string eta = "-";
            if (cps > 0 && pending > 0)
            {
                var sec = pending / cps;
                var ts = TimeSpan.FromSeconds(sec);
                eta = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
            }

            string summary = $"Summary | Ads:{totalAds} Clicked:{clicked} Pending:{pending} Err:{errors} | Success:{succ} Speed:{rate} ETA:{eta}";
            if (!Console.IsOutputRedirected)
            {
                int w = Console.WindowWidth;
                if (summary.Length >= w)
                    Console.Write(summary.Substring(0, Math.Max(0, w)));
                else
                    Console.Write(summary.PadRight(w));
            }
            else
            {
                if (summary.Length < tableWidth)
                    summary = summary.PadRight(tableWidth);
                Console.Write(summary);
            }
        }

        private static void EnsureBufferWidth(int requiredWidth)
        {
            try
            {
                // 창 너비보다 작게 설정할 수 없으므로 보정
                int desiredWidth = Math.Max(requiredWidth, Console.WindowWidth);
                if (Console.BufferWidth < desiredWidth)
                {
                    int desiredHeight = Math.Max(Console.BufferHeight, Console.WindowHeight);
                    Console.SetBufferSize(desiredWidth, desiredHeight);
                }
            }
            catch
            {
                // 일부 호스트(Windows Terminal 등)에서는 가로 스크롤이 지원되지 않을 수 있음
                // 실패 시 그냥 무시하고 진행(래핑은 호스트가 처리)
            }
        }

        private static string FitWithEllipsis(string s, int width)
        {
            if (width <= 0) return string.Empty;
            if (s.Length <= width) return s.PadRight(width);
            if (width == 1) return "…"; // 최소 표기
            return s.Substring(0, Math.Max(0, width - 1)) + "…";
        }

        private static void WriteLineFitting(string s)
        {
            if (!Console.IsOutputRedirected)
            {
                int w = Console.WindowWidth;
                if (s.Length > w)
                    Console.WriteLine(s.Substring(0, Math.Max(0, w)));
                else
                    Console.WriteLine(s);
            }
            else
            {
                Console.WriteLine(s);
            }
        }
    }
}
