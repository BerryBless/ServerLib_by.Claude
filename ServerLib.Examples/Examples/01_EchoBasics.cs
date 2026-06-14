using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace ServerLib.Examples.Examples;

/// <summary>
/// ServerLib의 가장 기본적인 사용 패턴을 시연합니다.
/// <see cref="ServerNet"/> 팩토리로 서버와 클라이언트를 생성하고,
/// <see cref="EchoPacket"/>을 클라→서버→클라 방향으로 왕복 전송합니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="ServerNet.CreateListener"/> / <see cref="ServerNet.CreateClient"/></description></item>
/// <item><description><see cref="IServerListener.OnClientConnected"/> / <see cref="IServerListener.OnReceived"/> / <see cref="IServerListener.Start"/> / <see cref="IServerListener.Stop"/> / <see cref="IServerListener.IsRunning"/></description></item>
/// <item><description><see cref="IClientConnection.OnConnected"/> / <see cref="IClientConnection.OnReceived"/> / <see cref="IClientConnection.ConnectAsync"/> / <see cref="IClientConnection.SendAsync"/> / <see cref="IClientConnection.IsConnected"/> / <see cref="IClientConnection.Disconnect"/> / <see cref="IClientConnection.DisposeAsync"/></description></item>
/// <item><description><see cref="PacketPool.TryParseHeader"/> / <see cref="PacketPool.HeaderSize"/></description></item>
/// <item><description><see cref="BinaryPacketSerializer.Serialize{T}"/> / <see cref="BinaryPacketSerializer.Deserialize{T}"/></description></item>
/// <item><description><c>PacketSendExtensions.SendAsync&lt;T&gt;</c> — <see cref="IClientConnection"/> 오버로드와 <see cref="ISession"/> 오버로드 모두 시연</description></item>
/// </list>
/// </remarks>
internal static class EchoBasics
{
    /// <summary>
    /// 루프백 서버를 구동하고 클라이언트가 EchoPacket을 전송한 뒤 서버가 에코하는 흐름을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 예제 진입점(호출 스레드 1개). 내부 콜백은 I/O 스레드에서 실행됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 에코 경로에 <see cref="ArrayPool{T}"/> 무할당 패턴 사용.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 콜백 완료는 <see cref="TaskCompletionSource"/>로 결정적 동기화합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        // BinaryPacketSerializer: IPacketSerializer 유일 구현체 — 4바이트 헤더[id(2B LE) | bodyLen(2B LE)] + 본문 포맷.
        // new()로 직접 생성해도 되지만 static 인스턴스가 없으므로 예제당 1회 생성(할당 1회, GC 무시).
        var serializer = new BinaryPacketSerializer();

        // TaskCompletionSource: I/O 스레드 콜백에서 신호를 줘 결정적으로 에코 수신을 기다림.
        // bare Task.Delay로 타이밍을 맞추면 flaky — TCS가 이벤트 기반으로 정확한 동기화를 제공.
        var echoReceived = new TaskCompletionSource();

        // ── 서버 구성 ──
        IServerListener listener = ServerNet.CreateListener();

        // ⚠️ 모든 콜백과 옵션은 Start() 전에 설정해야 합니다. 이후 설정 시 InvalidOperationException.

        ISession? serverSession = null; // OnClientConnected에서 세션을 캡처해 에코 전송에 사용

        // OnClientConnected: 새 클라이언트 연결 시 I/O 스레드에서 호출됨.
        // session.Context에 사용자 데이터를 저장하거나 초기 패킷을 전송할 수 있습니다.
        listener.OnClientConnected = session =>
        {
            serverSession = session;
            Console.WriteLine($"  [서버] 클라이언트 연결: {session.RemoteEndPoint}");
            return ValueTask.CompletedTask;
        };

        // OnReceived: 서버가 패킷을 수신할 때 I/O 스레드에서 호출됩니다.
        // data는 PacketPool 프레임 전체([packetId(2B), bodyLen(2B), body...])를 담은 슬라이스입니다.
        // 이 슬라이스는 콜백 반환 후 무효화되므로 보관이 필요하면 복사해야 합니다.
        listener.OnReceived = async (session, data) =>
        {
            // PacketPool.TryParseHeader: 4바이트 헤더를 읽어 packetId와 bodyLength를 파싱.
            // 헤더가 불완전하거나 손상된 경우 false를 반환해 안전하게 무시할 수 있음.
            if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
                return;

            if (packetId == EchoPacket.Id)
            {
                var pkt = serializer.Deserialize<EchoPacket>(data.Span);
                Console.WriteLine($"  [서버] EchoPacket 수신: \"{pkt.Message}\" → 에코 전송");

                // PacketSendExtensions.SendAsync<T>(this ISession, T packet): ISession 오버로드.
                // 내부에서 ArrayPool 대여 → 직렬화 → SendAsync(ReadOnlyMemory) → 반납을 수행합니다.
                await session.SendAsync(new EchoPacket { Message = $"[에코] {pkt.Message}" });
            }
        };

        listener.OnClientDisconnected = session =>
        {
            Console.WriteLine($"  [서버] 클라이언트 연결 해제: {session.RemoteEndPoint}");
            return ValueTask.CompletedTask;
        };

        listener.OnClientError = (session, ex) =>
        {
            Console.WriteLine($"  [서버] 오류: {ex.Message}");
            return ValueTask.CompletedTask;
        };

        int port = ExampleHarness.GetFreePort();
        listener.Start(port);
        Console.WriteLine($"  [서버] 시작됨 (port={port}, IsRunning={listener.IsRunning})");

        // ── 클라이언트 구성 ──
        await using IClientConnection conn = ServerNet.CreateClient();

        // ⚠️ OnReceived 등 콜백은 ConnectAsync() 전에 설정해야 합니다.

        conn.OnConnected = () =>
        {
            Console.WriteLine($"  [클라] 서버 연결 성공 (IsConnected={conn.IsConnected})");
            return ValueTask.CompletedTask;
        };

        conn.OnReceived = data =>
        {
            if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
                return ValueTask.CompletedTask;

            if (packetId == EchoPacket.Id)
            {
                var pkt = serializer.Deserialize<EchoPacket>(data.Span);
                Console.WriteLine($"  [클라] 에코 수신: \"{pkt.Message}\"");
                echoReceived.TrySetResult();
            }
            return ValueTask.CompletedTask;
        };

        conn.OnDisconnected = () =>
        {
            Console.WriteLine("  [클라] 연결 해제됨");
            return ValueTask.CompletedTask;
        };

        await conn.ConnectAsync(ExampleHarness.LoopbackHost, port);

        // ── PacketSendExtensions.SendAsync<T>(this IClientConnection, T): IClientConnection 오버로드 ──
        // 무할당 패턴: ArrayPool.Rent → Serialize → SendAsync(ReadOnlyMemory) → finally Return.
        // PacketSendExtensions가 이 흐름을 캡슐화하므로 호출부는 패킷 객체만 전달하면 됩니다.
        var sendPkt = new EchoPacket { Message = "안녕, ServerLib!" };
        Console.WriteLine($"  [클라] EchoPacket 전송: \"{sendPkt.Message}\"");
        await conn.SendAsync(sendPkt);

        // ── 에코 도착 대기 ──
        await ExampleHarness.WaitSignaledAsync(echoReceived, TimeSpan.FromSeconds(5));

        // ── 수동 직렬화 + SendAsync(ReadOnlyMemory) 패턴도 시연 ──
        // PacketPool.HeaderSize: 헤더 크기 상수(4). 버퍼 크기 계산에 사용합니다.
        int pktSize = PacketPool.HeaderSize + sendPkt.GetBodySize();
        // ArrayPool<byte>.Shared.Rent: 버킷 기반 TLS 풀에서 O(1) 대여 — new byte[]를 피해 GC 압력 억제.
        var buf = ArrayPool<byte>.Shared.Rent(pktSize);
        try
        {
            serializer.Serialize(sendPkt, buf);
            // SendAsync(ReadOnlyMemory<byte>): 저수준 오버로드 — 이미 직렬화된 버퍼를 직접 전달합니다.
            await conn.SendAsync(buf.AsMemory(0, pktSize));
        }
        finally
        {
            // 반드시 반납해야 함 — 누락 시 ArrayPool 고갈로 이후 Rent가 새 할당으로 퇴화(GC 누수).
            ArrayPool<byte>.Shared.Return(buf);
        }

        // ── Disconnect vs DisposeAsync ──
        // Disconnect(): 즉시 연결을 끊습니다(동기, 예외 없음).
        // DisposeAsync(): IAsyncDisposable — await using 블록 종료 시 자동 호출됩니다.
        conn.Disconnect();
        Console.WriteLine($"  [클라] Disconnect() 호출 후 IsConnected={conn.IsConnected}");

        // 서버 정리
        listener.Stop();
        Console.WriteLine($"  [서버] 중지됨 (IsRunning={listener.IsRunning})");
        Console.WriteLine("[OK] 01_EchoBasics");
    }
}
