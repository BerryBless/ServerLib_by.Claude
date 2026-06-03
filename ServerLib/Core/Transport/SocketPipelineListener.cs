using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

public sealed class SocketPipelineListener : IServerListener
{
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private readonly ISessionRegistrar? _registrar;
    private readonly ConcurrentDictionary<Guid, ISession> _activeSessions = new();

    public bool IsRunning => _listenSocket != null;
    public Func<ISession, ValueTask>? OnClientConnected { get; set; }
    public Func<ISession, ValueTask>? OnClientDisconnected { get; set; }
    public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    public SocketPipelineListener(ISessionRegistrar? registrar = null)
    {
        _registrar = registrar;
    }

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

        // 활성 세션 전체를 동기적으로 정리: DisposeAsync가 I/O 루프를 취소하고 소켓을 닫는다.
        // OnDisconnected 콜백은 ReadPipeAsync의 finally에서 비동기로 실행되므로 Stop() 반환 이후에 발화될 수 있다.
        var sessions = _activeSessions.Values.ToArray();
        foreach (var session in sessions)
        {
            try { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* 개별 세션 정리 실패는 나머지 정리를 중단시키지 않는다 */ }
        }

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
                    _registrar?.Unregister(session.SessionId);
                    _activeSessions.TryRemove(session.SessionId, out _);
                    if (OnClientDisconnected != null)
                        await OnClientDisconnected(session);
                    await session.DisposeAsync();
                };

                _registrar?.Register(session);
                _activeSessions[session.SessionId] = session;
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
