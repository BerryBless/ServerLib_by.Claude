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
    private TimeSpan? _idleTimeout;
    public TimeSpan? IdleTimeout
    {
        get => _idleTimeout;
        set
        {
            if (IsRunning) throw new InvalidOperationException(
                "IdleTimeout은 Start() 호출 전에만 설정할 수 있습니다.");
            _idleTimeout = value;
        }
    }
    public Func<ISession, ValueTask>? OnIdleTimeout { get; set; }

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
        if (_idleTimeout.HasValue)
            _ = IdleSweepLoopAsync(_idleTimeout.Value, _cts.Token);
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

    private async Task IdleSweepLoopAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 스윕 간격 = timeout/2 (최소 10ms) → 최대 1.5× timeout 후 감지
        var interval = TimeSpan.FromTicks(Math.Max(timeout.Ticks / 2, TimeSpan.FromMilliseconds(10).Ticks));
        using var timer = new PeriodicTimer(interval);
        var idleSessions = new List<ISession>(); // 틱마다 재사용 — per-tick 할당 방지

        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            idleSessions.Clear();

            // H5: .Values 대신 직접 열거 (LOH 할당 방지)
            // Critical: TryRemove 선점으로 이중 발화 방지
            foreach (var kvp in _activeSessions)
            {
                if (now - kvp.Value.LastReceivedAt <= timeout) continue;
                if (_activeSessions.TryRemove(kvp.Key, out var removed))
                    idleSessions.Add(removed);
            }

            if (idleSessions.Count == 0) continue;

            // H6: 병렬 처리 (MaxDegreeOfParallelism=4로 대량 타임아웃 블로킹 방지)
            await Parallel.ForEachAsync(
                idleSessions,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (session, _) =>
                {
                    // H1: OnIdleTimeout 예외 시에도 DisposeAsync 보장
                    if (OnIdleTimeout != null)
                    {
                        try { await OnIdleTimeout(session); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { /* 콜백 실패 — 세션 정리는 계속 진행 */ }
                    }
                    // H7: OperationCanceledException은 전파, 나머지는 격리
                    try { await session.DisposeAsync(); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { /* 개별 세션 정리 실패가 다른 세션 처리를 중단시키지 않는다 */ }
                });
        }
    }

    /// <summary>테스트 전용 — 세션을 _activeSessions에 직접 주입합니다.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal void InjectSessionForTest(ISession session)
        => _activeSessions[session.SessionId] = session;

    /// <summary>테스트 전용 — IdleSweepLoopAsync를 Start() 없이 직접 시작합니다.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal Task StartIdleSweepForTest(CancellationToken ct)
    {
        if (!_idleTimeout.HasValue)
            throw new InvalidOperationException("StartIdleSweepForTest는 IdleTimeout이 설정된 후에만 호출할 수 있습니다.");
        return IdleSweepLoopAsync(_idleTimeout.Value, ct);
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
                    session.TransitionTo(SessionState.Disconnected);
                    if (OnClientDisconnected != null)
                        await OnClientDisconnected(session);
                    await session.DisposeAsync();
                };

                _registrar?.Register(session);
                _activeSessions[session.SessionId] = session;
                session.TransitionTo(SessionState.Connected);
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
