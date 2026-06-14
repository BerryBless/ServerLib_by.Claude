using ServerLib.Core;
using ServerLib.Interface;

namespace ServerLib.Examples.Examples;

/// <summary>
/// <see cref="ISession"/>의 전체 수명주기와 속성을 시연합니다.
/// 세션 상태 전이, 커스텀 컨텍스트, LastReceivedAt/LastProgressAt 구분,
/// <see cref="SessionContextExtensions"/> 두 오버로드, <see cref="SessionState"/> 모든 값 타입 기능을 다룹니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="ISession.SessionId"/> / <see cref="ISession.RemoteEndPoint"/> / <see cref="ISession.ConnectedAt"/> / <see cref="ISession.LastReceivedAt"/> / <see cref="ISession.LastProgressAt"/></description></item>
/// <item><description><see cref="ISession.State"/> / <see cref="ISession.TransitionTo"/></description></item>
/// <item><description><see cref="ISession.Context"/> / <see cref="SessionContextExtensions.GetContext{T}"/> / <see cref="SessionContextExtensions.TryGetContext{T}"/></description></item>
/// <item><description><see cref="SessionState"/> — Connecting/Connected/Authenticated/Disconnecting/Disconnected + <see cref="SessionState.Custom"/> + 연산자 + ToString</description></item>
/// <item><description><see cref="IServerListener.OnClientDisconnected"/></description></item>
/// </list>
/// </remarks>
internal static class SessionLifecycle
{
    /// <summary>
    /// 루프백 서버를 구동하고 클라이언트 연결 이후 세션의 모든 속성과 상태 전이를 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 예제 진입점은 단일 스레드. 콜백은 I/O 스레드에서 실행됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> record GameContext 1회 힙 할당(세션당).
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. <see cref="TaskCompletionSource"/>로 결정적 동기화합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        // ── SessionState 값 타입 동작 먼저 시연 (소켓 불필요) ──
        Console.WriteLine("  [SessionState] 사전 정의 5종:");
        Console.WriteLine($"    Connecting={SessionState.Connecting}  (Value={SessionState.Connecting.Value})");
        Console.WriteLine($"    Connected={SessionState.Connected}    (Value={SessionState.Connected.Value})");
        Console.WriteLine($"    Authenticated={SessionState.Authenticated} (Value={SessionState.Authenticated.Value})");
        Console.WriteLine($"    Disconnecting={SessionState.Disconnecting} (Value={SessionState.Disconnecting.Value})");
        Console.WriteLine($"    Disconnected={SessionState.Disconnected}   (Value={SessionState.Disconnected.Value})");

        // SessionState.Custom: 예약 값(0~4) 이후의 앱 정의 상태를 만듭니다.
        var lobbyState = SessionState.Custom(5);   // 앱 정의: 로비 대기 중
        var inGameState = SessionState.Custom(10); // 앱 정의: 게임 중
        Console.WriteLine($"  [SessionState] 앱 정의: lobby={lobbyState}, inGame={inGameState}");

        // == / != 연산자 및 ToString
        Console.WriteLine($"  [SessionState] lobby == inGame: {lobbyState == inGameState}");
        Console.WriteLine($"  [SessionState] lobby != Connected: {lobbyState != SessionState.Connected}");
        Console.WriteLine($"  [SessionState] Connected.ToString(): \"{SessionState.Connected}\"");

        // Custom(예약 범위) → ArgumentOutOfRangeException 확인
        try
        {
            _ = SessionState.Custom(3); // 예약값(≤4) → 예외
            Console.WriteLine("  [SessionState] Custom(3): 예외 없음 (예상하지 못한 동작)");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine($"  [SessionState] Custom(3) → {ex.GetType().Name} (정상)");
        }

        // ── TCP 연결로 ISession 속성 시연 ──
        // TaskCompletionSource: 연결·연결 해제 이벤트를 비동기 콜백에서 결정적으로 대기.
        var connectedTcs = new TaskCompletionSource();
        var disconnectedTcs = new TaskCompletionSource();

        IServerListener listener = ServerNet.CreateListener();

        // ⚠️ 세션별 OnReceived/OnDisconnected/OnReceiveError는 라이브러리 소유:
        //    SocketPipelineSession.StartReceiving() 호출 이후에는 setter가 InvalidOperationException을 던집니다.
        //    Listener가 OnClientConnected 호출 전에 이미 StartReceiving()을 완료하므로
        //    OnClientConnected 안에서 session.OnReceived 등을 할당하면 throw가 발생합니다.
        //    동등한 공개 경로는 IServerListener.OnReceived 입니다.
        //
        //    단, getter는 항상 안전하게 읽을 수 있습니다.

        ISession? capturedSession = null;
        bool transitionToAuthResult = false;
        bool transitionAfterDisconnectResult = false; // Disconnected 이후 전이 시도 결과

        listener.OnClientConnected = session =>
        {
            capturedSession = session;

            // ── ISession 기본 속성 읽기 ──
            Console.WriteLine($"  [세션] SessionId   : {session.SessionId}");
            Console.WriteLine($"  [세션] RemoteEndPoint: {session.RemoteEndPoint}");
            Console.WriteLine($"  [세션] ConnectedAt  : {session.ConnectedAt:HH:mm:ss.fff}");
            Console.WriteLine($"  [세션] LastReceivedAt: {session.LastReceivedAt:HH:mm:ss.fff}");
            // LastProgressAt: 마지막으로 완전한 패킷이 도착한 시각.
            // LastReceivedAt은 바이트 수신 시, LastProgressAt은 패킷 프레이밍이 완성될 때 갱신됩니다.
            // slowloris 공격(헤더만 조금씩 보내는 지연 공격)에서 두 값이 달라집니다.
            Console.WriteLine($"  [세션] LastProgressAt: {session.LastProgressAt:HH:mm:ss.fff}");

            // ── 현재 상태 확인 ──
            Console.WriteLine($"  [세션] State 초기값: {session.State} (Connected=={session.State == SessionState.Connected})");

            // ── ISession.Context: 임의 사용자 데이터 저장 ──
            // Volatile.Write로 저장되므로 다른 스레드에서도 최신값이 보입니다.
            session.Context = new GameContext(42, "플레이어42");
            Console.WriteLine($"  [세션] Context 설정 완료: {session.Context}");

            // ── SessionContextExtensions.GetContext<T>: 타입 안전 캐스팅 헬퍼 ──
            var ctx = session.GetContext<GameContext>();
            Console.WriteLine($"  [세션] GetContext<GameContext>(): PlayerId={ctx?.PlayerId}, Nickname={ctx?.Nickname}");

            // ── SessionContextExtensions.TryGetContext<T>: null 안전 버전 ──
            if (session.TryGetContext<GameContext>(out var ctx2))
                Console.WriteLine($"  [세션] TryGetContext<GameContext>(): PlayerId={ctx2.PlayerId} (성공)");

            // 잘못된 타입으로 TryGetContext → false 반환
            bool wrongType = session.TryGetContext<string>(out _);
            Console.WriteLine($"  [세션] TryGetContext<string>(): {wrongType} (GameContext이므로 실패)");

            // ── TransitionTo(Authenticated): 앱 레벨 상태 전이 ──
            // TransitionTo는 내부적으로 Interlocked.CompareExchange로 원자 전이합니다.
            // 반환값: 전이 성공 시 true, 이미 해당 상태거나 Disconnected이면 false.
            transitionToAuthResult = session.TransitionTo(SessionState.Authenticated);
            Console.WriteLine($"  [세션] TransitionTo(Authenticated): {transitionToAuthResult}, 현재={session.State}");

            // 라이브러리 소유 콜백 getter 읽기 — setter는 이미 라이브러리가 배선했으므로 읽기만 안전
            bool hasLibraryOnReceived = session.OnReceived != null;
            Console.WriteLine($"  [세션] session.OnReceived(getter): null 아님={hasLibraryOnReceived} (라이브러리 소유)");

            connectedTcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        listener.OnClientDisconnected = session =>
        {
            // Disconnected 상태 이후 TransitionTo 시도 → false 반환(되돌릴 수 없음)
            transitionAfterDisconnectResult = session.TransitionTo(SessionState.Authenticated);
            Console.WriteLine($"  [세션] OnClientDisconnected: State={session.State}");
            Console.WriteLine($"  [세션] Disconnected 이후 TransitionTo(Authenticated): {transitionAfterDisconnectResult} (false 예상)");
            disconnectedTcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        int port = ExampleHarness.GetFreePort();
        listener.Start(port);

        await using IClientConnection conn = ServerNet.CreateClient();
        conn.OnConnected = () => ValueTask.CompletedTask;
        conn.OnDisconnected = () => ValueTask.CompletedTask;
        await conn.ConnectAsync(ExampleHarness.LoopbackHost, port);

        // 연결 완료 대기
        await ExampleHarness.WaitSignaledAsync(connectedTcs, TimeSpan.FromSeconds(5));

        // 연결 해제 후 Disconnected 상태 전이 시연
        conn.Disconnect();
        await ExampleHarness.WaitSignaledAsync(disconnectedTcs, TimeSpan.FromSeconds(5));

        // 결과 검증
        if (transitionToAuthResult != true)
            throw new InvalidOperationException("TransitionTo(Authenticated)가 true를 반환해야 합니다.");
        if (transitionAfterDisconnectResult != false)
            throw new InvalidOperationException("Disconnected 이후 TransitionTo는 false를 반환해야 합니다.");

        listener.Stop();
        Console.WriteLine("[OK] 02_SessionLifecycle");
    }
}
