using System.Diagnostics;

namespace StabilityTest;

/// <summary>2초 주기로 진행 상황(클라이언트·서버 received·세션·heap)을 콘솔에 출력합니다.</summary>
public sealed class StabilityMonitor
{
    private readonly ServerProcess _server;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public StabilityMonitor(ServerProcess server) => _server = server;

    public async Task RunAsync(Func<long> sentTotal, Func<int> activeReliable, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await SafeTickAsync(timer, ct))
        {
            _server.TryGetLatest(out var s);
            Console.WriteLine(
                $"[{_sw.Elapsed:hh\\:mm\\:ss}] " +
                $"clients={activeReliable(),5} | sent={sentTotal(),12:N0} | " +
                $"srvRecv={s.Received,12:N0} | sessions={s.Sessions,6} | " +
                $"heapMB={s.HeapBytes / (1024 * 1024),6:N0} | gen2={s.Gen2,4} | alive={!_server.HasExited}");
        }
    }

    private static async Task<bool> SafeTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
