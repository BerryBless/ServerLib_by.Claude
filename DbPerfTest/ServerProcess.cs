using System.Diagnostics;

namespace DbPerfTest;

/// <summary>[STATS] 라인에서 파싱한 서버 스냅샷입니다.</summary>
public sealed record ServerStatsSnapshot(
    long Received  = 0,
    long Sessions  = 0,
    long HeapBytes = 0);

/// <summary>[DBSTATS] 라인에서 파싱한 DB 연산 평균 지연 스냅샷입니다.</summary>
public sealed record DbStatsSnapshot(
    long MysqlSelectAvgUs = 0,
    long RedisGetAvgUs    = 0,
    long RedisSetAvgUs    = 0,
    long MysqlCount       = 0,
    long RedisGetCount    = 0,
    long RedisSetCount    = 0);

/// <summary>
/// Server.exe 자식 프로세스를 구동하고 stdout의 [STATS]·[DBSTATS] 라인을 파싱합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="Latest"/>·<see cref="LatestDb"/>·<see cref="Crashed"/>는
/// volatile 필드 기반으로 Thread-safe.
/// <see cref="WaitForReadinessAsync"/>·<see cref="WaitForStabilityAsync"/>는 단일 Task에서 호출합니다.
/// <b>[Memory:]</b> stdout 읽기는 별도 Task로 비동기 처리 — 메인 루프 블로킹 없음.
/// <b>[Blocking:]</b> <see cref="DisposeAsync"/>는 최대 5초 비동기 대기 후 Kill 보장.
/// </remarks>
public sealed class ServerProcess : IAsyncDisposable
{
    // Process: child Server.exe stdin/stdout 재지향 — CLI 오버라이드로 포트·모니터 주기 제어
    private readonly Process _proc;

    // volatile bool: _shutdownRequested — IO 스레드 간 가시성 보장
    private volatile bool _shutdownRequested;

    // volatile bool: 첫 [STATS] 수신 플래그 — readiness 폴러가 읽음
    private volatile bool _hasStats;

    // volatile object: 불변 record 참조 교체 — 64비트에서 참조 쓰기는 원자적
    private volatile ServerStatsSnapshot _latest = new();

    // volatile object: [DBSTATS] 최신 스냅샷. 비계측 모드에서는 업데이트 안 됨.
    private volatile DbStatsSnapshot? _latestDb;

    private ServerProcess(Process proc) => _proc = proc;

    /// <summary>서버가 우리가 'q'를 보내기 전에 예기치 않게 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crashed => _proc.HasExited && !_shutdownRequested;

    /// <summary>마지막으로 파싱한 [STATS] 스냅샷입니다.</summary>
    public ServerStatsSnapshot Latest => _latest;

    /// <summary>
    /// 마지막으로 파싱한 [DBSTATS] 스냅샷입니다.
    /// [DBSTATS] 미수신 시 null입니다.
    /// </summary>
    public DbStatsSnapshot? LatestDb => _latestDb;

    /// <summary>Server.exe를 자식 프로세스로 구동합니다.</summary>
    /// <param name="port">게임 서버 포트입니다.</param>
    /// <param name="adminPort">관리 포트입니다.</param>
    /// <param name="monitorIntervalSec">서버 모니터 출력 주기(초)입니다.</param>
    /// <param name="opt">DbPerfTest CLI 옵션입니다.</param>
    /// <returns>구동된 래퍼 인스턴스. exe를 찾지 못하거나 시작 실패 시 <see langword="null"/>.</returns>
    /// <remarks>
    /// exe 탐색 순서: <c>AppContext.BaseDirectory/../../../..</c>에서 Release → Debug 순.
    /// 둘 다 없으면 빌드 안내 메시지를 출력하고 null을 반환합니다.
    /// </remarks>
    public static ServerProcess? TryStart(int port, int adminPort, int monitorIntervalSec, DbPerfOptions opt)
    {
        string? exePath = FindServerExe();
        if (exePath is null)
        {
            Console.Error.WriteLine("[DbPerfTest] Server.exe를 찾을 수 없습니다.");
            Console.Error.WriteLine("  먼저 빌드하세요: dotnet build -c Release --project Server");
            Console.Error.WriteLine("  또는 --attach 옵션으로 외부 서버에 부착하세요.");
            return null;
        }

        Console.WriteLine($"[DbPerfTest] 서버 구동: {exePath}");

        // EnableLogin=true: MySQL+Redis 계측 경로 활성화 필수
        // SeedTestUser=true: 최초 기동 시 테스트 유저 자동 삽입
        // ArgumentList: 각 인자를 독립 argv — 공백 포함 연결 문자열도 분리되지 않음
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,  // stderr는 리디렉션하지 않아 콘솔에 그대로 표시
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        psi.ArgumentList.Add($"--Server:Port={port}");
        psi.ArgumentList.Add($"--Server:AdminPort={adminPort}");
        psi.ArgumentList.Add($"--Server:MonitorIntervalSeconds={monitorIntervalSec}");
        psi.ArgumentList.Add("--Server:Features:EnableLogin=true");
        psi.ArgumentList.Add("--Server:Auth:SeedTestUser=true");
        // MaxConnectionsPerIp: 모든 클라가 127.0.0.1 → 기본값 초과 방지
        psi.ArgumentList.Add($"--Server:MaxConnectionsPerIp={Math.Max(opt.Clients * 2, 100)}");

        if (opt.RedisConn is { } rc)
            psi.ArgumentList.Add($"--Server:Auth:RedisConnectionString={rc}");
        if (opt.MySqlConn is { } mc)
            psi.ArgumentList.Add($"--Server:Auth:MySqlConnectionString={mc}");
        if (opt.PbkdfIterations is { } pi)
            psi.ArgumentList.Add($"--Server:Auth:PbkdfIterations={pi}");
        if (opt.Username != "admin")
            psi.ArgumentList.Add($"--Server:Auth:SeedUsername={opt.Username}");
        if (opt.Password != "password123")
            psi.ArgumentList.Add($"--Server:Auth:SeedPassword={opt.Password}");

        var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine("[DbPerfTest] Process.Start 실패");
            return null;
        }

        var sp = new ServerProcess(proc);
        // stdout 비동기 읽기 Task: [STATS]·[DBSTATS] 라인 파싱 — 메인 루프와 독립 실행
        _ = Task.Run(sp.ReadStdoutAsync);
        return sp;
    }

    private static string? FindServerExe()
    {
        // AppContext.BaseDirectory 기준 4단계 상위 = 솔루션 루트
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] candidates =
        [
            Path.Combine(root, "Server", "bin", "Release", "net10.0", "Server.exe"),
            Path.Combine(root, "Server", "bin", "Debug",   "net10.0", "Server.exe"),
        ];
        return Array.Find(candidates, File.Exists);
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            // ReadLineAsync(): Pipe 기반 비동기 — IO 스레드 블로킹 없이 서버 출력 소비
            while (await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                bool suppress = line.StartsWith("[STATS]",   StringComparison.Ordinal) ||
                                line.StartsWith("[DBSTATS]", StringComparison.Ordinal) ||
                                line.StartsWith("[Monitor]", StringComparison.Ordinal);

                if (!suppress)
                    Console.WriteLine($"  [Server] {line}");

                if (line.StartsWith("[STATS]", StringComparison.Ordinal))
                {
                    _latest   = ParseStats(line);
                    _hasStats = true;
                }
                else if (line.StartsWith("[DBSTATS]", StringComparison.Ordinal))
                {
                    _latestDb = ParseDbStats(line);
                }
            }
        }
        catch { /* 프로세스 종료 시 스트림 닫힘 — 정상 종료 */ }
    }

    // 형식: [STATS] received=N sessions=N heapBytes=N ...
    private static ServerStatsSnapshot ParseStats(string line) => new(
        ParseLong(line, "received="),
        ParseLong(line, "sessions="),
        ParseLong(line, "heapBytes="));

    // 형식: [DBSTATS] mysqlSelectAvgUs=N redisGetAvgUs=N redisSetAvgUs=N mysqlCount=N redisGetCount=N redisSetCount=N
    private static DbStatsSnapshot ParseDbStats(string line) => new(
        ParseLong(line, "mysqlSelectAvgUs="),
        ParseLong(line, "redisGetAvgUs="),
        ParseLong(line, "redisSetAvgUs="),
        ParseLong(line, "mysqlCount="),
        ParseLong(line, "redisGetCount="),
        ParseLong(line, "redisSetCount="));

    private static long ParseLong(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        int start = idx + key.Length;
        int end   = line.IndexOf(' ', start);
        if (end < 0) end = line.Length;
        return long.TryParse(line.AsSpan(start, end - start), out long v) ? v : 0;
    }

    /// <summary>첫 [STATS] 수신까지 비동기로 대기합니다.</summary>
    /// <param name="timeoutMs">최대 대기 시간(밀리초). 기본 15초.</param>
    /// <returns>준비 완료이면 true, 타임아웃 또는 조기 종료이면 false.</returns>
    public async Task<bool> WaitForReadinessAsync(int timeoutMs = 15_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_hasStats)       return true;
            if (_proc.HasExited) return false;
            await Task.Delay(200);
        }
        return false;
    }

    /// <summary>클라이언트 전원 종료 후 서버 안정화(sessions==0)를 대기하고 최종 스냅샷을 반환합니다.</summary>
    /// <param name="timeoutMs">최대 대기 시간(밀리초). 기본 10초.</param>
    /// <returns>안정화된 최종 스냅샷. 타임아웃 시 마지막 스냅샷을 반환합니다.</returns>
    public async Task<ServerStatsSnapshot> WaitForStabilityAsync(int timeoutMs = 10_000)
    {
        long deadline     = Environment.TickCount64 + timeoutMs;
        long prevReceived = -1;
        int  stableCount  = 0;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(500);
            var snap = _latest;
            bool empty  = snap.Sessions == 0;
            bool stable = snap.Received == prevReceived;
            if (empty && stable)
            {
                if (++stableCount >= 3) return snap;
            }
            else stableCount = 0;
            prevReceived = snap.Received;
        }
        return _latest;
    }

    /// <summary>
    /// stdin에 'q'를 보내 서버를 graceful 종료하고 프로세스 리소스를 해제합니다.
    /// 5초 내 종료되지 않으면 강제 Kill합니다.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _shutdownRequested = true;
        if (_proc.HasExited) { _proc.Dispose(); return; }
        try
        {
            await _proc.StandardInput.WriteLineAsync("q");
            if (!_proc.WaitForExit(5000))
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* 이미 종료됨 */ }
        finally { _proc.Dispose(); }
    }
}
