using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using InvenAdClicker.helper;
using InvenAdClicker.Helper;
using InvenAdClicker.@struct;

namespace InvenAdClicker.Processing
{
    public class JobManager
    {
        private readonly ConcurrentQueue<Job> _jobPool;
        private readonly AppSettings _setting = SettingsManager.LoadAppSettings();
        private readonly ProgressTracker _progressTracker;

        public JobManager()
        {
            _progressTracker = ProgressTracker.Instance;
            _jobPool = new ConcurrentQueue<Job>();
            GenerateJobs(SettingsManager.LoadURL());
            Logger.Info("JobManager initialized.");
        }

        private void GenerateJobs(List<string> urls)
        {
            int totalIterations = _setting.MaxIter;
            int iterationsPerSet = _setting.MaxSet;
            int totalSets = (totalIterations + iterationsPerSet - 1) / iterationsPerSet;

            foreach (var url in urls)
            {
                int remainingIterations = totalIterations;
                for (int set = 0; set < totalSets; set++)
                {
                    int iterCount = Math.Min(remainingIterations, iterationsPerSet);
                    _jobPool.Enqueue(new Job(url, iterCount));
                    remainingIterations -= iterCount;
                }
                _progressTracker.UpdateProgress(url);
            }
            Logger.Info("Jobs generated successfully.");
        }

        public void AppendJob(string url, int remain)
        {
            _jobPool.Enqueue(new Job(url, remain));
        }

        public bool IsEmpty() => _jobPool.IsEmpty;

        public bool Dequeue(out Job job) => _jobPool.TryDequeue(out job);
    }
}