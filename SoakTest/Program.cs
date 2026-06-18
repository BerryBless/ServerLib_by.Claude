using SoakTest;

// ── CLI 파싱 ─────────────────────────────────────────────────────────────────
var opt = SoakOptions.Parse(args);

Console.WriteLine(
    $"[SoakTest] 소크 테스트 시작 — clients={opt.Clients}  port={opt.Port}  " +
    $"sends={opt.SendsPerConn}  churnDelay={opt.ChurnDelayMs}ms  " +
    $"settle={opt.ReceiveSettleMs}ms  report={opt.ReportIntervalSec}s  attach={opt.Attach}");
Console.WriteLine("[SoakTest] 종료: Ctrl+C 또는 'q'+Enter 입력");

// CancellationTokenSource: Ctrl+C + 'q' 입력을 단일 취소 신호로 통합
// using: 종료 시 반드시 Cancel() 후 Dispose — Task.Delay 등에서 잡힌 OCE를 정리
using var cts = new CancellationTokenSource();

// ── Ctrl+C 처리 ──────────────────────────────────────────────────────────────
// CancelKeyPress: 프로세스 즉시 종료(ProcessExit) 대신 graceful 종료 경로 실행
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[SoakTest] Ctrl+C 감지 — 종료 중...");
    cts.Cancel();
};

// ── 서버 구동 / 부착 ─────────────────────────────────────────────────────────
ServerProcess? server = null;
if (!opt.Attach)
{
    server = ServerProcess.TryStart(opt.Port, opt.AdminPort, monitorIntervalSec: 1);
    if (server is null)
    {
        Environment.Exit(2);
        return;
    }

    Console.WriteLine("[SoakTest] 서버 준비 대기 중 (첫 [STATS] 수신까지)...");
    bool ready = await server.WaitForReadinessAsync(timeoutMs: 10_000);
    if (!ready)
    {
        Console.Error.WriteLine("[SoakTest] 서버 준비 타임아웃 또는 조기 종료");
        await server.DisposeAsync();
        Environment.Exit(2);
        return;
    }
    Console.WriteLine("[SoakTest] 서버 준비 완료");
}
else
{
    Console.WriteLine($"[SoakTest] attach 모드 — port={opt.Port} 외부 서버에 연결합니다.");
    Console.WriteLine("[SoakTest] attach 모드: Crash·SessionLeak·DataLoss 체크 생략");
}

// 기준 heap: 서버 준비 직후(클라이언트 부하 전) 안정 상태의 heap 측정
long baselineHeap = server?.Latest.HeapBytes ?? 0;

// ── lock-free 집계 카운터 ────────────────────────────────────────────────────
var stats = new SoakStats();

// ── N개 클라이언트 Task 기동 ─────────────────────────────────────────────────
// Task.Run: 각 SoakClient.RunAsync를 독립 ThreadPool 작업으로 기동 — await 없이 병렬 실행
var clientTasks = Enumerable.Range(0, opt.Clients)
    .Select(_ => Task.Run(async () =>
    {
        var client = new SoakClient(
            "127.0.0.1", opt.Port,
            opt.SendsPerConn, opt.ChurnDelayMs, opt.ReceiveSettleMs,
            stats);
        try
        {
            await client.RunAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SoakClient] 예상치 못한 오류: {ex.Message}");
        }
    }))
    .ToArray();

Console.WriteLine($"[SoakTest] {opt.Clients}개 클라이언트 시작  [PROGRESS] {opt.ReportIntervalSec}초 주기 출력");

// ── 'q' 입력 감시 Task ────────────────────────────────────────────────────────
// Console.ReadLine()은 블로킹 → Task.Run으로 분리. cts 취소 후 Environment.Exit로 강제 종료되므로 abandon 무관.
var qWatcherTask = Task.Run(() =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        string? line;
        try { line = Console.ReadLine(); }
        catch { break; }

        if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("[SoakTest] 'q' 입력 감지 — 종료 중...");
            cts.Cancel();
            break;
        }
        if (cts.Token.IsCancellationRequested) break;
    }
});

// ── 주기적 진행 출력 Task ────────────────────────────────────────────────────
var reporterTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(opt.ReportIntervalSec), cts.Token); }
        catch (OperationCanceledException) { break; }

        var snap = server?.Latest;
        Console.WriteLine(
            $"[PROGRESS]  cycles={stats.Cycles:N0}  conns={stats.Connects:N0}  " +
            $"sent={stats.Sent:N0}  recv={stats.Received:N0}  errs={stats.Errors}  " +
            $"serverSessions={snap?.Sessions ?? -1}  " +
            $"serverRecv={snap?.Received ?? -1:N0}  " +
            $"heap={(snap?.HeapBytes ?? 0) / 1024:N0}KB");
    }
});

// ── 클라이언트 완료 대기 ─────────────────────────────────────────────────────
// cts 취소 → 각 SoakClient.RunAsync가 루프 탈출 → Task.WhenAll 반환
await Task.WhenAll(clientTasks);
Console.WriteLine("[SoakTest] 모든 클라이언트 종료 완료");

// ── 서버 안정화 대기 ─────────────────────────────────────────────────────────
// 중요: 반드시 서버에 'q'를 보내기 전에 권위 read를 수행해야 한다.
// Stop() 이후 Pipe 버퍼가 폐기되면 in-flight 패킷이 유실 → false DataLoss 판정 발생.
ServerStatsSnapshot? finalSnap = null;
if (server is not null)
{
    Console.WriteLine("[SoakTest] 서버 안정화 대기 중 (sessions=0, received 안정)...");
    finalSnap = await server.WaitForStabilityAsync(timeoutMs: 10_000);
    Console.WriteLine(
        $"[SoakTest] 안정화 완료  sessions={finalSnap.Sessions}  " +
        $"received={finalSnap.Received:N0}  sent={stats.Sent:N0}");
}

// ── 판정 ─────────────────────────────────────────────────────────────────────
bool crashed = server?.Crashed ?? false;
var report   = SoakReport.Evaluate(stats, finalSnap, baselineHeap, crashed, opt.Attach);
report.Print();

// ── 서버 종료 ────────────────────────────────────────────────────────────────
if (server is not null)
{
    Console.WriteLine("[SoakTest] 서버 종료 중...");
    await server.DisposeAsync();
}

// 종료코드: 0=PASS, 1=FAIL
Environment.Exit(report.OverallPass ? 0 : 1);
