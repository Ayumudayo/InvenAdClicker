using System;
using System.Collections.Generic;
using System.Linq;

namespace InvenAdClicker.Models
{
    public enum PipelineRunStatus { Success, PartialFailure, TotalFailure, Cancelled, FatalFailure }

    public sealed record PipelineRunResult
    {
        public PipelineRunStatus Status { get; init; }
        public PipelineRunResult WithCleanupError(Exception error) => this with
        {
            Status = PipelineRunStatus.FatalFailure,
            CleanupErrors = CleanupErrors.Concat(new[] { error }).ToArray()
        };

        public int ExitCode => Status switch
        {
            PipelineRunStatus.Success => 0,
            PipelineRunStatus.Cancelled => 1,
            PipelineRunStatus.FatalFailure => 2,
            PipelineRunStatus.PartialFailure => 3,
            PipelineRunStatus.TotalFailure => 4,
            _ => 2
        };
        public int TotalUrls { get; init; }
        public int CollectionSucceeded { get; init; }
        public int CollectionFailed { get; init; }
        public int NoAdsUrls { get; init; }
        public int UnprocessedUrls => Math.Max(0, TotalUrls - CollectionSucceeded - CollectionFailed);
        public int DiscoveredLinks { get; init; }
        public int ClickSucceeded { get; init; }
        public int ClickFailed { get; init; }
        public int SimulatedClicks { get; init; }
        public int UnprocessedLinks => Math.Max(0, DiscoveredLinks - ClickSucceeded - ClickFailed - SimulatedClicks);
        public Exception? PrimaryException { get; init; }
        public IReadOnlyList<Exception> CleanupErrors { get; init; } = Array.Empty<Exception>();
        public bool ShutdownTimedOut { get; init; }
        public int UnfinishedTaskCount { get; init; }
    }

    public sealed class PipelineShutdownOptions
    {
        public TimeSpan CooperativeTimeout { get; init; } = TimeSpan.FromSeconds(2);
        public TimeSpan AbortTimeout { get; init; } = TimeSpan.FromSeconds(3);
        public TimeSpan FinalObservationTimeout { get; init; } = TimeSpan.FromSeconds(2);

        public TimeSpan ResourceDisposalTimeout { get; init; } = TimeSpan.FromSeconds(5);

        internal void Validate()
        {
            if (CooperativeTimeout < TimeSpan.Zero || AbortTimeout < TimeSpan.Zero || FinalObservationTimeout < TimeSpan.Zero || ResourceDisposalTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(PipelineShutdownOptions), "Shutdown budgets must be nonnegative.");
        }
    }
}
