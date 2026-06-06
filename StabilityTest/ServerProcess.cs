using System.Diagnostics;

namespace StabilityTest;

/// <summary>출하 예제 <c>Server.exe</c>를 자식 프로세스로 실행하고 관찰합니다.</summary>
/// <remarks>
/// <b>[수명주기]</b> 하네스가 child 전체 수명을 소유합니다. <see cref="Dispose"/>는 살아있는 child를 강제 종료해
/// orphan 프로세스를 남기지 않습니다.
/// </remarks>
public sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // StatsSnapshot은 readonly record struct(5필드, 네이티브 int 초과 크기)이므로
    // volatile 필드로 선언 불가 — lock으로 원자적 읽기·쓰기를 보장한다.
    private readonly object _statsLock = new();
    private StatsSnapshot _latest;
    private bool _hasStats;

    public ServerProcess(StabilityConfig config)
    {
        string exePath = ResolveServerExe(config);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // 하네스가 포트·모니터 주기를 제어 — [STATS]를 1초마다 받아 count-stable/모니터 양쪽에 사용
                Arguments = $"--Server:Port={config.Port} --Server:MonitorIntervalSeconds=1",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _process.OutputDataReceived += OnOutput;
    }

    /// <summary>최신 [STATS] 스냅샷. 아직 1개도 못 받았으면 <paramref name="snapshot"/>=default, false.</summary>
    public bool TryGetLatest(out StatsSnapshot snapshot)
    {
        lock (_statsLock)
        {
            snapshot = _latest;
            return _hasStats;
        }
    }

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    /// <summary>현재 child의 private 메모리(바이트). 호출 시점 값으로 갱신합니다.</summary>
    public long PrivateMemoryBytes
    {
        get { _process.Refresh(); return _process.HasExited ? 0 : _process.PrivateMemorySize64; }
    }

    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
    }

    /// <summary>서버가 수신 대기 준비(`[Server] port` 라인)될 때까지 대기합니다.</summary>
    public async Task WaitForReadyAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_ready.Task, Task.Delay(timeout));
        if (done != _ready.Task)
            throw new TimeoutException("서버가 제한 시간 내 준비되지 않았습니다.");
    }

    /// <summary>stdin에 "q"를 보내 graceful 종료를 요청하고 종료를 기다립니다. 시한 초과 시 강제 종료.</summary>
    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (_process.HasExited) return;
        try { await _process.StandardInput.WriteLineAsync("q"); await _process.StandardInput.FlushAsync(); }
        catch { /* stdin 닫힘 — 아래에서 강제 종료 */ }

        using var cts = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { _process.Kill(entireProcessTree: true); } catch { } }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        if (!_ready.Task.IsCompleted && e.Data.Contains("[Server] port", StringComparison.Ordinal))
            _ready.TrySetResult();
        if (StatsLineParser.TryParse(e.Data, out var snapshot))
        {
            lock (_statsLock)
            {
                _latest = snapshot;
                _hasStats = true;
            }
        }
    }

    // 솔루션 폴더를 거슬러 올라가 Server의 빌드 출력 exe 경로를 해석한다.
    private static string ResolveServerExe(StabilityConfig _)
    {
        // 현재 빌드 구성(Debug/Release)을 BaseDirectory 경로에서 추론
        string baseDir = AppContext.BaseDirectory;
        string config = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudeCodeStudy.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("ClaudeCodeStudy.sln을 찾지 못했습니다 — 솔루션 트리 밖에서 실행됨.");

        string exe = Path.Combine(dir.FullName, "Server", "bin", config, "net10.0",
            OperatingSystem.IsWindows() ? "Server.exe" : "Server");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"서버 실행 파일을 찾지 못했습니다: {exe}. 먼저 Server를 {config}로 빌드하세요.");
        return exe;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}
