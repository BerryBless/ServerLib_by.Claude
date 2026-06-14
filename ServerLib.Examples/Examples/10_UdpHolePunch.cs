using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Transport;

namespace ServerLib.Examples.Examples;

/// <summary>
/// <see cref="UdpHolePuncher"/>를 루프백으로 시연합니다.
/// 두 피어가 가짜 릴레이 서버를 통해 서로의 엔드포인트를 교환하고
/// 동시에 홀펀칭 패킷을 전송해 직접 통신을 성립시키는 흐름을 다룹니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="UdpHolePuncher"/> ctor(localPort) / <see cref="UdpHolePuncher.LocalEndPoint"/></description></item>
/// <item><description><see cref="UdpHolePuncher.RegisterAsync"/> / <see cref="UdpHolePuncher.PunchAsync"/> / <see cref="UdpHolePuncher.WaitForPeerAsync"/> / <see cref="UdpHolePuncher.Dispose"/></description></item>
/// </list>
/// <br/><br/>
/// <b>[루프백 시연의 제약]</b><br/>
/// 실제 NAT 환경에서는 중계 서버가 각 피어의 공인 IP:포트를 교환해야 합니다.
/// 이 예제에서는 모두 127.0.0.1에서 실행되므로 릴레이는 단순히 UDP 패킷을 받기만 합니다.
/// WaitForPeerAsync는 PunchAsync가 보낸 패킷을 수신하면 상대방 엔드포인트를 반환합니다.
/// </remarks>
internal static class UdpHolePunch
{
    /// <summary>
    /// 가짜 릴레이를 세우고 두 UdpHolePuncher가 루프백에서 서로의 엔드포인트를 발견하는 과정을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> UdpHolePuncher 내부 UdpClient는 단일 스레드 Send/Receive — Thread-safe 아님.
    /// 두 puncher는 각자 독립된 UdpClient를 사용하므로 상호 안전합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> RegisterAsync: Guid.ToByteArray() 16바이트. PunchAsync: 1바이트 마커 ReadOnlyMemory.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. PunchAsync와 WaitForPeerAsync를 Task.WhenAll로 동시 실행합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        // ── 가짜 릴레이 UdpClient ──
        // 실제 중계 서버 역할: RegisterAsync가 보내는 Guid 등록 패킷을 수신합니다.
        // 이 예제에서 릴레이는 수신만 하며 응답하지 않습니다(루프백 시연 단순화).
        //
        // UdpClient(0): OS가 임시 포트를 원자적으로 할당 → 포트 충돌 없이 사용 가능.
        using var fakeRelay = new UdpClient(0);
        int relayPort = ExampleHarness.GetUdpPort(fakeRelay);
        var relayEndPoint = new IPEndPoint(IPAddress.Loopback, relayPort);
        Console.WriteLine($"  [릴레이] 가짜 릴레이 포트={relayPort}");

        // 릴레이가 RegisterAsync 패킷을 비동기로 수신 (무시해도 되지만 소켓 버퍼를 비워둠)
        // Task.Run: UdpClient.ReceiveAsync는 데이터가 올 때까지 대기 — 별도 Task로 분리
        _ = Task.Run(async () =>
        {
            try
            {
                // 최대 2회(puncher A, B 각 1회) 수신 후 종료
                for (int i = 0; i < 2; i++)
                    await fakeRelay.ReceiveAsync();
            }
            catch { /* 릴레이 종료 시 예외 무시 */ }
        });

        // ── UdpHolePuncher 생성 ──
        // UdpHolePuncher(localPort=0): OS가 임시 포트를 할당하고 LocalEndPoint로 확인 가능합니다.
        using var puncherA = new UdpHolePuncher(localPort: 0);
        using var puncherB = new UdpHolePuncher(localPort: 0);

        // LocalEndPoint: 이 puncher가 바인딩된 로컬 UDP 엔드포인트입니다.
        Console.WriteLine($"  [홀펀칭] PuncherA.LocalEndPoint={puncherA.LocalEndPoint}");
        Console.WriteLine($"  [홀펀칭] PuncherB.LocalEndPoint={puncherB.LocalEndPoint}");

        var peerIdA = Guid.NewGuid();
        var peerIdB = Guid.NewGuid();

        // ── RegisterAsync: 릴레이에 자신의 Guid 등록 ──
        // 실제 환경에서 릴레이는 공인 IP:포트를 매핑해 다른 피어에게 알려줍니다.
        // 이 예제에서는 릴레이가 단순히 패킷을 받기만 합니다.
        await puncherA.RegisterAsync(relayEndPoint, peerIdA);
        await puncherB.RegisterAsync(relayEndPoint, peerIdB);
        Console.WriteLine($"  [홀펀칭] RegisterAsync 완료 (A id={peerIdA.ToString()[..8]}..., B id={peerIdB.ToString()[..8]}...)");

        // ── PunchAsync + WaitForPeerAsync: 동시 실행 ──
        // 실제 NAT 홀펀칭은 두 피어가 동시에 상대방에게 UDP 패킷을 보내야 합니다.
        //
        // ⚠️ LocalEndPoint는 0.0.0.0:port 형태이므로 루프백 전송에는 사용할 수 없습니다.
        // PunchAsync의 목적지로 127.0.0.1:port 형태로 명시적으로 구성해야 합니다.
        int portOfA = puncherA.LocalEndPoint.Port;
        int portOfB = puncherB.LocalEndPoint.Port;
        // 루프백 엔드포인트: 실제 NAT 환경에서는 릴레이가 공인 IP를 알려주지만,
        // 루프백 테스트에서는 127.0.0.1 + 각 puncher의 로컬 포트를 직접 구성합니다.
        var loopbackA = new IPEndPoint(IPAddress.Loopback, portOfA);
        var loopbackB = new IPEndPoint(IPAddress.Loopback, portOfB);
        Console.WriteLine($"  [홀펀칭] 루프백 목적지 A={loopbackA}, B={loopbackB}");

        // Task.WhenAll: PunchAsync(A→B) + PunchAsync(B→A) + WaitForPeerAsync(A) + WaitForPeerAsync(B)를
        // 동시 실행 — 어느 한 쪽이 먼저 보내도 상대방이 WaitForPeerAsync로 대기 중이면 수신됩니다.
        var punchTask = Task.WhenAll(
            puncherA.PunchAsync(loopbackB, attempts: 3).AsTask(),
            puncherB.PunchAsync(loopbackA, attempts: 3).AsTask()
        );

        // CancellationTokenSource: WaitForPeerAsync 타임아웃 제어.
        // 링크드 CTS 없이 단순 타임아웃 — 루프백이므로 수백ms 내에 완료됩니다.
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var waitATask = puncherA.WaitForPeerAsync(TimeSpan.FromSeconds(5), waitCts.Token).AsTask();
        var waitBTask = puncherB.WaitForPeerAsync(TimeSpan.FromSeconds(5), waitCts.Token).AsTask();

        await Task.WhenAll(punchTask, waitATask, waitBTask);

        IPEndPoint? peerOfA = await waitATask;
        IPEndPoint? peerOfB = await waitBTask;

        Console.WriteLine($"  [홀펀칭] A가 발견한 피어: {peerOfA}");
        Console.WriteLine($"  [홀펀칭] B가 발견한 피어: {peerOfB}");

        if (peerOfA is null)
            throw new InvalidOperationException("A가 피어를 발견하지 못했습니다.");
        if (peerOfB is null)
            throw new InvalidOperationException("B가 피어를 발견하지 못했습니다.");

        // 루프백에서 발견된 포트가 서로 일치하는지 확인
        if (peerOfA.Port != puncherB.LocalEndPoint.Port)
            throw new InvalidOperationException($"A가 발견한 포트({peerOfA.Port}) ≠ B의 로컬 포트({puncherB.LocalEndPoint.Port})");
        if (peerOfB.Port != puncherA.LocalEndPoint.Port)
            throw new InvalidOperationException($"B가 발견한 포트({peerOfB.Port}) ≠ A의 로컬 포트({puncherA.LocalEndPoint.Port})");

        Console.WriteLine("  [홀펀칭] 상호 엔드포인트 발견 성공 ✓");

        // Dispose(): UdpHolePuncher가 보유한 UdpClient를 해제합니다. using 블록이 자동 호출합니다.
        // (using 블록 종료 시 자동 호출되므로 여기서는 명시적으로 보여줍니다)
        puncherA.Dispose();
        puncherB.Dispose();
        Console.WriteLine("  [홀펀칭] Dispose() 완료");

        Console.WriteLine("[OK] 10_UdpHolePunch");
    }
}
