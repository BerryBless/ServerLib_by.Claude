namespace ServerLib.Interface;

/// <summary>
/// TCP 포트에서 클라이언트 연결을 수락하고 세션을 관리하는 서버 리스너 인터페이스입니다.
/// accept 루프의 시작/중지와 연결·수신·해제 이벤트 콜백을 정의합니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description>모든 콜백은 <see cref="ValueTask"/>를 반환하여 hot path 할당을 제거합니다.</description></item>
/// <item><description>구현체는 accept 루프를 내부 스레드 풀에서 구동하므로 호출 스레드를 블로킹하지 않습니다.</description></item>
/// <item><description>세션 수명 관리(해제 시점 결정)는 리스너가 담당합니다. 소비자는 <see cref="OnClientDisconnected"/> 콜백에서 정리 작업만 수행하면 됩니다.</description></item>
/// </list>
/// </remarks>
public interface IServerListener
{
    /// <summary>
    /// 서버 리스너가 현재 accept 루프를 구동 중인지 나타냅니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. <see cref="Start"/>와 <see cref="Stop"/> 사이의 상태를 반영합니다.
    /// </remarks>
    bool IsRunning { get; }

    /// <summary>
    /// 새 클라이언트가 접속하여 세션이 수립되었을 때 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> accept 루프를 구동하는 I/O 스레드 풀에서 호출됩니다.
    /// 콜백 내부에서 동기 블로킹을 수행하면 다음 클라이언트의 accept가 지연됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 전달되는 <see cref="ISession"/>은 이미 생성된 객체입니다.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> <see cref="OnClientDisconnected"/>보다 항상 먼저 발화됩니다.
    /// </remarks>
    Func<ISession, ValueTask>? OnClientConnected { get; set; }

    /// <summary>
    /// 클라이언트 연결이 종료되어 세션이 해제될 때 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> I/O 스레드 풀에서 호출됩니다.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> 세션당 정확히 1회 호출됩니다. 이 콜백 반환 후 <see cref="ISession"/>은 해제됩니다.
    /// 콜백 내부에서 세션 참조를 캐싱한 경우 반드시 제거해야 합니다.
    /// </remarks>
    Func<ISession, ValueTask>? OnClientDisconnected { get; set; }

    /// <summary>
    /// 클라이언트로부터 데이터가 수신될 때 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> 해당 세션의 수신 루프를 담당하는 I/O 스레드에서 호출됩니다.
    /// 다수의 세션이 동시에 수신 중이면 여러 스레드에서 병렬 호출될 수 있습니다.
    /// <br/><br/>
    /// <b>[Memory Policy:]</b> 두 번째 인자 <see cref="ReadOnlyMemory{T}"/>는 내부 수신 버퍼의 슬라이스입니다.
    /// 콜백이 반환되면 해당 메모리 슬라이스는 무효화됩니다.
    /// 콜백 범위를 벗어나 데이터를 보관하려면 반드시 복사해야 합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 단일 세그먼트 수신 시 Zero-allocation 보장.
    /// </remarks>
    Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    /// <summary>
    /// 유휴 세션 타임아웃 기간입니다. <see langword="null"/>이면 유휴 감지를 비활성화합니다(기본값).
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="Start"/>() 호출 전에 설정해야 합니다.
    /// Start() 이후 변경 시 <see cref="InvalidOperationException"/>이 발생합니다.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>보안 주의:</b> <see langword="null"/>(기본값) 상태로 프로덕션 배포 시 유휴 세션이 무제한 잔류하여
    /// 리소스 고갈 위험이 있습니다. 프로덕션에서는 반드시 설정하십시오.
    /// 또한 소켓 레이어 수신 기반이므로 최소 데이터(1바이트) 전송으로 타임아웃 회피가 가능합니다.
    /// 절대 수명 제한이 필요한 경우 <see cref="ISession.ConnectedAt"/>을 별도로 감시하십시오.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan? IdleTimeout { get; set; }

    /// <summary>
    /// 세션이 유휴 타임아웃으로 해제되기 직전 호출되는 콜백입니다.
    /// <see cref="ISession.OnDisconnected"/>보다 먼저 발화됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 스윕 루프 내부 스레드에서 호출됩니다. 동기 블로킹 금지.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    Func<ISession, ValueTask>? OnIdleTimeout { get; set; }

    /// <summary>
    /// 지정된 포트에서 클라이언트 연결 수락을 시작합니다.
    /// </summary>
    /// <param name="port">리슨할 TCP 포트 번호 (1–65535).</param>
    /// <exception cref="InvalidOperationException">이미 실행 중인 상태에서 호출 시 발생합니다.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">포트 바인딩 실패 시 발생합니다.</exception>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. accept 루프는 내부 스레드 풀에서 구동되므로 즉시 반환됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서 호출해야 합니다.
    /// </remarks>
    void Start(int port);

    /// <summary>
    /// accept 루프를 중지하고 리스닝 소켓을 닫습니다.
    /// 이미 수립된 기존 세션은 강제 종료되지 않습니다.
    /// </summary>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. 취소 신호를 전송하고 즉시 반환됩니다.
    /// 진행 중인 I/O 작업은 백그라운드에서 정리됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Not Thread-safe. <see cref="Start"/>와 동일한 스레드에서 호출해야 합니다.
    /// </remarks>
    void Stop();
}
