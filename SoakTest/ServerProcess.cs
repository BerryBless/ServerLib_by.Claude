using System.Diagnostics;

namespace SoakTest;

/// <summary>서버 stdout에서 파싱한 [STATS] 스냅샷입니다.</summary>
/// <param name="Received">서버가 수신한 총 패킷 수입니다.</param>
/// <param name="Sessions">현재 활성 세션 수입니다.</param>
/// <param name="HeapBytes">관리 힙 사용량(바이트)입니다.</param>
public sealed record ServerStatsSnapshot(
    long Received  = 0,
    long Sessions  = 0,
    long HeapBytes = 0);

/// <summary>
/// 서버 stdout에서 파싱한 [TICKET] KPI 스냅샷입니다.
/// 티켓팅 모드 전용 — 비티켓팅 모드에서는 null입니다.
/// </summary>
/// <remarks>
/// <b>[KPI 보존식]</b> 안정화 후 다음이 성립해야 합니다:
/// <c>TotalReserved == TotalConfirmed + TotalPaymentFailed + TotalAbandoned + TotalExpired + Reserved</c>
/// </remarks>
/// <param name="Free">현재 여유 좌석 수입니다.</param>
/// <param name="Reserved">현재 예약 중(결제 전) 좌석 수입니다.</param>
/// <param name="Sold">현재 판매 완료 좌석 수입니다.</param>
/// <param name="TotalReserved">누적 예약 시도 수입니다.</param>
/// <param name="TotalConfirmed">누적 결제 완료 수입니다.</param>
/// <param name="TotalPaymentFailed">누적 결제 실패 수입니다.</param>
/// <param name="TotalAbandoned">누적 graceful-FIN 반납 수입니다.</param>
/// <param name="TotalExpired">누적 TTL 만료 반납 수입니다.</param>
/// <param name="TotalSeatTaken">누적 SeatTaken 응답 수입니다.</param>
public sealed record TicketSnapshot(
    long Free              = 0,
    long Reserved          = 0,
    long Sold              = 0,
    long TotalReserved     = 0,
    long TotalConfirmed    = 0,
    long TotalPaymentFailed = 0,
    long TotalAbandoned    = 0,
    long TotalExpired      = 0,
    long TotalSeatTaken    = 0);

/// <summary>
/// 티켓팅 모드로 서버를 시작할 때 자식 프로세스에 주입할 구성입니다.
/// </summary>
/// <param name="Rows">좌석 그리드 행 수입니다.</param>
/// <param name="Cols">좌석 그리드 열 수입니다.</param>
/// <param name="SeatsPerSession">세션당 최대 예약 좌석 수입니다.</param>
/// <param name="TtlSeconds">예약 TTL(초)입니다.</param>
/// <param name="PaymentDelayMs">결제 처리 지연(밀리초)입니다.</param>
/// <param name="MaxConnectionsPerIp">IP당 최대 동시 연결 수입니다. clients ≥ 기본(50) 시 오버라이드가 필요합니다.</param>
public readonly record struct TicketingStartConfig(
    int Rows, int Cols, int SeatsPerSession,
    int TtlSeconds, int PaymentDelayMs, int MaxConnectionsPerIp);

/// <summary>
/// Server.exe 자식 프로세스를 구동하고 stdout의 [STATS]·[TICKET] 라인을 파싱합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="Latest"/>·<see cref="LatestTicket"/>·<see cref="Crashed"/>는
/// volatile 필드 기반으로 Thread-safe.
/// <see cref="WaitForReadinessAsync"/>·<see cref="WaitForStabilityAsync"/>는 단일 Task에서 호출합니다.
/// <b>[Memory:]</b> stdout 읽기는 별도 Task로 비동기 처리 — 메인 루프를 블로킹하지 않습니다.
/// <b>[Blocking:]</b> <see cref="DisposeAsync"/>는 최대 5초 비동기 대기 후 Kill 보장.
/// </remarks>
public sealed class ServerProcess : IAsyncDisposable
{
    // Process: child Server.exe stdin/stdout 재지향 — CLI 오버라이드로 포트·모니터 주기 제어
    private readonly Process _proc;

    // volatile bool: _shutdownRequested를 Reader/Writer 스레드 간 가시성 보장 — 단일 비트 플래그
    private volatile bool _shutdownRequested;

    // volatile bool: stdout 파싱 Task가 쓰고 readiness 폴러가 읽음 — 첫 [STATS] 수신 플래그
    private volatile bool _hasStats;

    // volatile object: 불변 record 참조 교체 — 64비트에서 참조 쓰기/읽기는 원자적
    // stdout 파싱 Task(Writer)와 reporter·판정 코드(Reader)가 교차 접근
    private volatile ServerStatsSnapshot _latest = new();

    // volatile object: [TICKET] KPI 라인 파싱 결과. 티켓팅 모드 전용 — 비티켓팅에서는 업데이트 안 됨.
    // 판별 토큰 "reserved_total=" 존재 라인에서만 갱신 (이벤트 로그 라인과 혼동 방지)
    private volatile TicketSnapshot? _latestTicket;

    private ServerProcess(Process proc) => _proc = proc;

    /// <summary>서버가 우리가 'q'를 보내기 전에 예기치 않게 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crashed => _proc.HasExited && !_shutdownRequested;

    /// <summary>마지막으로 파싱한 [STATS] 스냅샷입니다.</summary>
    public ServerStatsSnapshot Latest => _latest;

    /// <summary>
    /// 마지막으로 파싱한 [TICKET] KPI 스냅샷입니다.
    /// 티켓팅 모드가 아니거나 아직 [TICKET] KPI 라인을 수신하지 않은 경우 null입니다.
    /// </summary>
    public TicketSnapshot? LatestTicket => _latestTicket;

    /// <summary>
    /// Server.exe를 자식 프로세스로 구동합니다.
    /// </summary>
    /// <param name="port">게임 서버 포트입니다.</param>
    /// <param name="adminPort">관리 포트입니다.</param>
    /// <param name="monitorIntervalSec">서버 모니터 출력 주기(초)입니다.</param>
    /// <param name="ticket">
    /// 티켓팅 모드 구성입니다. null이면 티켓팅 오버라이드를 주입하지 않습니다(DamageWorkload 기본).
    /// </param>
    /// <returns>구동된 래퍼 인스턴스. exe를 찾지 못하거나 시작 실패 시 <see langword="null"/>.</returns>
    /// <remarks>
    /// exe 탐색 순서: <c>AppContext.BaseDirectory/../../../..</c>에서 Release → Debug 순.
    /// 둘 다 없으면 빌드 안내 메시지를 출력하고 null을 반환합니다.
    /// </remarks>
    public static ServerProcess? TryStart(
        int port, int adminPort, int monitorIntervalSec,
        TicketingStartConfig? ticket = null)
    {
        string? exePath = FindServerExe();
        if (exePath is null)
        {
            Console.Error.WriteLine("[SoakTest] Server.exe를 찾을 수 없습니다.");
            Console.Error.WriteLine("  먼저 빌드하세요: dotnet build -c Release --project Server");
            Console.Error.WriteLine("  또는 --attach 옵션으로 외부 서버에 부착하세요.");
            return null;
        }

        Console.WriteLine($"[SoakTest] 서버 구동: {exePath}");

        // 기본 인자
        var sb = new System.Text.StringBuilder();
        sb.Append($"--Server:Port={port} ");
        sb.Append($"--Server:AdminPort={adminPort} ");
        sb.Append($"--Server:MonitorIntervalSeconds={monitorIntervalSec}");

        // 티켓팅 오버라이드 주입
        if (ticket.HasValue)
        {
            var t = ticket.Value;
            sb.Append($" --Server:Features:EnableTicketing=true");
            sb.Append($" --Server:Ticket:Rows={t.Rows}");
            sb.Append($" --Server:Ticket:Cols={t.Cols}");
            sb.Append($" --Server:Ticket:MaxSeatsPerSession={t.SeatsPerSession}");
            sb.Append($" --Server:Ticket:ReservationTtlSeconds={t.TtlSeconds}");
            sb.Append($" --Server:Ticket:PaymentDelayMs={t.PaymentDelayMs}");
            // MaxConnectionsPerIp 오버라이드: 전 클라가 127.0.0.1 → 기본 50 초과 시 연결 거부 방지
            sb.Append($" --Server:MaxConnectionsPerIp={t.MaxConnectionsPerIp}");
        }

        var psi = new ProcessStartInfo(exePath, sb.ToString())
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false, // stderr는 리디렉션하지 않아 콘솔에 그대로 표시
            UseShellExecute = false,
            CreateNoWindow  = true,
        };

        var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine("[SoakTest] Process.Start 실패");
            return null;
        }

        var sp = new ServerProcess(proc);
        // stdout 비동기 읽기 Task: [STATS]·[TICKET] 라인 파싱 — 메인 루프와 독립 실행
        _ = Task.Run(sp.ReadStdoutAsync);
        return sp;
    }

    // ── 서버 exe 탐색 ───────────────────────────────────────────────────────────
    private static string? FindServerExe()
    {
        // AppContext.BaseDirectory 기준 4단계 상위 = 솔루션 루트
        // Debug 빌드:   SoakTest/bin/Debug/net10.0/   → ../../../../
        // Release 빌드: SoakTest/bin/Release/net10.0/ → ../../../../
        string solutionRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        string[] candidates =
        [
            Path.Combine(solutionRoot, "Server", "bin", "Release", "net10.0", "Server.exe"),
            Path.Combine(solutionRoot, "Server", "bin", "Debug",   "net10.0", "Server.exe"),
        ];

        return Array.Find(candidates, File.Exists);
    }

    // ── stdout 비동기 읽기 ───────────────────────────────────────────────────────
    private async Task ReadStdoutAsync()
    {
        try
        {
            // ReadLineAsync(): Pipe 기반 비동기 — IO 스레드 블로킹 없이 서버 출력 소비
            while (await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                // 빈번한 라인들은 노이즈 억제
                bool suppress = line.StartsWith("[STATS]",  StringComparison.Ordinal) ||
                                line.StartsWith("[TICKET]", StringComparison.Ordinal) ||
                                line.StartsWith("[Monitor]", StringComparison.Ordinal) ||
                                line.StartsWith("[+]", StringComparison.Ordinal) ||
                                line.StartsWith("[-]", StringComparison.Ordinal);

                if (!suppress)
                    Console.WriteLine($"  [Server] {line}");

                if (line.StartsWith("[STATS]", StringComparison.Ordinal))
                {
                    _latest   = ParseStats(line);
                    _hasStats = true;
                }
                else if (line.StartsWith("[TICKET]", StringComparison.Ordinal))
                {
                    // [TICKET] 라인은 두 종류:
                    //   (1) 이벤트 로그: "[TICKET] ip  user=X  reserve=OK  seats=[A1]  free=40"
                    //   (2) KPI 라인:    "[TICKET] free=F reserved=R sold=S reserved_total=RT ..."
                    // 판별: "reserved_total=" 토큰 존재 여부로만 구분.
                    // (이벤트 라인도 free= 포함하므로 free=만으로 판별하면 오파싱 → reserved=0 누수 마스킹)
                    if (line.Contains("reserved_total=", StringComparison.Ordinal))
                        _latestTicket = ParseTicket(line);
                }
            }
        }
        catch
        {
            // 프로세스 종료 시 스트림 닫힘 → 정상 종료로 간주, 무시
        }
    }

    // ── [STATS] 파싱 ─────────────────────────────────────────────────────────────
    // 형식: [STATS] received=123 hp=456 gen=0 sessions=5 heapBytes=789 ...
    private static ServerStatsSnapshot ParseStats(string line)
    {
        long received  = ParseLong(line, "received=");
        long sessions  = ParseLong(line, "sessions=");
        long heapBytes = ParseLong(line, "heapBytes=");
        return new ServerStatsSnapshot(received, sessions, heapBytes);
    }

    // ── [TICKET] KPI 파싱 ────────────────────────────────────────────────────────
    // 형식: [TICKET] free=F reserved=R sold=S reserved_total=RT confirmed=C payfail=PF abandon=AB expired=EX seattaken=ST
    private static TicketSnapshot ParseTicket(string line)
    {
        long free        = ParseLong(line, "free=");
        long reserved    = ParseLong(line, "reserved=");
        long sold        = ParseLong(line, "sold=");
        long totalRes    = ParseLong(line, "reserved_total=");
        long confirmed   = ParseLong(line, "confirmed=");
        long payFail     = ParseLong(line, "payfail=");
        long abandon     = ParseLong(line, "abandon=");
        long expired     = ParseLong(line, "expired=");
        long seatTaken   = ParseLong(line, "seattaken=");
        return new TicketSnapshot(free, reserved, sold, totalRes, confirmed, payFail, abandon, expired, seatTaken);
    }

    private static long ParseLong(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        int start = idx + key.Length;
        int end   = line.IndexOf(' ', start);
        if (end < 0) end = line.Length;
        return long.TryParse(line.AsSpan(start, end - start), out long v) ? v : 0;
    }

    // ── 준비 대기 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// 서버가 첫 [STATS] 라인을 출력할 때까지 비동기로 대기합니다.
    /// </summary>
    /// <param name="timeoutMs">최대 대기 시간(밀리초). 기본 10초.</param>
    /// <returns>준비 완료이면 true, 타임아웃 또는 조기 종료이면 false.</returns>
    public async Task<bool> WaitForReadinessAsync(int timeoutMs = 10_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_hasStats)       return true;
            if (_proc.HasExited) return false; // 시작 직후 크래시
            await Task.Delay(200);
        }
        return false;
    }

    // ── 안정화 대기 ──────────────────────────────────────────────────────────────
    /// <summary>
    /// 클라이언트 전원 종료 후, 서버가 안정될 때까지 폴링합니다.
    /// </summary>
    /// <param name="timeoutMs">
    /// 최대 대기 시간(밀리초). 티켓팅 모드에서는 <c>max(10s, TTL*1000 + 5000)</c>를 권장합니다.
    /// </param>
    /// <param name="requireTicketDrained">
    /// true이면 <see cref="TicketSnapshot.Reserved"/> == 0 이 될 때까지 추가 대기합니다.
    /// TTL-expire 사이클이 있을 때 서버 스위퍼 정리 시간을 확보하기 위해 사용합니다.
    /// graceful 반납은 즉시 이루어지므로 graceful 전용 워크로드에서는 false여도 됩니다.
    /// </param>
    /// <returns>안정화된 최종 스냅샷입니다. 타임아웃 시 마지막 스냅샷을 반환합니다.</returns>
    /// <remarks>
    /// <b>[중요 — 종료 순서:]</b> 이 메서드는 반드시 서버에 'q'를 보내기 <b>전에</b> 호출해야 합니다.
    /// Stop() 이후에는 Pipe 버퍼가 폐기되어 in-flight 패킷이 유실되므로 false DataLoss 판정이 발생합니다.
    /// </remarks>
    public async Task<ServerStatsSnapshot> WaitForStabilityAsync(
        int timeoutMs = 10_000,
        bool requireTicketDrained = false)
    {
        long deadline     = Environment.TickCount64 + timeoutMs;
        long prevReceived = -1;
        long prevTotalRes = -1;
        int  stableCount  = 0;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(500);
            var snap   = _latest;
            var ticket = _latestTicket;

            // 기본 조건: sessions==0 && received 안정
            bool sessionsEmpty    = snap.Sessions == 0;
            bool receivedStable   = snap.Received == prevReceived;

            // 티켓팅 추가 조건: reserved==0 && reserved_total 안정
            // - graceful 반납: ReleaseAllByContext → 즉시. TTL 무관.
            // - TTL-expire 반납: SweepExpired(~1s 주기). → TTL 경과 후 해소.
            //   requireTicketDrained 는 expire 사이클이 있을 때 true로 설정.
            bool ticketDrained = !requireTicketDrained
                || (ticket != null && ticket.Reserved == 0 && ticket.TotalReserved == prevTotalRes);

            if (sessionsEmpty && receivedStable && ticketDrained)
            {
                if (++stableCount >= 3) return snap; // 3연속(~1.5초) 안정 → 확정
            }
            else
            {
                stableCount = 0;
            }

            prevReceived = snap.Received;
            prevTotalRes = ticket?.TotalReserved ?? prevTotalRes;
        }

        return _latest; // 타임아웃: 현재 스냅샷 반환(판정은 SoakReport에서 처리)
    }

    // ── graceful 종료 ────────────────────────────────────────────────────────────
    /// <summary>
    /// stdin에 'q'를 보내 서버를 graceful 종료하고 프로세스 리소스를 해제합니다.
    /// 5초 내 종료되지 않으면 강제 Kill합니다.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _shutdownRequested = true; // Crashed 속성이 false를 반환하도록 플래그 설정

        if (_proc.HasExited)
        {
            _proc.Dispose();
            return;
        }

        try
        {
            // stdin에 'q' 전송 → Server/Program.cs의 Console.ReadLine() 루프 탈출 신호
            await _proc.StandardInput.WriteLineAsync("q");
            await _proc.StandardInput.FlushAsync();
        }
        catch
        {
            // 이미 종료됐을 수 있음 — 무시
        }

        // WaitForExitAsync: 비동기 대기 — 5초 내 정상 종료 기대
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[SoakTest] 서버 5초 내 종료 미완 → 강제 Kill");
            _proc.Kill(entireProcessTree: true);
        }

        _proc.Dispose();
    }
}
