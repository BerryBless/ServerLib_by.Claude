using System.Net;

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
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="Start"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
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
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="Start"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<ISession, ValueTask>? OnClientDisconnected { get; set; }

    /// <summary>
    /// 세션의 수신 처리 중 예외(손상/악성 패킷 또는 <see cref="OnReceived"/> 핸들러 예외)로 인해
    /// 해당 세션이 강제 종료될 때 호출되는 콜백입니다. (<see cref="ISession.OnReceiveError"/>의 리스너 레벨 통지)
    /// </summary>
    /// <remarks>
    /// <b>[목적:]</b> 에러로 인한 세션 종료를 정상 종료·유휴 타임아웃과 구분해 관측하기 위함입니다.
    /// 이 콜백이 발화한 세션은 직후 <see cref="OnClientDisconnected"/>를 거쳐 해제됩니다.
    /// <br/><br/>
    /// <b>[Thread Context:]</b> 해당 세션의 수신 I/O 스레드에서 호출됩니다. 동기 블로킹 금지.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> 에러 종료 경로에서만, 세션당 최대 1회 발화합니다. 정상 종료 경로에서는 호출되지 않습니다.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="Start"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<ISession, Exception, ValueTask>? OnClientError { get; set; }

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
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="Start"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
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
    /// 리소스 고갈 위험이 있습니다. 프로덕션에서는 반드시 설정하십시오. (이 값을 설정해야만 slowloris 방어가 동작합니다.)</description></item>
    /// <item><description><b>판정 기준(B3):</b> 유휴 판정은 <see cref="ISession.LastProgressAt"/>(마지막 <b>완전한 패킷</b> 수신 시각)을 사용합니다.
    /// 바이트 단위 수신(<see cref="ISession.LastReceivedAt"/>)이 아니므로, 1바이트씩 흘려 타임아웃을 회피하는 trickle/slowloris 공격에도 견고합니다.
    /// 단, 단일 대용량 패킷을 이 기간보다 느리게 전송하는 정상 클라이언트도 정리될 수 있습니다(소형 패킷 위주 서버에서는 무영향).</description></item>
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
    /// <item><description><b>설정 시점:</b> <see cref="Start"/>() 호출 전에만 설정 가능; 이후 <see cref="InvalidOperationException"/>.</description></item>
    /// </list>
    /// </remarks>
    Func<ISession, ValueTask>? OnIdleTimeout { get; set; }

    /// <summary>
    /// 동시에 수용할 세션 수의 상한입니다(B1). <see langword="null"/>(기본값)이면 무제한입니다.
    /// </summary>
    /// <remarks>
    /// <b>[목적:]</b> 연결 폭주로 세션 객체·수신 버퍼가 무제한 적재되어 메모리가 고갈되는 것을 막습니다.
    /// 상한 도달 시 신규 연결은 accept 직후 즉시 닫히며(세션 미생성), <see cref="TotalRejectedConnections"/>가 증가합니다.
    /// <br/><br/>
    /// <b>[보안 주의:]</b> <see langword="null"/>로 두면 단일 클라이언트가 수만 연결로 서버를 고갈시킬 수 있습니다. 프로덕션에서는 반드시 설정하십시오.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="Start"/>() 호출 전에만 설정 가능합니다.
    /// </remarks>
    int? MaxConnections { get; set; }

    /// <summary>
    /// 단일 원격 IP가 동시에 유지할 수 있는 연결 수의 상한입니다(B2). <see langword="null"/>(기본값)이면 무제한입니다.
    /// </summary>
    /// <remarks>
    /// <b>[목적:]</b> 한 출발지 IP가 연결 슬롯을 독점(<see cref="MaxConnections"/> 소진)하는 것을 막습니다.
    /// 상한 초과 시 해당 IP의 신규 연결만 거부되며 다른 IP는 영향받지 않습니다.
    /// <br/><br/>
    /// <b>[범위 한계:]</b> 동시 연결 수 제한이며, 초당 연결/패킷 <b>속도 제한(rate limiting)은 포함하지 않습니다</b>.
    /// 속도 기반 방어가 필요하면 네트워크 계층(LB·방화벽·WAF)에서 보완하십시오.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="Start"/>() 호출 전에만 설정 가능합니다.
    /// </remarks>
    int? MaxConnectionsPerIp { get; set; }

    /// <summary>
    /// 상한(<see cref="MaxConnections"/>·<see cref="MaxConnectionsPerIp"/>) 초과로 거부된 누적 연결 수입니다.
    /// </summary>
    /// <remarks>
    /// <b>[관측성:]</b> 폭주 상황에서 콜백 호출 비용 없이 드롭 규모를 관측하기 위한 카운터입니다(연결당 콜백은 그 자체로 부하가 됨).
    /// <b>[Thread Safety:]</b> Thread-safe(Interlocked).
    /// </remarks>
    long TotalRejectedConnections { get; }

    /// <summary>
    /// 새로 수락되는 각 세션에 적용할 송신 타임아웃입니다. <see langword="null"/>(기본값)이면 송신을 무한 대기합니다.
    /// </summary>
    /// <remarks>
    /// <b>[목적:]</b> 수신을 멈춘(죽은) 피어가 송신 게이트를 영구 점유하여
    /// <see cref="ISessionRegistry.BroadcastAsync"/> 전체가 정지하는 것을 방지합니다.
    /// 시한 초과 시 해당 세션 송신만 <see cref="System.Net.Sockets.SocketException"/>(<see cref="System.Net.Sockets.SocketError.TimedOut"/>)으로 끊기고, 나머지 브로드캐스트는 계속됩니다.
    /// <br/><br/>
    /// <b>[적용 범위:]</b> 이미 수락된 세션에는 소급 적용되지 않으며, 설정 이후 수락되는 세션부터 반영됩니다.
    /// <br/><br/>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="Start"/>() 호출 전에 설정하는 것을 권장합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan? SessionSendTimeout { get; set; }

    /// <summary>
    /// 현재 활성(수신 루프 구동 중) 세션의 수입니다.
    /// </summary>
    /// <remarks>
    /// <b>[관측성:]</b> 연결 폭주(FIN+RST) 이후 이 값이 0으로 복귀하는지로 세션 정리 경로의 완결(누수 부재)을 검증할 수 있습니다.
    /// <br/><br/>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 내부 동시성 컬렉션의 스냅샷 카운트를 반환합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    int ActiveSessionCount { get; }

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
    /// 지정된 바인드 주소와 포트에서 클라이언트 연결 수락을 시작합니다.
    /// </summary>
    /// <param name="port">리슨할 TCP 포트 번호 (1–65535).</param>
    /// <param name="bindAddress">바인드할 IP 주소. <see cref="IPAddress.Loopback"/>으로 지정하면
    /// 루프백 인터페이스(127.0.0.1)에만 노출되어 원격 접근을 차단합니다.
    /// <see cref="IPAddress.Any"/>는 모든 인터페이스에 노출합니다.</param>
    /// <exception cref="InvalidOperationException">이미 실행 중인 상태에서 호출 시 발생합니다.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">포트 바인딩 실패 시 발생합니다.</exception>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. accept 루프는 내부 스레드 풀에서 구동되므로 즉시 반환됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서 호출해야 합니다.
    /// <br/><br/>
    /// <b>[보안:]</b> 관리 포트처럼 내부 전용 리스너는 반드시 <see cref="IPAddress.Loopback"/>을 지정하여
    /// 외부 네트워크 노출을 방지하세요.
    /// </remarks>
    void Start(int port, IPAddress bindAddress);

    /// <summary>
    /// accept 루프를 중지하고 리스닝 소켓을 닫습니다.
    /// 이미 수립된 기존 세션은 강제 종료되지 않습니다.
    /// </summary>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. 취소 신호를 전송하고 즉시 반환됩니다.
    /// 진행 중인 I/O 작업은 백그라운드에서 정리됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Not Thread-safe. <see cref="Start(int)"/>와 동일한 스레드에서 호출해야 합니다.
    /// </remarks>
    void Stop();
}
