using ServerLib.Core.Memory;
using ServerLib.Core.Rpc;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace ServerLib.Examples.Examples;

/// <summary>
/// <see cref="RpcDispatcher"/>의 패킷 ID 기반 핸들러 라우팅을 시연합니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="RpcDispatcher"/> ctor(maxPacketId) / <see cref="RpcDispatcher.Register"/> / <see cref="RpcDispatcher.DispatchAsync"/></description></item>
/// <item><description>등록된 id 디스패치 / 미등록 id 무시 / payload.Length &lt; 2 no-op / Register 범위 초과 예외</description></item>
/// </list>
/// <br/><br/>
/// <b>[⚠️ RPC 프레이밍 주의]</b><br/>
/// <see cref="RpcDispatcher.DispatchAsync"/>는 payload[0..1]을 packetId(LE)로 읽고 payload[2..]를 핸들러에 전달합니다.<br/>
/// <see cref="IServerListener.OnReceived"/>가 제공하는 data는 PacketPool 전체 프레임
/// <c>[packetId(2B) | bodyLen(2B) | body(N B)]</c>이므로, data를 DispatchAsync에 직접 전달하면:<br/>
/// - data[0..1] = packetId → RpcDispatcher가 올바르게 읽음<br/>
/// - 핸들러가 받는 payload = data[2..] = <c>[bodyLen(2B) | body(N B)]</c><br/>
/// 이는 PacketPool 4바이트 헤더와 RpcDispatcher의 2바이트 id 해석이 우연히 호환되는 것입니다.
/// </remarks>
internal static class Rpc
{
    /// <summary>
    /// RpcDispatcher를 구성하고 루프백 서버로 패킷을 전송해 핸들러 라우팅을 검증합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> RpcDispatcher.Register는 서버 시작 전 단일 스레드에서 호출.
    /// DispatchAsync는 I/O 스레드에서 호출됩니다 — 핸들러도 I/O 스레드에서 실행됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 핸들러 등록 배열 1회. 디스패치 자체는 Zero-allocation(배열 인덱싱).
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. TCS로 핸들러 호출을 결정적으로 대기합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        var serializer = new BinaryPacketSerializer();

        // TaskCompletionSource: 핸들러가 호출될 때 신호를 보내 결정적으로 대기합니다.
        var handlerCalled = new TaskCompletionSource();

        // ── RpcDispatcher 생성 ──
        // maxPacketId=10: 패킷 ID 0~9까지의 핸들러를 등록할 수 있습니다.
        // 내부적으로 Func[] 배열을 maxPacketId 크기로 생성합니다.
        var dispatcher = new RpcDispatcher(maxPacketId: 10);

        // ── Register: 패킷 ID에 핸들러 등록 ──
        // IncrementPacket.Id(=3)에 핸들러 등록. 핸들러가 받는 payload는 data.Slice(2):
        //   data[0..1] = packetId (RpcDispatcher가 읽어 핸들러 선택에 사용)
        //   data[2..3] = bodyLen (PacketPool 헤더의 일부 — 본문 없는 IncrementPacket은 0x00,0x00)
        //   data[4..]  = 실제 본문 (IncrementPacket은 없음)
        dispatcher.Register(IncrementPacket.Id, (session, payload, ct) =>
        {
            // payload = data[IncrementPacket.Id..] = [bodyLen(2B), body(0B)]
            // IncrementPacket은 본문이 없으므로 payload[0..1]만 존재(bodyLen=0)
            Console.WriteLine($"  [RPC] IncrementPacket 핸들러 호출: 세션={session.SessionId.ToString()[..8]}..., payloadLen={payload.Length}");
            handlerCalled.TrySetResult();
            return ValueTask.CompletedTask;
        });

        // ── Register 범위 초과 → ArgumentOutOfRangeException ──
        try
        {
            dispatcher.Register(10, (_, _, _) => ValueTask.CompletedTask); // id=10 >= maxPacketId=10
            throw new InvalidOperationException("예외가 발생해야 합니다.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine($"  [RPC] Register(id=10) → {ex.GetType().Name} (정상, maxPacketId=10이므로 유효 범위는 0~9)");
        }

        // ── 서버 구성: OnReceived에서 DispatchAsync로 포워딩 ──
        IServerListener listener = ServerNet.CreateListener();

        // ISession 캡처: DispatchAsync는 ISession을 요구합니다.
        // ⚠️ OnClientConnected에서 session.OnReceived를 할당하면 InvalidOperationException
        //    서버 수신 경로는 IServerListener.OnReceived로만 처리합니다.
        ISession? serverSession = null;
        listener.OnClientConnected = session =>
        {
            serverSession = session;
            return ValueTask.CompletedTask;
        };

        listener.OnReceived = async (session, data) =>
        {
            // ⚠️ RPC 프레이밍 주의 (헤더 주석 참조):
            //    PacketPool 프레임을 DispatchAsync에 직접 전달 —
            //    data[0..1](packetId)을 DispatchAsync가 읽고, 핸들러에는 data[2..](bodyLen+body)가 전달됨.
            await dispatcher.DispatchAsync(session, data);
        };

        int port = ExampleHarness.GetFreePort();
        listener.Start(port);

        // ── 클라이언트: IncrementPacket 전송 (PacketPool 정상 프레임) ──
        await using IClientConnection conn = ServerNet.CreateClient();
        conn.OnReceived = _ => ValueTask.CompletedTask;
        await conn.ConnectAsync(ExampleHarness.LoopbackHost, port);

        // PacketSendExtensions.SendAsync<T>: 내부에서 [packetId(2B)|bodyLen(2B)|body]로 직렬화해 전송.
        // DispatchAsync는 첫 2바이트(packetId)를 읽고, IncrementPacket.Id(=3)에 등록된 핸들러를 호출합니다.
        await conn.SendAsync(new IncrementPacket());
        Console.WriteLine("  [RPC] 클라이언트가 IncrementPacket 전송");

        // ── 핸들러 호출 대기 ──
        await ExampleHarness.WaitSignaledAsync(handlerCalled, TimeSpan.FromSeconds(5));

        // ── 미등록 ID: 조용히 무시됩니다 ──
        // EchoPacket.Id(=1)는 등록되지 않았으므로 핸들러 없이 무시됩니다.
        await conn.SendAsync(new EchoPacket { Message = "미등록" });
        Console.WriteLine("  [RPC] EchoPacket.Id(=1) 미등록 → DispatchAsync가 조용히 무시합니다");

        // ── payload.Length < 2: DispatchAsync가 no-op ──
        // 직접 data 없이 빈 메모리 전달 시 Length<2이면 즉시 return됩니다.
        // (소켓 전송 없이 직접 호출로 시연)
        if (serverSession != null)
        {
            await dispatcher.DispatchAsync(serverSession, ReadOnlyMemory<byte>.Empty);
            Console.WriteLine("  [RPC] ReadOnlyMemory.Empty(length=0) → no-op (정상)");

            await dispatcher.DispatchAsync(serverSession, new byte[] { 0x01 }.AsMemory()); // length=1 < 2
            Console.WriteLine("  [RPC] length=1 payload → no-op (정상)");
        }

        listener.Stop();
        Console.WriteLine("[OK] 05_Rpc");
    }
}
