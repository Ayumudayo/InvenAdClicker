using System.Collections.Concurrent;
using InvenAdClicker.helper;
using InvenAdClicker.@struct;

namespace InvenAdClicker.processing
{
    public enum ProgressStatus
    {
        Waiting,
        Running,
        Suspended,
        Finished,
        Error
    }

    public class ProgressInfo
    {
        public ProgressStatus Status { get; set; }
        public int Iteration { get; set; }
        public int ErrorCount { get; set; }
        public int ThreadCount { get; set; }
    }

    public class ProgressTracker
    {
        private static readonly Lazy<ProgressTracker> _instance = new Lazy<ProgressTracker>(() => new ProgressTracker());
        public static ProgressTracker Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, ProgressInfo> _progressTracker;
        private readonly AppSettings _settings;

        private ProgressTracker()
        {
            _progressTracker = new ConcurrentDictionary<string, ProgressInfo>();
            _settings = SettingsManager.LoadAppSettings();
        }

        public void UpdateProgress(string url, bool incrementIteration = false, bool incrementError = false, int threadCountChange = 0)
        {
            _progressTracker.AddOrUpdate(url,
                (key) => new ProgressInfo { Status = ProgressStatus.Waiting, Iteration = 0, ErrorCount = 0, ThreadCount = 0 },
                (key, oldValue) =>
                {
                    if (incrementIteration) oldValue.Iteration++;
                    if (incrementError) oldValue.ErrorCount++;
                    oldValue.ThreadCount += threadCountChange;
                    oldValue.ThreadCount = Math.Max(oldValue.ThreadCount, 0);

                    if (incrementError) oldValue.Status = ProgressStatus.Error;
                    else if (oldValue.Iteration == _settings.MaxIter) oldValue.Status = ProgressStatus.Finished;
                    else if (oldValue.ThreadCount > 0) oldValue.Status = ProgressStatus.Running;
                    else if (oldValue.Iteration > 0) oldValue.Status = ProgressStatus.Suspended;
                    else oldValue.Status = ProgressStatus.Waiting;

                    return oldValue;
                });
        }

        public void PrintProgress()
        {
            int urlMaxLength = _progressTracker.Keys.Max(url => url.Length) + 2;
            int statusMaxLength = Enum.GetNames(typeof(ProgressStatus)).Max(name => name.Length) + 2;
            int iterationMaxLength = 10; // "Iteration".Length + 여유 공간
            int errorCountMaxLength = 7; // "ErrCnt".Length + 여유 공간
            int threadCountMaxLength = 8; // "ThrdCnt".Length + 여유 공간

            // 커서 위치 저장 및 초기 설정
            Console.Clear();
            bool isInitialPrint = true;
            int cursorTop = Console.CursorTop;
            Console.WriteLine("Inven Ad Clicker V3\n");

            while (true)
            {
                if (isInitialPrint)
                {
                    Console.WriteLine($"{"URL".PadRight(urlMaxLength)} {"Status".PadRight(statusMaxLength)} {"Iteration".PadRight(iterationMaxLength)} {"ErrCnt".PadRight(errorCountMaxLength)} {"ThrdCnt".PadRight(threadCountMaxLength)}");
                    Console.WriteLine(new string('-', urlMaxLength + statusMaxLength + iterationMaxLength + errorCountMaxLength + threadCountMaxLength));
                    isInitialPrint = false;
                }

                int currentLine = cursorTop + 4; // 헤더와 구분선 아래부터 시작
                foreach (var entry in _progressTracker)
                {
                    Console.SetCursorPosition(0, currentLine++);
                    string url = entry.Key.PadRight(urlMaxLength);
                    var info = entry.Value;
                    string status = info.Status.ToString().PadRight(statusMaxLength);
                    string iteration = $"{info.Iteration}/{_settings.MaxIter}".PadRight(iterationMaxLength);
                    string errorCount = info.ErrorCount.ToString().PadRight(errorCountMaxLength);
                    string threadCount = info.ThreadCount.ToString().PadRight(threadCountMaxLength);

                    Console.ForegroundColor = GetColorForStatus(info.Status);
                    Console.WriteLine($"{url} {status} {iteration} {errorCount} {threadCount}");
                    Console.ResetColor();
                }

                Thread.Sleep(1000); // 1초 대기
            }
        }

        //public ProgressInfo GetUrlInfo(string url)
        //{
        //    ProgressInfo info;
        //    _progressTracker.TryGetValue(url, out info);
        //    return info;
        //}

        private static ConsoleColor GetColorForStatus(ProgressStatus status)
        {
            return status switch
            {
                ProgressStatus.Running => ConsoleColor.Green,
                ProgressStatus.Suspended => ConsoleColor.Magenta,
                ProgressStatus.Waiting => ConsoleColor.Yellow,
                ProgressStatus.Finished => ConsoleColor.Cyan,
                ProgressStatus.Error => ConsoleColor.Red,
                _ => ConsoleColor.White,
            };
        }
    }
}
