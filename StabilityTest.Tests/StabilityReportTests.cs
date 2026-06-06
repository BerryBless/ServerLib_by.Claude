using StabilityTest;
using Xunit;

namespace StabilityTest.Tests;

public class StabilityReportTests
{
    private static StabilityEvidence Healthy() => new()
    {
        Crashed = false, ExitCode = 0, HangDetected = false,
        ReceivedFinal = 10_000, SentTotal = 10_000,
        TestFinal = 200, SentInc = 600, SentDec = 400,
        SessionsFinal = 0,
        HeapBaseline = 10_000_000, HeapFinal = 12_000_000, HeapTolerance = 2.0,
    };

    [Fact]
    public void Healthy_run_passes_all_hard_checks()
    {
        var (results, pass) = StabilityReport.Evaluate(Healthy());
        Assert.True(pass);
        Assert.All(results.Where(r => r.Severity == CheckSeverity.Hard), r => Assert.True(r.Passed));
    }

    [Fact]
    public void Crash_fails_overall()
    {
        var e = Healthy(); e.Crashed = true;
        var (_, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
    }

    [Fact]
    public void Data_loss_fails_overall()
    {
        var e = Healthy(); e.ReceivedFinal = 9_999;
        var (results, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
        Assert.Contains(results, r => r.Name == "DataLoss" && !r.Passed);
    }

    [Fact]
    public void Corruption_fails_overall()
    {
        var e = Healthy(); e.TestFinal = 199;
        var (_, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
    }

    [Fact]
    public void Leaked_sessions_fail_overall()
    {
        var e = Healthy(); e.SessionsFinal = 3;
        var (_, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
    }

    [Fact]
    public void Heap_over_tolerance_is_soft_and_does_not_fail_overall()
    {
        var e = Healthy(); e.HeapFinal = 100_000_000;
        var (results, pass) = StabilityReport.Evaluate(e);
        Assert.True(pass);
        Assert.Contains(results, r => r.Name == "LeakHeap" && r.Severity == CheckSeverity.Soft && !r.Passed);
    }
}
