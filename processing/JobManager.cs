using System.Collections.Concurrent;
using InvenAdClicker.helper;
using InvenAdClicker.@struct;

namespace InvenAdClicker.processing;

public class JobManager
{
    private ConcurrentQueue<Job> _jobPool;
    private readonly AppSettings _setting = SettingsManager.LoadAppSettings();
    private ProgressTracker _progressTracker;

    public JobManager()
    {
        _progressTracker = ProgressTracker.Instance;
        _jobPool = new();
        GenerateJobs(SettingsManager.LoadURL());
        Logger.Info("JobManger initialized.");
    }

    private void GenerateJobs(List<string> urls)
    {
        int totalIterations = _setting.MaxIter;
        int iterationsPerSet = _setting.MaxSet;
        int totalSets = (totalIterations + iterationsPerSet - 1) / iterationsPerSet;

        foreach (var url in urls)
        {
            int remainingIterations = totalIterations;
            for (int currentSet = 0; currentSet < totalSets; currentSet++)
            {
                int itCnt = Math.Min(remainingIterations, iterationsPerSet);
                _jobPool.Enqueue(new Job(url, itCnt));
                remainingIterations -= itCnt;
            }
            _progressTracker.UpdateProgress(url);
        }
        Logger.Info("Jobs are generated.");
    }

    public void AppendJob(string url, int remain)
    {
        _jobPool.Enqueue(new Job(url, remain));
    }

    public bool IsEmpty()
    {
        return _jobPool.IsEmpty;
    }

    public bool Dequeue(out Job job)
    {
        return _jobPool.TryDequeue(out job);
    }

    private List<string> GetUrlsToProcess()
    {
        return new List<string>
        {
            "https://ff14.inven.co.kr/",
            "https://www.inven.co.kr/board/ff14/4336",
            "https://www.inven.co.kr/board/ff14/4336/831015",
            "https://m.inven.co.kr/ff14/",
            "https://m.inven.co.kr/board/ff14/4336",
            "https://m.inven.co.kr/board/ff14/4336/979369",
            "https://it.inven.co.kr/",
            "https://www.inven.co.kr/board/it/2631",
            "https://www.inven.co.kr/board/it/2631/226970",
            "https://www.inven.co.kr/",
            "https://m.inven.co.kr/"
        };
    }
}