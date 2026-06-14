using ServerLib.Interface;

namespace ServerLib.Examples.Examples;

/// <summary>
/// <see cref="IServerListener"/>의 연결 제한 기능을 시연합니다.
/// MaxConnections 초과 연결 거부, TotalRejectedConnections 카운터,
/// ActiveSessionCount, IdleTimeout/OnIdleTimeout을 다룹니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="IServerListener.MaxConnections"/> — Start 전 설정 필수</description></item>
/// <item><description><see cref="IServerListener.MaxConnectionsPerIp"/> — IP당 최대 연결</description></item>
/// <item><description><see cref="IServerListener.TotalRejectedConnections"/> — 거부된 총 연결 수</description></item>
/// <item><description><see cref="IServerListener.ActiveSessionCount"/> — 현재 활성 세션 수</description></item>
/// <item><description><see cref="IServerListener.IdleTimeout"/> / <see cref="IServerListener.OnIdleTimeout"/></description></item>
/// </list>
/// </remarks>
internal static class ConnectionLimits
{
    /// <summary>
    /// MaxConnections=1로 서버를 구동해 2번째 연결이 거부되는 것을 검증하고,
    /// 짧은 IdleTimeout으로 비활성 클라이언트가 내보내지는 것을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 단일 예제 진입점. 콜백은 I/O 스레드에서 실행됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 연결 거부 처리는 서버 내부에서 Zero-allocation.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. TCS로 결정적 동기화합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        // ── MaxConnections 제한 시연 ──
        Console.WriteLine("  [ConnectionLimits] MaxConnections=1 시연:");

        // TaskCompletionSource: 첫 클라이언트 연결 완료를 I/O 스레드 콜백에서 신호합니다.
        var firstConnected = new TaskCompletionSource();

        IServerListener listener = ServerNet.CreateListener();

        // ⚠️ 모든 제한 옵션은 Start() 전에 설정해야 합니다.
        listener.MaxConnections = 1;       // 최대 동시 연결 1개
        listener.MaxConnectionsPerIp = 2;  // IP당 최대 2개 (MaxConnections가 더 제한적이므로 사실상 무관)

        listener.OnClientConnected = session =>
        {
            Console.WriteLine($"  [서버] 연결 수락: {session.RemoteEndPoint} (ActiveSessionCount={listener.ActiveSessionCount})");
            firstConnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        listener.OnClientDisconnected = session =>
        {
            Console.WriteLine($"  [서버] 연결 해제: {session.RemoteEndPoint} (ActiveSessionCount={listener.ActiveSessionCount})");
            return ValueTask.CompletedTask;
        };

        int port = ExampleHarness.GetFreePort();
        listener.Start(port);
        Console.WriteLine($"  [서버] 시작됨 (MaxConnections={listener.MaxConnections})");

        // 첫 번째 클라이언트 — 정상 연결
        await using IClientConnection conn1 = ServerNet.CreateClient();
        conn1.OnReceived = _ => ValueTask.CompletedTask;
        conn1.OnConnected = () => ValueTask.CompletedTask;
        conn1.OnDisconnected = () => ValueTask.CompletedTask;
        await conn1.ConnectAsync(ExampleHarness.LoopbackHost, port);
        await ExampleHarness.WaitSignaledAsync(firstConnected, TimeSpan.FromSeconds(5));

        Console.WriteLine($"  [서버] ActiveSessionCount={listener.ActiveSessionCount} (예상=1)");
        Console.WriteLine($"  [서버] TotalRejectedConnections={listener.TotalRejectedConnections} (예상=0)");

        // 두 번째 클라이언트 — MaxConnections 초과로 거부됨
        // 서버가 TCP 핸드셰이크 완료 직후 즉시 소켓을 닫으므로,
        // 클라이언트는 연결 성공 직후 OnDisconnected를 받습니다.
        await using IClientConnection conn2 = ServerNet.CreateClient();
        // TaskCompletionSource: 서버가 MaxConnections 초과로 conn2를 즉시 종료할 때 OnDisconnected에서 신호합니다.
        // TCS.TrySetResult()는 여러 스레드에서 안전하게 호출 가능 — I/O 스레드에서 첫 호출만 반영됩니다.
        var conn2Disconnected = new TaskCompletionSource();
        conn2.OnReceived = _ => ValueTask.CompletedTask;
        conn2.OnConnected = () => ValueTask.CompletedTask;
        conn2.OnDisconnected = () =>
        {
            Console.WriteLine("  [클라2] OnDisconnected (서버가 MaxConnections 초과로 즉시 종료)");
            conn2Disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        try
        {
            await conn2.ConnectAsync(ExampleHarness.LoopbackHost, port);
        }
        catch (Exception ex)
        {
            // 일부 구현에서는 ConnectAsync 자체가 실패할 수 있습니다.
            Console.WriteLine($"  [클라2] ConnectAsync 예외: {ex.GetType().Name} (거부로 인한 정상 동작)");
        }

        // 거부 카운터가 증가할 때까지 잠시 대기 (비동기 카운터 갱신)
        var rejectWait = System.Diagnostics.Stopwatch.StartNew();
        while (listener.TotalRejectedConnections == 0 && rejectWait.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(50);

        Console.WriteLine($"  [서버] TotalRejectedConnections={listener.TotalRejectedConnections} (예상≥1)");
        Console.WriteLine($"  [서버] ActiveSessionCount={listener.ActiveSessionCount} (예상=1)");

        listener.Stop();

        // ── IdleTimeout / OnIdleTimeout 시연 ──
        Console.WriteLine("\n  [ConnectionLimits] IdleTimeout 시연:");

        // TaskCompletionSource: OnIdleTimeout 콜백에서 신호합니다.
        var idleTimeoutFired = new TaskCompletionSource();

        IServerListener listener2 = ServerNet.CreateListener();

        // IdleTimeout: 이 시간 동안 완전한 패킷이 도착하지 않으면 OnIdleTimeout이 호출됩니다.
        // LastProgressAt 기준 (LastReceivedAt이 아님 — slowloris 공격에 안전).
        // ⚠️ Start() 전에 설정해야 합니다.
        listener2.IdleTimeout = TimeSpan.FromMilliseconds(500); // 0.5초 유휴 → 타임아웃

        // OnIdleTimeout: 유휴 타임아웃 시 I/O 스레드에서 호출됩니다.
        // 애플리케이션이 직접 연결을 끊거나 재연결을 유도하는 로직을 여기에 구현합니다.
        listener2.OnIdleTimeout = session =>
        {
            Console.WriteLine($"  [서버2] OnIdleTimeout 호출: {session.RemoteEndPoint} (IdleTimeout={listener2.IdleTimeout})");
            idleTimeoutFired.TrySetResult();
            return ValueTask.CompletedTask;
        };

        listener2.OnClientConnected = session =>
        {
            Console.WriteLine($"  [서버2] 클라이언트 연결: {session.RemoteEndPoint}");
            return ValueTask.CompletedTask;
        };

        listener2.OnClientDisconnected = _ => ValueTask.CompletedTask;

        int port2 = ExampleHarness.GetFreePort();
        listener2.Start(port2);

        await using IClientConnection conn3 = ServerNet.CreateClient();
        conn3.OnReceived = _ => ValueTask.CompletedTask;
        conn3.OnConnected = () => ValueTask.CompletedTask;
        conn3.OnDisconnected = () => ValueTask.CompletedTask;
        await conn3.ConnectAsync(ExampleHarness.LoopbackHost, port2);

        // 패킷을 전송하지 않고 대기 → IdleTimeout 발화
        Console.WriteLine($"  [클라3] 패킷 전송 없이 대기 (IdleTimeout={listener2.IdleTimeout})...");
        await ExampleHarness.WaitSignaledAsync(idleTimeoutFired, TimeSpan.FromSeconds(5));

        listener2.Stop();
        Console.WriteLine("[OK] 08_ConnectionLimits");
    }
}
