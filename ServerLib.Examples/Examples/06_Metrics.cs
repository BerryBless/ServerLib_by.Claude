using ServerLib.Core;

namespace ServerLib.Examples.Examples;

/// <summary>
/// <see cref="ServerMetrics"/>의 모든 카운터 메서드를 인프로세스(소켓 불필요)로 시연합니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="ServerMetrics.OnClientConnected"/> / <see cref="ServerMetrics.OnClientDisconnected"/></description></item>
/// <item><description><see cref="ServerMetrics.OnPacketReceived"/> / <see cref="ServerMetrics.OnBytesSent"/> / <see cref="ServerMetrics.OnBytesReceived"/></description></item>
/// <item><description><see cref="ServerMetrics.ConnectedCount"/> / <see cref="ServerMetrics.TotalPacketsReceived"/> / <see cref="ServerMetrics.TotalBytesSent"/> / <see cref="ServerMetrics.TotalBytesReceived"/></description></item>
/// <item><description><see cref="ServerMetrics.Reset"/></description></item>
/// </list>
/// </remarks>
internal static class Metrics
{
    /// <summary>
    /// ServerMetrics 카운터를 직접 조작해 모든 On* 메서드와 읽기 프로퍼티를 검증합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> ServerMetrics의 모든 카운터는 Interlocked 전용 — lock 없이 다수 IO 스레드에서 안전합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> Zero-allocation. Interlocked 연산은 long 값 타입을 인라인 처리합니다.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. Interlocked 연산은 하드웨어 원자 명령(lock prefix)으로 즉시 완료됩니다.
    /// </remarks>
    public static Task RunAsync()
    {
        // ServerMetrics: 모든 카운터가 Interlocked.Add/Increment/Decrement로 원자 갱신됩니다.
        // 다수 IO 스레드에서 동시 호출해도 Monitor 락 없이 경합을 처리합니다.
        var metrics = new ServerMetrics();

        // ── 초기 상태 ──
        Console.WriteLine($"  [Metrics] 초기: ConnectedCount={metrics.ConnectedCount}, TotalPackets={metrics.TotalPacketsReceived}, BytesSent={metrics.TotalBytesSent}, BytesReceived={metrics.TotalBytesReceived}");

        // ── OnClientConnected / OnClientDisconnected ──
        metrics.OnClientConnected(); // Interlocked.Increment
        metrics.OnClientConnected();
        Console.WriteLine($"  [Metrics] OnClientConnected ×2 → ConnectedCount={metrics.ConnectedCount}");

        metrics.OnClientDisconnected(); // Interlocked.Decrement
        Console.WriteLine($"  [Metrics] OnClientDisconnected ×1 → ConnectedCount={metrics.ConnectedCount}");

        // ── OnPacketReceived ──
        for (int i = 0; i < 5; i++)
            metrics.OnPacketReceived(); // Interlocked.Increment
        Console.WriteLine($"  [Metrics] OnPacketReceived ×5 → TotalPacketsReceived={metrics.TotalPacketsReceived}");

        // ── OnBytesSent / OnBytesReceived ──
        metrics.OnBytesSent(512);       // Interlocked.Add(ref _totalBytesSent, 512)
        metrics.OnBytesSent(1024);
        metrics.OnBytesReceived(256);   // Interlocked.Add(ref _totalBytesReceived, 256)
        metrics.OnBytesReceived(128);
        Console.WriteLine($"  [Metrics] BytesSent={metrics.TotalBytesSent} (예상=1536), BytesReceived={metrics.TotalBytesReceived} (예상=384)");

        // 검증
        if (metrics.ConnectedCount != 1)
            throw new InvalidOperationException($"ConnectedCount 오류: {metrics.ConnectedCount} (예상=1)");
        if (metrics.TotalPacketsReceived != 5)
            throw new InvalidOperationException($"TotalPacketsReceived 오류: {metrics.TotalPacketsReceived} (예상=5)");
        if (metrics.TotalBytesSent != 1536)
            throw new InvalidOperationException($"TotalBytesSent 오류: {metrics.TotalBytesSent} (예상=1536)");
        if (metrics.TotalBytesReceived != 384)
            throw new InvalidOperationException($"TotalBytesReceived 오류: {metrics.TotalBytesReceived} (예상=384)");

        // ── Reset: 모든 카운터를 0으로 초기화 ──
        // Interlocked.Exchange(ref field, 0)으로 각 카운터를 원자적으로 0으로 교체합니다.
        metrics.Reset();
        Console.WriteLine($"  [Metrics] Reset() 후: ConnectedCount={metrics.ConnectedCount}, TotalPackets={metrics.TotalPacketsReceived}, BytesSent={metrics.TotalBytesSent}, BytesReceived={metrics.TotalBytesReceived}");

        if (metrics.ConnectedCount != 0 || metrics.TotalPacketsReceived != 0 ||
            metrics.TotalBytesSent != 0 || metrics.TotalBytesReceived != 0)
            throw new InvalidOperationException("Reset() 후 카운터가 0이 아닙니다.");

        Console.WriteLine("[OK] 06_Metrics");
        return Task.CompletedTask;
    }
}
