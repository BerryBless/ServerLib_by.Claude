using SoakTest;
using SoakTest.Workloads;

// ── CLI 파싱 ─────────────────────────────────────────────────────────────────
var opt = SoakOptions.Parse(args);

bool isTicketing = opt.Workload == WorkloadType.Ticketing;

Console.WriteLine(
    $"[SoakTest] 소크 테스트 시작 — workload={opt.Workload}  clients={opt.Clients}  " +
    $"port={opt.Port}  churnDelay={opt.ChurnDelayMs}ms  report={opt.ReportIntervalSec}s  attach={opt.Attach}");

if (isTicketing)
{
    Console.WriteLine(
        $"[SoakTest] [티켓팅] rows={opt.Rows}×cols={opt.Cols}={opt.Rows * opt.Cols}석  " +
        $"K={opt.SeatsPerSession}  ttl={opt.TtlSeconds}s  payDelay={opt.PaymentDelayMs}ms  " +
        $"contention={opt.Contention}  abandon={opt.AbandonRate:P0}  expire={opt.ExpireRate:P0}");
}
else
{
    Console.WriteLine(
        $"[SoakTest] [Damage] sends={opt.SendsPerConn}  settle={opt.ReceiveSettleMs}ms");
}

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
    // 티켓팅 모드: 서버에 티켓팅 config 오버라이드 주입
    // MaxConnectionsPerIp: 전 클라가 127.0.0.1 → 기본 50 초과 시 거부 → 클라 수 × 2로 여유 확보
    TicketingStartConfig? ticketCfg = isTicketing
        ? new TicketingStartConfig(
            opt.Rows, opt.Cols, opt.SeatsPerSession,
            opt.TtlSeconds, opt.PaymentDelayMs,
            MaxConnectionsPerIp: Math.Max(opt.Clients * 2, 100))
        : null;

    server = ServerProcess.TryStart(opt.Port, opt.AdminPort, monitorIntervalSec: 1, ticketCfg);
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
// i: Select 파라미터이므로 각 람다가 고유한 i를 캡처 → clientId 충돌 없음
var clientTasks = Enumerable.Range(0, opt.Clients)
    .Select(i => Task.Run(async () =>
    {
        // 워크로드 전략 선택: 연결 수명은 SoakClient, "무엇을 보낼 것인가"는 IWorkload
        IWorkload workload = isTicketing
            ? new TicketingWorkload(
                clientId:       i,
                seatsPerSession: opt.SeatsPerSession,
                totalRows:       opt.Rows,
                totalCols:       opt.Cols,
                paymentDelayMs:  opt.PaymentDelayMs,
                abandonRate:     opt.AbandonRate,
                expireRate:      opt.ExpireRate,
                ttlSeconds:      opt.TtlSeconds,
                loginSettleMs:   opt.LoginSettleMs,
                pattern:         opt.Contention,
                stats:           stats)
            : new DamageWorkload(opt.SendsPerConn, opt.ReceiveSettleMs, stats);

        var client = new SoakClient("127.0.0.1", opt.Port, opt.ChurnDelayMs, stats, workload);
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

        var snap   = server?.Latest;
        var ticket = server?.LatestTicket;

        Console.Write(
            $"[PROGRESS]  cycles={stats.Cycles:N0}  conns={stats.Connects:N0}  " +
            $"sent={stats.Sent:N0}  recv={stats.Received:N0}  errs={stats.Errors}  " +
            $"serverSessions={snap?.Sessions ?? -1}  " +
            $"serverRecv={snap?.Received ?? -1:N0}  " +
            $"heap={(snap?.HeapBytes ?? 0) / 1024:N0}KB");

        // 티켓팅 모드 추가 출력
        if (isTicketing && ticket is not null)
        {
            Console.Write(
                $"  | free={ticket.Free} res={ticket.Reserved} sold={ticket.Sold}" +
                $"  rsv={stats.ReserveSent:N0} pay={stats.PaySent:N0}" +
                $" abn={stats.AbandonCycles:N0} exp={stats.ExpireCycles:N0}");
        }

        Console.WriteLine();
    }
});

// ── 클라이언트 완료 대기 ─────────────────────────────────────────────────────
// cts 취소 → 각 SoakClient.RunAsync가 루프 탈출 → Task.WhenAll 반환
await Task.WhenAll(clientTasks);
Console.WriteLine("[SoakTest] 모든 클라이언트 종료 완료");

// ── 서버 안정화 대기 ─────────────────────────────────────────────────────────
// 중요: 반드시 서버에 'q'를 보내기 전에 권위 read를 수행해야 한다.
// Stop() 이후 Pipe 버퍼가 폐기되면 in-flight 패킷이 유실 → false DataLoss / SlotLeak 판정 발생.
ServerStatsSnapshot? finalSnap = null;
if (server is not null)
{
    // 티켓팅 모드: TTL-expire 사이클이 있으면 TTL + 여유 시간 확보
    // - graceful 반납은 즉시(ReleaseAllByContext) → TTL 무관.
    // - TTL-expire 반납은 스위퍼(~1s 주기) 처리 → TTL 경과 후 해소.
    int stabilityTimeoutMs = isTicketing
        ? Math.Max(10_000, opt.TtlSeconds * 1000 + 5_000)
        : 10_000;

    bool requireDrained = isTicketing; // expire 사이클이 없어도 안전하게 true

    Console.WriteLine("[SoakTest] 서버 안정화 대기 중...");
    finalSnap = await server.WaitForStabilityAsync(stabilityTimeoutMs, requireDrained);
    Console.WriteLine(
        $"[SoakTest] 안정화 완료  sessions={finalSnap.Sessions}  " +
        $"received={finalSnap.Received:N0}  sent={stats.Sent:N0}");

    if (isTicketing && server.LatestTicket is { } ts)
    {
        Console.WriteLine(
            $"[SoakTest] 최종 티켓 상태  free={ts.Free} reserved={ts.Reserved} sold={ts.Sold}" +
            $"  totalReserved={ts.TotalReserved}  confirmed={ts.TotalConfirmed}");
    }
}

// 최종 티켓 스냅샷 (권위 read: 'q' 이전에 확보)
TicketSnapshot? finalTicketSnap = isTicketing ? server?.LatestTicket : null;

// ── 판정 ─────────────────────────────────────────────────────────────────────
bool crashed = server?.Crashed ?? false;
var report = SoakReport.Evaluate(
    stats,
    finalSnap,
    baselineHeap,
    crashed,
    opt.Attach,
    isTicketing:  isTicketing,
    ticketSnap:   finalTicketSnap,
    totalSeats:   opt.Rows * opt.Cols);
report.Print();

// ── 서버 종료 ────────────────────────────────────────────────────────────────
if (server is not null)
{
    Console.WriteLine("[SoakTest] 서버 종료 중...");
    await server.DisposeAsync();
}

// 종료코드: 0=PASS, 1=FAIL
Environment.Exit(report.OverallPass ? 0 : 1);
