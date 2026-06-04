using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;
using ServerLib.Core.Transport;
using Xunit;

namespace ServerLib.Tests;

/// <summary>P2 리팩토링 보호: DispatchPacketAsync의 동작(일반 패킷→OnReceived, PING→PONG 자동회신)을 고정한다.</summary>
public sealed class SessionDispatchTests
{
    private static (Socket server, Socket client) CreateConnectedPair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        var server = listener.Accept();
        return (server, client);
    }

    [Fact]
    public async Task Dispatch_NormalPacket_InvokesOnReceivedWithFullPacketBytes()
    {
        var (server, client) = CreateConnectedPair();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = new SocketPipelineSession(server);
        session.OnReceived = mem => { tcs.TrySetResult(mem.ToArray()); return ValueTask.CompletedTask; };
        session.StartReceiving();

        // 클라이언트 → 서버: 일반 패킷(id=1, body 0) = 헤더 4바이트만
        var pkt = new byte[PacketPool.HeaderSize];
        PacketPool.WriteHeader(pkt, packetId: 1, bodyLength: 0);
        await client.SendAsync(pkt, SocketFlags.None);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PacketPool.HeaderSize, received.Length);
        Assert.True(PacketPool.TryParseHeader(received, out ushort id, out int body));
        Assert.Equal(1, id);
        Assert.Equal(0, body);

        client.Dispose();
    }

    [Fact]
    public async Task Dispatch_PingPacket_AutoRepliesPong_WithoutInvokingOnReceived()
    {
        var (server, client) = CreateConnectedPair();
        var onReceivedCalled = false;

        await using var session = new SocketPipelineSession(server);
        session.OnReceived = _ => { Volatile.Write(ref onReceivedCalled, true); return ValueTask.CompletedTask; };
        session.StartReceiving();

        // 클라이언트 → 서버: PING
        var ping = new byte[HeartbeatProtocol.MaxPacketSize];
        int pingLen = HeartbeatProtocol.BuildPing(12345L, ping);
        await client.SendAsync(ping.AsMemory(0, pingLen), SocketFlags.None);

        // 서버 → 클라이언트: PONG 자동 회신 수신
        var recv = new byte[HeartbeatProtocol.MaxPacketSize];
        int n = await client.ReceiveAsync(recv, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(n > 0);
        // PONG이 PING의 ClientTicks를 그대로 에코했는지(=RTT 계산 가능) 확인
        Assert.True(HeartbeatProtocol.TryComputeRtt(recv.AsSpan(0, n), nowTicks: 12445L, out long rtt));
        Assert.Equal(100L, rtt);
        Assert.False(Volatile.Read(ref onReceivedCalled), "PING은 앱 OnReceived를 호출하지 않아야 한다");

        client.Dispose();
    }
}
