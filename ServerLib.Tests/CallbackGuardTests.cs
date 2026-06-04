using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Transport;
using Xunit;

namespace ServerLib.Tests;

/// <summary>E3: 콜백을 Start/Connect/StartReceiving 이후 설정하면 InvalidOperationException.</summary>
public sealed class CallbackGuardTests
{
    // 연결된 loopback 소켓 쌍(세션 테스트용)
    private static (Socket server, Socket client) CreateConnectedPair()
    {
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        var port = ((IPEndPoint)l.LocalEndPoint!).Port;
        var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        c.Connect(IPAddress.Loopback, port);
        var s = l.Accept();
        return (s, c);
    }

    [Fact]
    public void Listener_SetCallbackAfterStart_Throws()
    {
        var listener = new SocketPipelineListener();
        listener.Start(0); // 0 = OS가 임시 포트 할당, accept 루프 시작
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => listener.OnReceived = (_, _) => ValueTask.CompletedTask);
            Assert.Throws<InvalidOperationException>(
                () => listener.OnClientConnected = _ => ValueTask.CompletedTask);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Client_SetCallbackAfterConnect_Throws()
    {
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        var port = ((IPEndPoint)l.LocalEndPoint!).Port;

        await using var client = new SocketPipelineClient();
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port);
        using var serverSide = l.Accept();

        Assert.Throws<InvalidOperationException>(
            () => client.OnReceived = _ => ValueTask.CompletedTask);
    }

    [Fact]
    public async Task Session_SetCallbackAfterStartReceiving_Throws()
    {
        var (server, client) = CreateConnectedPair();
        await using var session = new SocketPipelineSession(server);
        session.StartReceiving();

        Assert.Throws<InvalidOperationException>(
            () => session.OnReceived = _ => ValueTask.CompletedTask);

        client.Dispose();
    }
}
