using ServerLib.Interface;

namespace ServerLib.Examples.Examples;

/// <summary>
/// 하트비트(PingInterval·Rtt)와 송신 타임아웃(SendTimeout·SessionSendTimeout)을 시연합니다.
/// 클라이언트가 PingInterval을 설정하면 라이브러리가 자동으로 PING/PONG을 교환하고
/// <see cref="IClientConnection.Rtt"/>를 갱신합니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="IClientConnection.PingInterval"/> — Connect 전 설정 필수</description></item>
/// <item><description><see cref="IClientConnection.Rtt"/> — PONG 수신 후 갱신</description></item>
/// <item><description><see cref="IClientConnection.SendTimeout"/> — 클라이언트 송신 타임아웃</description></item>
/// <item><description><see cref="IServerListener.SessionSendTimeout"/> — 서버 세션별 송신 타임아웃</description></item>
/// </list>
/// </remarks>
internal static class Heartbeat
{
    /// <summary>
    /// PingInterval을 설정한 클라이언트가 자동 PING/PONG 교환 후 Rtt > 0 이 되는 것을 검증합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 예제 진입점은 단일 스레드. PingInterval·SendTimeout은 ConnectAsync 전 설정.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> PING/PONG은 내부 HeartbeatProtocol이 처리 — 앱 코드 할당 없음.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Rtt > 0 폴링은 짧은 대기(비동기 Task.Delay)를 사용합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        IServerListener listener = ServerNet.CreateListener();

        // SessionSendTimeout: 서버가 응답 없는 클라에게 송신할 때의 최대 허용 시간.
        // null(기본값)이면 비활성화. 설정 시 타임아웃 초과하면 SocketException(TimedOut)이 발생합니다.
        // ⚠️ Start() 전에 설정해야 합니다.
        listener.SessionSendTimeout = TimeSpan.FromSeconds(10);

        listener.OnClientConnected = session =>
        {
            Console.WriteLine($"  [서버] 클라이언트 연결 (SessionSendTimeout={listener.SessionSendTimeout})");
            return ValueTask.CompletedTask;
        };

        listener.OnClientDisconnected = _ => ValueTask.CompletedTask;

        int port = ExampleHarness.GetFreePort();
        listener.Start(port);

        await using IClientConnection conn = ServerNet.CreateClient();

        // ⚠️ PingInterval / SendTimeout은 ConnectAsync() 전에 설정해야 합니다.

        // PingInterval: 이 간격으로 라이브러리가 자동으로 PING(PingPacket)을 송신합니다.
        // 서버는 내부 HeartbeatProtocol이 자동으로 PONG(PongPacket)을 회신합니다.
        // PONG 수신 시 conn.Rtt가 갱신됩니다 (PONG.ClientTicks 기준 왕복 시간).
        conn.PingInterval = TimeSpan.FromMilliseconds(200); // 200ms마다 PING

        // SendTimeout: 클라이언트 송신 1건의 최대 허용 시간.
        // null(기본값)이면 비활성화. 루프백 예제에서는 넉넉하게 설정합니다.
        conn.SendTimeout = TimeSpan.FromSeconds(10);

        conn.OnReceived = _ => ValueTask.CompletedTask;
        conn.OnConnected = () =>
        {
            Console.WriteLine($"  [클라] 연결됨 (PingInterval={conn.PingInterval}, SendTimeout={conn.SendTimeout})");
            return ValueTask.CompletedTask;
        };
        conn.OnDisconnected = () => ValueTask.CompletedTask;

        await conn.ConnectAsync(ExampleHarness.LoopbackHost, port);

        // ── Rtt > 0 될 때까지 폴링 ──
        // PING/PONG 왕복이 완료되면 conn.Rtt에 반영됩니다.
        // 루프백이므로 수 ms 내에 완료됩니다.
        var rttWait = System.Diagnostics.Stopwatch.StartNew();
        while (conn.Rtt == TimeSpan.Zero && rttWait.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);

        if (conn.Rtt == TimeSpan.Zero)
            throw new TimeoutException("5초 내에 Rtt가 0보다 커지지 않았습니다.");

        Console.WriteLine($"  [클라] Rtt={conn.Rtt.TotalMilliseconds:F3}ms (PING/PONG 왕복 측정값)");
        Console.WriteLine($"  [클라] IsConnected={conn.IsConnected}");

        listener.Stop();
        Console.WriteLine("[OK] 07_Heartbeat");
    }
}
