using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;
using Xunit;

namespace ServerLib.Tests;

/// <summary>클라이언트 P8(대칭) 보호: ReadPipeAsync의 PONG 가로채기/일반 패킷 전달 동작을 고정한다.</summary>
public sealed class SocketClientDispatchTests
{
    private static (Socket listener, int port) StartListener()
    {
        var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        return (l, ((IPEndPoint)l.LocalEndPoint!).Port);
    }

    private static byte[] MakePacket(ushort id)
    {
        var b = new byte[PacketPool.HeaderSize];
        PacketPool.WriteHeader(b, id, bodyLength: 0);
        return b;
    }

    private static ushort PeekId(byte[] b)
    {
        PacketPool.TryParseHeader(b, out ushort id, out _);
        return id;
    }

    [Fact]
    public async Task ClientReceive_NormalPacket_InvokesOnReceivedWithBytes()
    {
        var (listener, port) = StartListener();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new SocketPipelineClient();
        client.OnReceived = m => { tcs.TrySetResult(m.ToArray()); return ValueTask.CompletedTask; };
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port);
        using var serverSide = listener.Accept();
        listener.Dispose();

        serverSide.Send(MakePacket(id: 1)); // 일반 패킷(id=1)

        var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, PeekId(got));
    }

    [Fact]
    public async Task ClientReceive_PongPacket_UpdatesRtt_NotDeliveredToOnReceived()
    {
        var (listener, port) = StartListener();
        var received = new ConcurrentQueue<byte[]>();

        await using var client = new SocketPipelineClient();
        client.OnReceived = m => { received.Enqueue(m.ToArray()); return ValueTask.CompletedTask; };
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port);
        using var serverSide = listener.Accept();
        listener.Dispose();

        // PONG(50ms 과거 ticks)을 먼저, 그 다음 일반 마커(id=1)를 보낸다.
        var serializer = new BinaryPacketSerializer();
        var pongBuf = new byte[HeartbeatProtocol.MaxPacketSize];
        long pastTicks = DateTimeOffset.UtcNow.UtcTicks - TimeSpan.FromMilliseconds(50).Ticks;
        int pongLen = serializer.Serialize(new PongPacket { ClientTicks = pastTicks }, pongBuf);
        serverSide.Send(pongBuf.AsSpan(0, pongLen).ToArray());
        serverSide.Send(MakePacket(id: 1));

        // 마커가 OnReceived로 도착할 때까지 폴링 → TCP in-order이므로 그 전에 PONG이 처리됨.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !received.Any(b => PeekId(b) == 1))
            await Task.Delay(20);

        Assert.Contains(received, b => PeekId(b) == 1);                  // 마커는 OnReceived로 전달
        Assert.DoesNotContain(received, b => PeekId(b) == PongPacket.Id); // PONG은 OnReceived로 전달되면 안 됨
        Assert.True(client.Rtt > TimeSpan.Zero, "PONG 수신으로 RTT가 갱신되어야 한다");
    }
}
