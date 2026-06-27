using DbPerfTest;
using System.Diagnostics;

var opt = DbPerfOptions.Parse(args);

Console.WriteLine(
    $"[DbPerf] DB 성능 테스트 시작 — clients={opt.Clients}  duration={opt.DurationSeconds}s  " +
    $"warmup={opt.WarmupSeconds}s  ratio={opt.ReadParts}:{opt.WriteParts}  attach={opt.Attach}");
Console.WriteLine("[DbPerf] 종료: Ctrl+C");

// CancellationTokenSource: Ctrl+C를 graceful 종료 신호로 통합
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[DbPerf] Ctrl+C — 종료 중...");
    cts.Cancel();
};

// ── 서버 구동 ─────────────────────────────────────────────────────────────────
ServerProcess? server = null;
if (!opt.Attach)
{
    server = ServerProcess.TryStart(opt.Port, opt.AdminPort, monitorIntervalSec: 1, opt);
    if (server is null)
    {
        Environment.Exit(2);
        return;
    }

    Console.WriteLine("[DbPerf] 서버 준비 대기 중 (첫 [STATS] 수신까지, 최대 15s)...");
    if (!await server.WaitForReadinessAsync(15_000))
    {
        Console.Error.WriteLine("[DbPerf] 서버 준비 타임아웃");
        await server.DisposeAsync();
        Environment.Exit(2);
        return;
    }
    Console.WriteLine("[DbPerf] 서버 준비 완료");
}
else
{
    Console.WriteLine($"[DbPerf] attach 모드 — port={opt.Port}. Crash/SessionLeak 체크 생략.");
}

long baselineHeap = server?.Latest.HeapBytes ?? 0;

var recorder    = new LatencyRecorder();
var clientStats = new ClientStats();

// volatile bool: warmup 종료 후 true로 플립, 측정 종료 후 false
bool recording = false;

// Task.Run: 각 클라이언트를 독립 ThreadPool 작업으로 기동
var clientTasks = Enumerable.Range(0, opt.Clients)
    .Select(_ => Task.Run(async () =>
    {
        var client = new DbPerfClient("127.0.0.1", opt.Port, opt, recorder, clientStats);
        await client.RunAsync(() => Volatile.Read(ref recording), cts.Token);
    }))
    .ToArray();

// ── Warmup ────────────────────────────────────────────────────────────────────
Console.WriteLine($"[DbPerf] 워밍업 중 ({opt.WarmupSeconds}s) — 이 구간 지연은 기록하지 않습니다...");
await Task.Delay(TimeSpan.FromSeconds(opt.WarmupSeconds), cts.Token)
         .ContinueWith(_ => { });

if (cts.Token.IsCancellationRequested)
{
    await Task.WhenAll(clientTasks);
    if (server is not null) await server.DisposeAsync();
    Environment.Exit(2);
    return;
}

// ── 측정 구간 ─────────────────────────────────────────────────────────────────
Console.WriteLine($"[DbPerf] 측정 중 ({opt.DurationSeconds}s)...");
long measureStartTicks = Stopwatch.GetTimestamp();
Volatile.Write(ref recording, true);

using var progressCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!progressCts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(5_000, progressCts.Token); }
        catch (OperationCanceledException) { break; }
        Console.WriteLine(
            $"[DbPerf] 진행 — errors={clientStats.Errors}  " +
            $"writes={recorder.WriteCount}  reads={recorder.ReadCount}");
    }
});

await Task.Delay(TimeSpan.FromSeconds(opt.DurationSeconds), cts.Token)
         .ContinueWith(_ => { });

progressCts.Cancel();
Volatile.Write(ref recording, false);
double elapsedSec =
    (Stopwatch.GetTimestamp() - measureStartTicks) / (double)Stopwatch.Frequency;

Console.WriteLine($"[DbPerf] 측정 완료 — 경과={elapsedSec:F1}s  " +
                  $"writes={recorder.WriteCount}  reads={recorder.ReadCount}");

// ── 클라이언트 종료 ───────────────────────────────────────────────────────────
cts.Cancel();
await Task.WhenAll(clientTasks);

// ── 서버 안정화 대기 ──────────────────────────────────────────────────────────
ServerStatsSnapshot? finalSnap = null;
if (server is not null)
{
    Console.WriteLine("[DbPerf] 서버 안정화 대기 중 (sessions=0)...");
    finalSnap = await server.WaitForStabilityAsync(10_000);
}

// ── 판정 리포트 ───────────────────────────────────────────────────────────────
var report = DbPerfReport.Evaluate(
    recorder, elapsedSec, clientStats,
    finalSnap, baselineHeap,
    server?.Crashed ?? false, opt.Attach,
    server?.LatestDb, opt);

report.Print();

if (server is not null)
    await server.DisposeAsync();

Environment.Exit(report.OverallPass ? 0 : 1);
