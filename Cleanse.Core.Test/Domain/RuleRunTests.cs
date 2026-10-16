using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Domain;

public class RuleRunTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    [Fact]
    public void A_new_run_is_running_with_empty_counters()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.IsRunning);
        Assert.Equal(RuleRunCounters.Empty, run.Counters);
        Assert.Null(run.FinishedAtUtc);
    }

    [Fact]
    public void Counters_accumulate_across_batches()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.RecordUpserts(new RuleRunCounters(10, 4, 1, 2, 3, 0)).IsSuccess);
        Assert.True(run.RecordUpserts(new RuleRunCounters(5, 1, 0, 1, 3, 0)).IsSuccess);

        Assert.Equal(15, run.Counters.DetectionsProcessed);
        Assert.Equal(5, run.Counters.IssuesCreated);
        Assert.Equal(6, run.Counters.IssuesUnchanged);
    }

    [Fact]
    public void Completing_records_the_sweep_and_closes_the_run()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        var result = run.Complete(7, Night1.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(7, run.Counters.IssuesDeactivated);
        Assert.Equal(Night1.AddHours(1), run.FinishedAtUtc);
    }

    [Fact]
    public void A_closed_run_rejects_further_work()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.Complete(0, Night1).IsSuccess);

        Assert.Equal(ErrorCodes.RunNotRunning, run.Complete(0, Night1).Error!.Code);
        Assert.Equal(ErrorCodes.RunNotRunning, run.Abandon("too late", Night1).Error!.Code);
        Assert.Equal(ErrorCodes.RunNotRunning, run.RecordUpserts(RuleRunCounters.Empty).Error!.Code);
    }

    [Fact]
    public void Abandoning_keeps_the_reason()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.Abandon("  source system timed out  ", Night1).IsSuccess);

        Assert.Equal(RunState.Abandoned, run.State);
        Assert.Equal("source system timed out", run.AbandonReason);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_state()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.RecordUpserts(new RuleRunCounters(3, 1, 1, 1, 0, 0)).IsSuccess);
        Assert.True(run.ReportProgress("Evaluating rules", 120, 500, Night1.AddMinutes(1)).IsSuccess);

        var restored = RuleRun.FromSnapshot(run.ToSnapshot());

        Assert.Equal(run.ToSnapshot(), restored.ToSnapshot());
    }

    [Fact]
    public void Progress_starts_at_nothing_and_beats_on_every_report()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.Equal(0d, run.ProgressPercent);
        Assert.Equal(Night1, run.HeartbeatAtUtc);

        Assert.True(run.ReportProgress("Evaluating rules", 250, 1000, Night1.AddMinutes(5)).IsSuccess);

        Assert.Equal(25d, run.ProgressPercent);
        Assert.Equal("Evaluating rules", run.Progress.CurrentStage);
        Assert.Equal(Night1.AddMinutes(5), run.HeartbeatAtUtc);
    }

    [Fact]
    public void Reporting_without_a_stage_leaves_the_previous_one_standing()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.ReportProgress("Evaluating rules", 1, 10, Night1).IsSuccess);

        Assert.True(run.ReportProgress(null, 2, 10, Night1).IsSuccess);

        Assert.Equal("Evaluating rules", run.Progress.CurrentStage);
    }

    [Fact]
    public void An_unknown_total_reports_no_progress_rather_than_a_division_by_zero()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.ReportProgress("Acquiring snapshot", 0, 0, Night1).IsSuccess);

        Assert.Equal(0d, run.ProgressPercent);
        Assert.Null(run.EstimatedCompletionAtUtc(Night1.AddMinutes(1)));
    }

    [Fact]
    public void A_completed_run_reads_as_a_hundred_percent_even_with_no_total()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.Complete(0, Night1.AddMinutes(1)).IsSuccess);

        Assert.Equal(100d, run.ProgressPercent);
    }

    [Fact]
    public void A_completed_run_has_no_current_stage()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.ReportProgress("Deactivating issues no longer detected", 10, 10, Night1).IsSuccess);

        Assert.True(run.Complete(0, Night1.AddMinutes(1)).IsSuccess);

        // It finished, so there is no stage it is in; the counts below say what it did.
        Assert.Null(run.Progress.CurrentStage);
        Assert.Equal(10, run.Progress.RecordsProcessed);
    }

    [Theory]
    [InlineData(RunState.Abandoned)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Failed)]
    public void A_run_that_stopped_early_keeps_the_stage_it_stopped_at(RunState ending)
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.ReportProgress("Evaluating rules", 3, 10, Night1).IsSuccess);

        var ended = ending switch
        {
            RunState.Abandoned => run.Abandon("rule threw", Night1.AddMinutes(1)),
            RunState.Cancelled => run.Cancel(null, Night1.AddMinutes(1)),
            _ => run.Fail("boom", null, Night1.AddMinutes(1))
        };

        Assert.True(ended.IsSuccess);

        // Where it got to is the whole diagnostic, so unlike a completed run it is kept.
        Assert.Equal("Evaluating rules", run.Progress.CurrentStage);
    }

    [Fact]
    public void Projects_completion_from_the_rate_achieved_so_far()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        // A quarter of the work in one minute projects a four minute run.
        Assert.True(run.ReportProgress("Evaluating rules", 250, 1000, Night1.AddMinutes(1)).IsSuccess);

        Assert.Equal(Night1.AddMinutes(4), run.EstimatedCompletionAtUtc(Night1.AddMinutes(1)));
    }

    [Fact]
    public void Projects_nothing_before_the_first_record_is_processed()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.ReportProgress("Evaluating rules", 0, 1000, Night1).IsSuccess);

        Assert.Null(run.EstimatedCompletionAtUtc(Night1.AddMinutes(1)));
    }

    [Fact]
    public void A_finished_run_projects_the_time_it_actually_finished()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.Complete(0, Night1.AddMinutes(3)).IsSuccess);

        Assert.Equal(Night1.AddMinutes(3), run.EstimatedCompletionAtUtc(Night1.AddHours(9)));
    }

    [Fact]
    public void Elapsed_runs_to_now_while_running_and_freezes_once_finished()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.Equal(TimeSpan.FromMinutes(2), run.Elapsed(Night1.AddMinutes(2)));

        Assert.True(run.Complete(0, Night1.AddMinutes(3)).IsSuccess);

        Assert.Equal(TimeSpan.FromMinutes(3), run.Elapsed(Night1.AddHours(9)));
    }

    [Fact]
    public void Requesting_cancellation_leaves_the_run_running_until_the_engine_notices()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.RequestCancellation(Night1.AddMinutes(1)).IsSuccess);

        Assert.True(run.CancellationRequested);
        Assert.True(run.IsRunning);
        Assert.Equal(RunState.Running, run.State);
    }

    [Fact]
    public void Requesting_cancellation_twice_is_harmless()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.RequestCancellation(Night1).IsSuccess);
        var version = run.Version;

        Assert.True(run.RequestCancellation(Night1).IsSuccess);

        Assert.Equal(version, run.Version);
    }

    [Fact]
    public void Cancelling_closes_the_run_with_a_reason()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.Cancel("  asked to stop  ", Night1.AddMinutes(1)).IsSuccess);

        Assert.Equal(RunState.Cancelled, run.State);
        Assert.Equal("asked to stop", run.AbandonReason);
        Assert.True(run.CancellationRequested);
        Assert.Equal(Night1.AddMinutes(1), run.FinishedAtUtc);
    }

    [Fact]
    public void Cancelling_without_a_reason_still_records_one()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.Cancel(null, Night1).IsSuccess);

        Assert.Equal("Cancelled.", run.AbandonReason);
    }

    [Fact]
    public void Failing_keeps_the_message_and_the_full_technical_detail()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        Assert.True(run.Fail("The snapshot is unreadable.", "SqliteException: file is not a database\n  at ...", Night1).IsSuccess);

        Assert.Equal(RunState.Failed, run.State);
        Assert.Equal("The snapshot is unreadable.", run.Failure!.Message);
        Assert.Contains("SqliteException", run.Failure.Detail);
        Assert.Equal(Night1, run.FinishedAtUtc);
    }

    [Fact]
    public void A_closed_run_rejects_progress_cancellation_and_failure()
    {
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(run.Complete(0, Night1).IsSuccess);

        Assert.Equal(ErrorCodes.RunNotRunning, run.ReportProgress("x", 1, 2, Night1).Error!.Code);
        Assert.Equal(ErrorCodes.RunNotRunning, run.RequestCancellation(Night1).Error!.Code);
        Assert.Equal(ErrorCodes.RunNotRunning, run.Cancel(null, Night1).Error!.Code);
        Assert.Equal(ErrorCodes.RunNotRunning, run.Fail("too late", null, Night1).Error!.Code);
    }
}
