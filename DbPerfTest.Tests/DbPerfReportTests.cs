using DbPerfTest;
using Xunit;

namespace DbPerfTest.Tests;

public class DbPerfReportTests
{
    private static DbPerfOptions DefaultOpt() => DbPerfOptions.Parse([]);

    private static LatencyRecorder MakeRecorder(long[] writeUs, long[] readUs)
    {
        var r = new LatencyRecorder();
        foreach (var v in writeUs) r.RecordWrite(v);
        foreach (var v in readUs)  r.RecordRead(v);
        return r;
    }

    [Fact]
    public void Evaluate_HappyPath_OverallPass()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var snap = new ServerStatsSnapshot(Received: 10, Sessions: 0, HeapBytes: 10_000_000);
        var dbSnap = new DbStatsSnapshot(MysqlSelectAvgUs: 38, RedisGetAvgUs: 1, RedisSetAvgUs: 1,
            MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1);
        var report = DbPerfReport.Evaluate(recorder, elapsedSec: 1.0, new ClientStats(),
            snap, baselineHeap: 9_000_000, serverCrashed: false, attachMode: false, dbSnap, DefaultOpt());
        Assert.True(report.OverallPass);
        Assert.False(report.NoDbData);
    }

    [Fact]
    public void Evaluate_SessionLeak_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var snap = new ServerStatsSnapshot(Sessions: 1, HeapBytes: 10_000_000);
        var report = DbPerfReport.Evaluate(recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            new DbStatsSnapshot(MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1), DefaultOpt());
        Assert.False(report.OverallPass);
        Assert.True(report.SessionLeak);
    }

    [Fact]
    public void Evaluate_NoDbData_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var snap = new ServerStatsSnapshot(Sessions: 0, HeapBytes: 10_000_000);
        var report = DbPerfReport.Evaluate(recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false, dbSnap: null, DefaultOpt());
        Assert.False(report.OverallPass);
        Assert.True(report.NoDbData);
    }

    [Fact]
    public void Evaluate_ThroughputBelowTarget_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        // 1 write + 1 read in 1s = 2 req/s, target=1000
        var opt = DbPerfOptions.Parse(["--target-throughput", "1000"]);
        var snap = new ServerStatsSnapshot(Sessions: 0, HeapBytes: 10_000_000);
        var report = DbPerfReport.Evaluate(recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            new DbStatsSnapshot(MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1), opt);
        Assert.False(report.OverallPass);
        Assert.True(report.ThroughputBelowTarget);
    }

    [Fact]
    public void Evaluate_LatencyAboveTarget_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]); // write 50ms, read 2ms
        var opt = DbPerfOptions.Parse(["--target-p99-ms", "1"]);
        var snap = new ServerStatsSnapshot(Sessions: 0, HeapBytes: 10_000_000);
        var report = DbPerfReport.Evaluate(recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            new DbStatsSnapshot(MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1), opt);
        Assert.False(report.OverallPass);
        Assert.True(report.LatencyAboveTarget);
    }

    [Fact]
    public void Evaluate_AttachMode_SkipsServerChecks()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        // attachMode=true: crash/sessionLeak skipped; NoDbData still checked
        var report = DbPerfReport.Evaluate(recorder, 1.0, new ClientStats(),
            null, 0, serverCrashed: true, attachMode: true, null, DefaultOpt());
        Assert.True(report.NoDbData);
        Assert.False(report.Crash);
        Assert.False(report.SessionLeak);
    }
}
