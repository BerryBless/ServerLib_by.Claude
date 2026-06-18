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
/// Server.exe 자식 프로세스를 구동하고 stdout의 [STATS] 라인을 파싱합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="Latest"/>·<see cref="Crashed"/>는 volatile 필드 기반으로 Thread-safe.
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

    private ServerProcess(Process proc) => _proc = proc;

    /// <summary>서버가 우리가 'q'를 보내기 전에 예기치 않게 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crashed => _proc.HasExited && !_shutdownRequested;

    /// <summary>마지막으로 파싱한 [STATS] 스냅샷입니다.</summary>
    public ServerStatsSnapshot Latest => _latest;

    /// <summary>
    /// Server.exe를 자식 프로세스로 구동합니다.
    /// </summary>
    /// <param name="port">게임 서버 포트입니다.</param>
    /// <param name="adminPort">관리 포트입니다.</param>
    /// <param name="monitorIntervalSec">서버 모니터 출력 주기(초)입니다.</param>
    /// <returns>구동된 래퍼 인스턴스. exe를 찾지 못하거나 시작 실패 시 <see langword="null"/>.</returns>
    /// <remarks>
    /// exe 탐색 순서: <c>AppContext.BaseDirectory/../../../..</c>에서 Release → Debug 순.
    /// 둘 다 없으면 빌드 안내 메시지를 출력하고 null을 반환합니다.
    /// </remarks>
    public static ServerProcess? TryStart(int port, int adminPort, int monitorIntervalSec)
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

        var psi = new ProcessStartInfo(exePath,
            $"--Server:Port={port} " +
            $"--Server:AdminPort={adminPort} " +
            $"--Server:MonitorIntervalSeconds={monitorIntervalSec}")
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
        // stdout 비동기 읽기 Task: [STATS] 라인 파싱 — 메인 루프와 독립 실행
        _ = Task.Run(sp.ReadStdoutAsync);
        return sp;
    }

    // ── 서버 exe 탐색 ───────────────────────────────────────
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

    // ── stdout 비동기 읽기 ───────────────────────────────────
    private async Task ReadStdoutAsync()
    {
        try
        {
            // ReadLineAsync(): Pipe 기반 비동기 — IO 스레드 블로킹 없이 서버 출력 소비
            while (await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                // [STATS]/[Monitor]/연결 이벤트([+]/[-])는 매우 빈번 → 노이즈 억제
                bool suppress = line.StartsWith("[STATS]", StringComparison.Ordinal) ||
                                line.StartsWith("[Monitor]", StringComparison.Ordinal) ||
                                line.StartsWith("[+]", StringComparison.Ordinal) ||
                                line.StartsWith("[-]", StringComparison.Ordinal);

                if (!suppress)
                    Console.WriteLine($"  [Server] {line}");

                if (line.StartsWith("[STATS]", StringComparison.Ordinal))
                {
                    _latest  = ParseStats(line);
                    _hasStats = true;
                }
            }
        }
        catch
        {
            // 프로세스 종료 시 스트림 닫힘 → 정상 종료로 간주, 무시
        }
    }

    // ── [STATS] 파싱 ────────────────────────────────────────
    // 형식: [STATS] received=123 hp=456 gen=0 sessions=5 heapBytes=789 ...
    private static ServerStatsSnapshot ParseStats(string line)
    {
        long received  = ParseLong(line, "received=");
        long sessions  = ParseLong(line, "sessions=");
        long heapBytes = ParseLong(line, "heapBytes=");
        return new ServerStatsSnapshot(received, sessions, heapBytes);
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

    // ── 준비 대기 ────────────────────────────────────────────
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
            if (_hasStats)   return true;
            if (_proc.HasExited) return false; // 시작 직후 크래시
            await Task.Delay(200);
        }
        return false;
    }

    // ── 안정화 대기 ──────────────────────────────────────────
    /// <summary>
    /// 클라이언트 전원 종료 후, 서버 sessions==0이고 received가 안정될 때까지 폴링합니다.
    /// </summary>
    /// <param name="timeoutMs">최대 대기 시간(밀리초). 기본 10초.</param>
    /// <returns>안정화된 최종 스냅샷입니다. 타임아웃 시 마지막 스냅샷을 반환합니다.</returns>
    /// <remarks>
    /// <b>[중요 — 종료 순서:]</b> 이 메서드는 반드시 서버에 'q'를 보내기 <b>전에</b> 호출해야 합니다.
    /// Stop() 이후에는 Pipe 버퍼가 폐기되어 in-flight 패킷이 유실되므로 false DataLoss 판정이 발생합니다.
    /// </remarks>
    public async Task<ServerStatsSnapshot> WaitForStabilityAsync(int timeoutMs = 10_000)
    {
        long deadline    = Environment.TickCount64 + timeoutMs;
        long prevReceived= -1;
        int  stableCount = 0;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(500);
            var snap = _latest;

            // sessions==0: 전 클라 세션 해제 확인
            // received 안정: in-flight 패킷 처리 완료 확인
            if (snap.Sessions == 0 && snap.Received == prevReceived)
            {
                if (++stableCount >= 3) return snap; // 3연속(~1.5초) 안정 → 확정
            }
            else
            {
                stableCount = 0;
            }
            prevReceived = snap.Received;
        }

        return _latest; // 타임아웃: 현재 스냅샷 반환(판정은 SoakReport에서 처리)
    }

    // ── graceful 종료 ────────────────────────────────────────
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
