using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ServerLib.Examples.Examples;

/// <summary>
/// <see cref="ISessionRegistry"/>를 통한 브로드캐스트와 세션 등록·조회를 시연합니다.
/// N개 클라이언트가 연결된 상태에서 서버가 <see cref="ChatPacket"/>을 전체 브로드캐스트하고
/// 각 클라이언트가 수신을 확인합니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="ServerNet.CreateSessionRegistry"/> / <see cref="ServerNet.CreateListener"/>(registry 전달)</description></item>
/// <item><description><see cref="ISessionRegistry.Count"/> / <see cref="ISessionRegistry.TryGet"/> / <see cref="ISessionRegistry.GetAll"/> / <see cref="ISessionRegistry.BroadcastAsync"/></description></item>
/// <item><description><see cref="ISession.SendAsync"/> — 개별 세션 직접 전송</description></item>
/// <item><description>무할당 브로드캐스트 패턴: <see cref="ArrayPool{T}"/> Rent → Serialize → BroadcastAsync → Return</description></item>
/// </list>
/// </remarks>
internal static class BroadcastRegistry
{
    private const int ClientCount = 3; // 동시 연결 클라이언트 수

    /// <summary>
    /// 레지스트리를 생성하고 N개 클라이언트에게 브로드캐스트를 전송한 뒤 모두 수신을 확인합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 단일 예제 진입점. 콜백은 I/O 스레드에서 실행됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 브로드캐스트 버퍼는 <see cref="ArrayPool{T}"/>에서 대여 후 반납.
    /// 개별 세션 전송(ISession.SendAsync)도 내부에서 동일한 패턴을 사용합니다.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. TCS 배열로 N개 클라 수신을 결정적으로 대기합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        var serializer = new BinaryPacketSerializer();

        // TaskCompletionSource 배열: 각 클라이언트가 브로드캐스트를 수신하면 신호를 보냅니다.
        // 배열은 단순 참조 배열이므로 할당은 배열 헤더 1회 + TCS 참조 N개입니다.
        var receivedTcs = Enumerable.Range(0, ClientCount)
            .Select(_ => new TaskCompletionSource())
            .ToArray();

        // ── 레지스트리 생성 ──
        // ISessionRegistry: 연결된 세션 집합을 관리하고 BroadcastAsync를 제공합니다.
        // ServerNet.CreateSessionRegistry()를 통해서만 생성 — 구현체(SessionRegistry)는 internal.
        ISessionRegistry registry = ServerNet.CreateSessionRegistry();

        // IServerListener에 registry를 주입: 이후 연결·해제 시 자동으로 세션이 등록·해제됩니다.
        IServerListener listener = ServerNet.CreateListener(registry);

        // 세션 캡처용 — TryGet 시연에 사용
        Guid firstSessionId = Guid.Empty;

        listener.OnClientConnected = session =>
        {
            if (firstSessionId == Guid.Empty)
                firstSessionId = session.SessionId;
            Console.WriteLine($"  [서버] 연결: {session.RemoteEndPoint} (현재 레지스트리 Count={registry.Count})");
            return ValueTask.CompletedTask;
        };

        listener.OnClientDisconnected = session =>
        {
            Console.WriteLine($"  [서버] 해제: {session.RemoteEndPoint} (남은 Count={registry.Count})");
            return ValueTask.CompletedTask;
        };

        int port = ExampleHarness.GetFreePort();
        listener.Start(port);

        // ── N개 클라이언트 동시 연결 ──
        var clients = new IClientConnection[ClientCount];
        for (int i = 0; i < ClientCount; i++)
        {
            int idx = i; // 람다 캡처용 복사
            clients[i] = ServerNet.CreateClient();
            clients[i].OnReceived = data =>
            {
                if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
                    return ValueTask.CompletedTask;
                if (packetId == ChatPacket.Id)
                {
                    var pkt = serializer.Deserialize<ChatPacket>(data.Span);
                    Console.WriteLine($"  [클라{idx}] ChatPacket 수신: {pkt.Sender}: {pkt.Content}");
                    receivedTcs[idx].TrySetResult();
                }
                return ValueTask.CompletedTask;
            };
            await clients[i].ConnectAsync(ExampleHarness.LoopbackHost, port);
        }

        // 모든 클라이언트가 레지스트리에 등록될 때까지 잠시 대기 (OnClientConnected가 비동기이므로)
        // 정확한 동기화를 위해 Count가 ClientCount가 될 때까지 반복 확인(busy-wait 짧게 허용)
        var regWait = Stopwatch.StartNew();
        while (registry.Count < ClientCount && regWait.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(10);

        Console.WriteLine($"\n  [레지스트리] Count={registry.Count} (예상={ClientCount})");

        // ── ISessionRegistry.GetAll(): 전체 세션 열거 ──
        var allSessions = registry.GetAll();
        Console.WriteLine($"  [레지스트리] GetAll() → {allSessions.Count}개 세션:");
        foreach (var s in allSessions)
            Console.WriteLine($"    SessionId={s.SessionId.ToString()[..8]}... State={s.State}");

        // ── ISessionRegistry.TryGet(): 특정 세션 조회 ──
        if (registry.TryGet(firstSessionId, out ISession? found))
            Console.WriteLine($"  [레지스트리] TryGet({firstSessionId.ToString()[..8]}...): 성공, State={found!.State}");
        else
            Console.WriteLine($"  [레지스트리] TryGet: 세션을 찾지 못했습니다.");

        // ── 개별 세션 직접 전송(ISession.SendAsync) ──
        var directMsg = new ChatPacket { Sender = "서버", Content = "개별 메시지" };
        if (found != null)
        {
            await found.SendAsync(directMsg);
            Console.WriteLine($"  [서버] found 세션에 직접 SendAsync 호출");
        }

        // ── ISessionRegistry.BroadcastAsync(): 무할당 브로드캐스트 패턴 ──
        var broadcastPkt = new ChatPacket { Sender = "서버", Content = "전체 브로드캐스트" };
        int pktSize = PacketPool.HeaderSize + broadcastPkt.GetBodySize();
        // ArrayPool<byte>.Shared.Rent: 버킷 기반 TLS 풀에서 O(1) 대여 — new byte[]를 피해 GC 압력 억제.
        // BroadcastAsync가 N개 세션에 동일 버퍼를 순차 전송하므로 단 1번만 직렬화하면 됩니다.
        var buf = ArrayPool<byte>.Shared.Rent(pktSize);
        try
        {
            serializer.Serialize(broadcastPkt, buf);
            // BroadcastAsync: 레지스트리에 등록된 모든 세션에 동일 ReadOnlyMemory<byte>를 전송합니다.
            // 호출 동안 buf 내용이 유효해야 하며, 반환 후 Return해야 합니다.
            await registry.BroadcastAsync(buf.AsMemory(0, pktSize));
            Console.WriteLine($"  [서버] BroadcastAsync 완료 (pktSize={pktSize}B, 대상={registry.Count}개 세션)");
        }
        finally
        {
            // 반납 누락 시 ArrayPool 고갈 → 이후 Rent가 new byte[]로 퇴화(GC 압력 누적)
            ArrayPool<byte>.Shared.Return(buf);
        }

        // ── 모든 클라이언트 수신 확인 ──
        // Task.WhenAll: 모든 TCS가 신호를 보낼 때까지 대기 (병렬 완료, 직렬 보다 빠름)
        await Task.WhenAll(receivedTcs.Select(t =>
            ExampleHarness.WaitSignaledAsync(t, TimeSpan.FromSeconds(5))));

        // 정리
        foreach (var c in clients)
        {
            c.Disconnect();
            await c.DisposeAsync();
        }
        listener.Stop();
        Console.WriteLine("[OK] 03_BroadcastRegistry");
    }
}
