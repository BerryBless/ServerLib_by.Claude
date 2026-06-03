using System.Net;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

public sealed class SocketPipelineListener : IServerListener
{
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listenSocket != null;
    public Func<ISession, ValueTask>? OnClientConnected { get; set; }
    public Func<ISession, ValueTask>? OnClientDisconnected { get; set; }
    public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    /// <summary>
    /// 세션 레지스트리입니다. 설정하면 연결/해제 시 자동으로 Register/Unregister됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Start() 호출 전에 설정해야 합니다. 서버 동작 중 교체는 지원하지 않습니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    public ISessionRegistry? Registry { get; set; }

    public void Start(int port)
    {
        if (IsRunning) throw new InvalidOperationException("Already running.");

        _cts = new CancellationTokenSource();
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        _listenSocket.Listen(backlog: 512);

        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listenSocket?.Dispose();
        _listenSocket = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await _listenSocket!.AcceptAsync(ct);
                ConfigureSocket(clientSocket);
                var session = new SocketPipelineSession(clientSocket);
                session.OnReceived = data => OnReceived?.Invoke(session, data) ?? ValueTask.CompletedTask;
                session.OnDisconnected = async () =>
                {
                    Registry?.Unregister(session.SessionId);
                    if (OnClientDisconnected != null)
                        await OnClientDisconnected(session);
                    await session.DisposeAsync();
                };

                Registry?.Register(session);
                session.StartReceiving();

                if (OnClientConnected != null)
                    await OnClientConnected(session);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) { }
        }
    }

    private static void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = true;  // Nagle 알고리즘 비활성화 (게임 서버 필수)
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }
}
