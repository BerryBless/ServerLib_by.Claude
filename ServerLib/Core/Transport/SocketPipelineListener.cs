using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

// 구현 은닉(internal): 외부 소비자는 ServerNet.CreateListener()가 반환하는 IServerListener로만 사용한다.
internal sealed class SocketPipelineListener : IServerListener
{
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private readonly ISessionRegistrar? _registrar;
    private readonly ConcurrentDictionary<Guid, ISession> _activeSessions = new();
    // ConcurrentDictionary<IPAddress,int>: IP별 동시 연결 수(B2). IPAddress는 값 기준 Equals/GetHashCode를 구현해 키로 안전.
    // 증가는 단일 accept 루프(직렬), 감소는 다수 OnDisconnected(병렬) → AddOrUpdate/TryUpdate/TryRemove(KVP)로 원자 갱신.
    private readonly ConcurrentDictionary<IPAddress, int> _connectionsPerIp = new();
    private long _rejectedConnections; // Interlocked: 상한 초과 거부 누적(폭주 시 콜백 대신 카운터로 관측)

    public bool IsRunning => _listenSocket != null;

    /// <summary>현재 활성 세션 수입니다. 세션 레지스트리·메트릭 토글과 무관하게 항상 사용 가능합니다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.Count"/> 경유 — 주기적 통계(초 단위)용이라 비용 무시 가능.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. 정수 프로퍼티 읽기.</description></item>
    /// </list>
    /// </remarks>
    // ConcurrentDictionary.Count: 누수 검증의 결정적 신호원 — 폭주(FIN+RST) 후 0 복귀가 정리 경로 완결을 증명한다.
    public int ActiveSessionCount => _activeSessions.Count;

    // E3: 콜백은 Start() 전에만 설정 — IO 루프 가동 중 재할당을 막아 가시성/동작 일관성 보장(IdleTimeout 가드와 동일 패턴).
    private Func<ISession, ValueTask>? _onClientConnected;
    public Func<ISession, ValueTask>? OnClientConnected
    {
        get => _onClientConnected;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnClientConnected는 Start() 호출 전에만 설정할 수 있습니다.");
            _onClientConnected = value;
        }
    }

    private Func<ISession, ValueTask>? _onClientDisconnected;
    public Func<ISession, ValueTask>? OnClientDisconnected
    {
        get => _onClientDisconnected;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnClientDisconnected는 Start() 호출 전에만 설정할 수 있습니다.");
            _onClientDisconnected = value;
        }
    }

    private Func<ISession, ReadOnlyMemory<byte>, ValueTask>? _onReceived;
    public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived
    {
        get => _onReceived;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnReceived는 Start() 호출 전에만 설정할 수 있습니다.");
            _onReceived = value;
        }
    }

    private Func<ISession, Exception, ValueTask>? _onClientError;
    public Func<ISession, Exception, ValueTask>? OnClientError
    {
        get => _onClientError;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnClientError는 Start() 호출 전에만 설정할 수 있습니다.");
            _onClientError = value;
        }
    }
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
    private Func<ISession, ValueTask>? _onIdleTimeout;
    public Func<ISession, ValueTask>? OnIdleTimeout
    {
        get => _onIdleTimeout;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnIdleTimeout은 Start() 호출 전에만 설정할 수 있습니다.");
            _onIdleTimeout = value;
        }
    }

    /// <summary>새로 수락되는 각 세션에 적용할 송신 타임아웃입니다. <see langword="null"/>(기본값)이면 비활성화됩니다.</summary>
    /// <remarks>
    /// 응답하지 않는(수신 버퍼를 비우지 않는) 피어로 인해 세션의 <see cref="ISession.SendAsync"/>가 무한 블록되어
    /// 송신 게이트를 영구 점유하고 <see cref="ISessionRegistry.BroadcastAsync"/>가 정지하는 것을 방지합니다.
    /// 이미 수락된 세션에는 소급 적용되지 않으며, 이후 수락되는 세션부터 반영됩니다.
    /// </remarks>
    public TimeSpan? SessionSendTimeout { get; set; }

    private int? _maxConnections;
    public int? MaxConnections
    {
        get => _maxConnections;
        set
        {
            if (IsRunning) throw new InvalidOperationException("MaxConnections는 Start() 호출 전에만 설정할 수 있습니다.");
            _maxConnections = value;
        }
    }

    private int? _maxConnectionsPerIp;
    public int? MaxConnectionsPerIp
    {
        get => _maxConnectionsPerIp;
        set
        {
            if (IsRunning) throw new InvalidOperationException("MaxConnectionsPerIp는 Start() 호출 전에만 설정할 수 있습니다.");
            _maxConnectionsPerIp = value;
        }
    }

    public long TotalRejectedConnections => Interlocked.Read(ref _rejectedConnections);

    public SocketPipelineListener(ISessionRegistrar? registrar = null)
    {
        _registrar = registrar;
    }

    // 거부 처리: 소켓을 닫고 거부 카운터를 증가시킨다. accept 직후 세션 미생성 경로라 Pipe 등 자원 할당 없음(B4).
    private void RejectConnection(Socket socket)
    {
        Interlocked.Increment(ref _rejectedConnections);
        try { socket.Dispose(); } catch { /* 이미 닫힘/오류는 무시 */ }
    }

    // IP 동시 연결 수 1 감소. 0 도달 시 엔트리를 제거해 _connectionsPerIp가 무한히 커지는 것을 막는다(IP 순회 공격 방어).
    // TryRemove(KeyValuePair)/TryUpdate는 "값이 여전히 c일 때만" 적용되는 CAS라 병렬 증감과 경합해도 정확하다.
    private void DecrementIp(IPAddress ip)
    {
        while (_connectionsPerIp.TryGetValue(ip, out var c))
        {
            if (c <= 1)
            {
                if (_connectionsPerIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, c))) return;
            }
            else if (_connectionsPerIp.TryUpdate(ip, c - 1, c))
            {
                return;
            }
            // 경합으로 실패 시 최신값으로 재시도
        }
    }

    public void Start(int port) => StartCore(port, IPAddress.Any);

    public void Start(int port, IPAddress bindAddress) => StartCore(port, bindAddress);

    private void StartCore(int port, IPAddress bindAddress)
    {
        if (IsRunning) throw new InvalidOperationException("Already running.");

        _cts = new CancellationTokenSource();
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // SO_REUSEADDR: 직전 종료 소켓이 TIME_WAIT(수 분) 상태여도 동일 포트 재바인드 허용 → 서버 재시작 즉시 복구
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(new IPEndPoint(bindAddress, port));
        // backlog: 커널의 accept 대기 큐 크기(앱 큐가 아닌 OS 큐). 순간 연결 폭주를 버퍼링하고 초과 SYN은 커널이 드롭
        _listenSocket.Listen(backlog: 512);

        // Start()는 동기 반환, 루프는 _cts 취소까지 무한 구동하므로 await 없이 분리 구동(fire-and-forget)
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
            // 동기 Stop()에서 async Dispose 완료까지 블록(sync-over-async). I/O 스레드가 아닌 종료 경로라 데드락 위험 없음.
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
                // B3: 바이트 기준(LastReceivedAt)이 아니라 "완전한 패킷" 진척 기준(LastProgressAt)으로 판정한다.
                // → 1바이트씩 흘려 byte-idle을 회피하는 trickle/slowloris도 진척이 없으면 정리된다.
                if (now - kvp.Value.LastProgressAt <= timeout) continue;
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
                // AcceptAsync: 커널 큐의 다음 연결을 비동기 대기 — 동기 Accept()와 달리 스레드풀 스레드를 점유하지 않음
                var clientSocket = await _listenSocket!.AcceptAsync(ct);
                ConfigureSocket(clientSocket);

                // B1: 동시 세션 상한. 단일 accept 루프이므로 Count 검사→등록이 다른 accept와 경합하지 않는다
                // (동시 제거는 Count만 줄여 보수적으로 동작). _activeSessions는 EnableSessionRegistry 토글과 무관하게
                // 항상 채워지므로 이 게이트는 토글 상태와 독립적이다.
                if (_maxConnections is int max && _activeSessions.Count >= max)
                {
                    RejectConnection(clientSocket);
                    continue;
                }

                // B2: IP당 동시 연결 상한. 원자적으로 1 증가 후 초과면 롤백·거부.
                IPAddress? ip = (clientSocket.RemoteEndPoint as IPEndPoint)?.Address;
                bool ipReserved = false;
                if (_maxConnectionsPerIp is int perIp && ip is not null)
                {
                    if (_connectionsPerIp.AddOrUpdate(ip, 1, static (_, c) => c + 1) > perIp)
                    {
                        DecrementIp(ip); // 초과분 즉시 롤백
                        RejectConnection(clientSocket);
                        continue;
                    }
                    ipReserved = true;
                }

                // B4: 위 저비용 검사를 통과한 연결만 세션·Pipe를 할당한다(거부 연결은 자원 미할당).
                SocketPipelineSession session;
                try
                {
                    session = new SocketPipelineSession(clientSocket) { SendTimeout = SessionSendTimeout };
                    session.OnReceived = data => OnReceived?.Invoke(session, data) ?? ValueTask.CompletedTask;
                    session.OnReceiveError = ex => OnClientError?.Invoke(session, ex) ?? ValueTask.CompletedTask;
                    session.OnDisconnected = async () =>
                    {
                        // OnDisconnected는 세션당 정확히 1회 발화 → IP 예약 해제의 유일·확정 지점(accept의 증가 1회와 짝).
                        if (ipReserved && ip is not null) DecrementIp(ip);
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
                    session.StartReceiving(); // 이 시점부터 OnDisconnected 발화 보장 → 이후 IP 예약 해제는 그쪽이 담당
                }
                catch
                {
                    // StartReceiving 이전(세션 생성·등록 중) 실패 시 OnDisconnected가 발화하지 않아 IP 예약이 누수된다.
                    // 여기서 직접 해제해 leak 윈도를 닫고, 세션 설정 예외가 accept 루프를 죽이지 않도록 다음 연결로 진행한다.
                    if (ipReserved && ip is not null) DecrementIp(ip);
                    RejectConnection(clientSocket);
                    continue;
                }

                // OnClientConnected는 leak 윈도 밖(StartReceiving 성공 후)에서 호출 — 여기서 throw해도 IP 해제는 OnDisconnected가 담당.
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
        // TCP keep-alive: 유휴 후 OS가 주기적 probe로 죽은 피어(NAT 매핑 만료·WiFi 끊김 등)를 탐지 → 좀비 세션 방지
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }
}
