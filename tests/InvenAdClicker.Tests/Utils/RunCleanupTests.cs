using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Utils
{
    public class RunCleanupTests
    {
        [Test]
        public void FailedContextCleanup_RetainsOriginalExceptionAndReportsSecondaryError()
        {
            var loginFailure = new InvalidOperationException("login failed");
            var closeFailure = new ApplicationException("context close failed");
            async Task InitializeAsync()
            {
                try { throw loginFailure; }
                catch (Exception ex)
                {
                    await ExceptionCleanup.CaptureAsync(ex, () => Task.FromException(closeFailure));
                    throw;
                }
            }
            var observed = Assert.ThrowsAsync<InvalidOperationException>(InitializeAsync);
            Assert.That(observed, Is.SameAs(loginFailure));
            Assert.That(ExceptionCleanup.Read(observed!), Is.EqualTo(new[] { closeFailure }));
        }

        [Test]
        public async Task DisposalFault_DoesNotReplacePrimaryFailure()
        {
            var primary = new InvalidOperationException("worker failed");
            var cleanup = new ApplicationException("scope disposal failed");
            var result = new PipelineRunResult { Status = PipelineRunStatus.FatalFailure, PrimaryException = primary };
            var completed = await RunCleanup.DisposeAsync(result, () => new ValueTask(Task.FromException(cleanup)), TimeSpan.FromSeconds(1));
            Assert.Multiple(() =>
            {
                Assert.That(completed.PrimaryException, Is.SameAs(primary));
                Assert.That(completed.CleanupErrors, Does.Contain(cleanup));
                Assert.That(completed.ExitCode, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task DisposalDeadline_IsReportedWhileLateFailureIsObserved()
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = await RunCleanup.DisposeAsync(new PipelineRunResult(),
                () => new ValueTask(pending.Task), TimeSpan.Zero);
            pending.SetException(new InvalidOperationException("late scope error"));
            Assert.Multiple(() =>
            {
                Assert.That(completed.ShutdownTimedOut, Is.True);
                Assert.That(completed.CleanupErrors, Has.Some.TypeOf<TimeoutException>());
                Assert.That(completed.ExitCode, Is.EqualTo(2));
            });
        }
    }
}
