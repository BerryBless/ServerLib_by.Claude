using System.Net;
using System.Net.Sockets;
using ServerLib.Core;
using ServerLib.Core.Transport;
using Xunit;

namespace ServerLib.Tests;

/// <summary>P3 회귀: 수신 버퍼를 비우지 않는 피어에 대해 SendAsync가 SendTimeout으로 끊기는지 검증.</summary>
public sealed class SessionSendTimeoutTests
{
    // 연결된 loopback 소켓 쌍을 만든다. server는 SocketPipelineSession이 감싸고, client는 절대 읽지 않는다.
    private static (Socket server, Socket client) CreateConnectedPair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        var server = listener.Accept();
        // 송신 버퍼를 작게 잡아 빠르게 가득 차도록(피어가 안 읽으면 곧 블록)
        server.SendBufferSize = 4096;
        client.ReceiveBufferSize = 4096;
        return (server, client);
    }

    [Fact]
    public async Task SendAsync_PeerNotDraining_WithSendTimeout_ThrowsTimedOut()
    {
        var (server, client) = CreateConnectedPair();
        await using var session = new SocketPipelineSession(server)
        {
            SendTimeout = TimeSpan.FromMilliseconds(200)
        };

        // 단일 SendAsync는 부분 송신 후 반환하므로, 64KB 청크를 반복 송신해 커널 송신 버퍼를 가득 채운다.
        // 피어가 안 읽으면 어느 시점의 SendAsync가 블록되고, SendTimeout이 이를 SocketException(TimedOut)으로 끊어야 한다.
        var chunk = new byte[64 * 1024];
        async Task SendUntilBlocked()
        {
            for (int i = 0; i < 100_000; i++)
                await session.SendAsync(chunk);
        }

        // 5초 데드라인: 메커니즘이 깨졌으면(취소가 블록된 송신을 중단 못하면) hang 대신 TimeoutException으로 '실패'하게 한다.
        var ex = await Assert.ThrowsAsync<SocketException>(
            async () => await SendUntilBlocked().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SocketError.TimedOut, ex.SocketErrorCode);

        client.Dispose();
    }

    [Fact]
    public async Task BroadcastAsync_OneWedgedPeer_StillDeliversToOthers_AndReturns()
    {
        var registry = new SessionRegistry();
        var timeout = TimeSpan.FromMilliseconds(200);

        // 정상 피어: client를 계속 읽어(drain) 송신이 막히지 않는다.
        var (goodServer, goodClient) = CreateConnectedPair();
        var goodSession = new SocketPipelineSession(goodServer) { SendTimeout = timeout };

        // 막힌 피어: client를 읽지 않아 송신 버퍼가 차면 블록 → SendTimeout으로 끊겨야 한다.
        var (badServer, badClient) = CreateConnectedPair();
        var badSession = new SocketPipelineSession(badServer) { SendTimeout = timeout };

        registry.Register(goodSession);
        registry.Register(badSession);

        // 정상 피어 drain: 백그라운드에서 수신 바이트를 누적
        using var drainCts = new CancellationTokenSource();
        long goodReceived = 0;
        var drain = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (!drainCts.Token.IsCancellationRequested)
                {
                    int n = await goodClient.ReceiveAsync(buf, SocketFlags.None, drainCts.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref goodReceived, n);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        });

        // 막힌 피어의 송신 버퍼를 미리 가득 채운다(다음 broadcast 송신이 즉시 블록되도록).
        try
        {
            var chunk = new byte[64 * 1024];
            for (int i = 0; i < 100_000; i++)
                await badSession.SendAsync(chunk);
        }
        catch (SocketException) { /* 버퍼 가득 → TimedOut 발생 시점 = 사전 충전 완료 */ }

        // Act: 한 세션이 막혀 있어도 broadcast는 정상 피어에 전달하고 5초 내 반환해야 한다.
        var message = new byte[256];
        await registry.BroadcastAsync(message).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // 정상 피어가 broadcast 메시지를 수신했는지 확인(짧은 대기로 drain 반영)
        await Task.Delay(100);
        Assert.True(Interlocked.Read(ref goodReceived) >= message.Length,
            "막힌 피어가 있어도 정상 피어는 broadcast를 수신해야 한다");

        drainCts.Cancel();
        await drain;
        await goodSession.DisposeAsync();
        await badSession.DisposeAsync();
        goodClient.Dispose();
        badClient.Dispose();
    }
}
