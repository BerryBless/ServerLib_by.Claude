using System.Net;

namespace ServerLib.Interface;

/// <summary>
/// 서버에 연결된 클라이언트 1개를 나타내는 세션 인터페이스입니다.
/// 세션의 생명주기(연결 → 수신/송신 → 해제)를 정의하며,
/// GC 압력을 최소화하는 Zero-allocation 설계를 계약으로 강제합니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description>모든 구현체는 <see cref="IAsyncDisposable"/>을 통해 비동기 정리를 지원해야 합니다.</description></item>
/// <item><description>수신 데이터는 <see cref="ReadOnlyMemory{T}"/> 슬라이스로만 전달되며, 중간 복사를 금지합니다.</description></item>
/// <item><description>송신 경로(<see cref="SendAsync"/>)는 <see cref="ValueTask"/>를 반환하여 hot path 힙 할당을 제거합니다.</description></item>
/// </list>
/// </remarks>
public interface ISession : IAsyncDisposable
{
    /// <summary>
    /// 세션의 전역 고유 식별자입니다. 생성 시 자동 발급되며 불변입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 읽기 전용 속성이므로 모든 스레드에서 안전하게 접근 가능합니다.
    /// </remarks>
    Guid SessionId { get; }

    /// <summary>
    /// 연결된 클라이언트의 원격 엔드포인트(IP 주소 및 포트)입니다.
    /// UDP 기반 가상 연결이나 로컬 파이프 연결의 경우 <see langword="null"/>일 수 있습니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 연결 수립 후 불변입니다.
    /// </remarks>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// 세션이 수립된 정확한 시각(UTC)입니다.
    /// 세션 만료 판정, 연결 지속 시간 모니터링에 활용합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 생성 후 불변입니다.
    /// </remarks>
    DateTimeOffset ConnectedAt { get; }

    /// <summary>
    /// 마지막으로 데이터를 수신한 정확한 시각(UTC)입니다.
    /// 연결 수립 시 <see cref="ConnectedAt"/>과 동일값으로 초기화되며,
    /// 데이터 수신마다 원자적으로 갱신됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Interlocked 기반으로 원자적 갱신됩니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    DateTimeOffset LastReceivedAt { get; }

    /// <summary>
    /// 마지막으로 <b>완전한 패킷 1개</b>를 프레이밍한 정확한 시각(UTC)입니다. 연결 수립 시 <see cref="ConnectedAt"/>과 동일값으로 초기화됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[목적(B3):]</b> <see cref="LastReceivedAt"/>은 <b>바이트 1개라도</b> 수신되면 갱신되므로,
    /// 공격자가 타임아웃보다 짧은 주기로 1바이트씩 흘리면(slowloris/slow-read) 유휴 감지를 회피할 수 있습니다.
    /// 이 속성은 <b>의미 있는 진척(완전한 패킷 수신)</b>이 있을 때만 갱신되므로, 유휴 타임아웃 판정의 안전한 기준이 됩니다.
    /// 헤더만 보내고 본문을 흘리는 연결, 연결 후 침묵하는 연결 모두 진척이 없어 정리 대상이 됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe. Interlocked 기반 원자적 갱신·읽기.
    /// <b>[Memory Allocation:]</b> Zero-allocation. <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    DateTimeOffset LastProgressAt { get; }

    /// <summary>
    /// 현재 세션의 생명주기 상태입니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile read로 최신값을 반환합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation (값 타입).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    SessionState State { get; }

    /// <summary>
    /// 세션 상태를 새 상태로 전환합니다.
    /// </summary>
    /// <param name="newState">전환할 새 상태</param>
    /// <returns>
    /// 전환이 적용되면 <see langword="true"/>. 세션이 이미 종착 상태(<see cref="SessionState.Disconnected"/>)이면
    /// 부활을 막기 위해 전환을 거부하고 <see langword="false"/>를 반환합니다.
    /// </returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. CAS(Interlocked.CompareExchange)로 원자적 전환하여 동시 호출 시에도 종착 상태를 보존합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// <b>[상태 소유권:]</b> transport 생명주기 상태(<see cref="SessionState.Connecting"/>·<see cref="SessionState.Connected"/>·
    /// <see cref="SessionState.Disconnecting"/>·<see cref="SessionState.Disconnected"/>)는 서버 라이브러리가 소유·구동합니다.
    /// 소비자가 이 상태로 직접 전환하면 보고 상태와 실제 소켓 상태가 어긋날 수 있습니다.
    /// 소비자는 <see cref="SessionState.Authenticated"/> 또는 <see cref="SessionState.Custom(System.Int32)"/>(앱 레벨) 상태만 설정하십시오.
    /// 하드 강제는 <see cref="SessionState.Disconnected"/> 부활 차단(CAS)뿐이며, 그 외는 규약입니다.
    /// </remarks>
    bool TransitionTo(SessionState newState);

    /// <summary>
    /// 세션에 부착된 단일 사용자 컨텍스트 객체입니다. 사용자가 직접 클래스를 정의하여 할당하며, 읽을 때 캐스팅이 필요합니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile read/write로 참조를 원자적으로 갱신합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 참조 타입 할당 시 Zero-allocation. 단, 값 타입(struct/int 등)을 할당하면 박싱이 발생하므로 hot path에서는 반드시 참조 타입(class/record) 컨텍스트를 사용하십시오.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// <item><description><b>생명주기:</b> 세션이 해제(<see cref="IAsyncDisposable.DisposeAsync"/>)될 때 라이브러리가 참조를 <see langword="null"/>로 비웁니다. 컨텍스트에 담긴 민감 데이터(토큰·키 등)의 zeroize는 호출자 책임입니다.</description></item>
    /// </list>
    /// </remarks>
    object? Context { get; set; }

    /// <summary>
    /// 원격 클라이언트로부터 데이터가 수신될 때 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> 네트워크 I/O 스레드 풀에서 직접 호출됩니다.
    /// 콜백 내부에서 동기 블로킹(DB, File I/O, <c>Thread.Sleep</c>)을 절대 수행하지 마십시오.
    /// 블로킹 시 전체 수신 루프가 정지됩니다.
    /// <br/><br/>
    /// <b>[Memory Policy:]</b> 전달되는 <see cref="ReadOnlyMemory{T}"/>는 내부 수신 버퍼의 슬라이스입니다.
    /// 콜백이 반환된 후 해당 메모리 참조는 무효화됩니다.
    /// 콜백 완료 이후에도 데이터를 보관해야 한다면 <c>.ToArray()</c> 또는 별도 버퍼로 복사해야 합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 단일 세그먼트 수신의 경우 Zero-allocation이 보장됩니다.
    /// 분산 세그먼트(Scatter) 수신 시 <see cref="System.Buffers.ArrayPool{T}"/> 임시 버퍼로 병합됩니다.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> 세션 수신 시작 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다. (서버 라이브러리가 수신 시작 전에 배선합니다.)
    /// </remarks>
    Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    /// <summary>
    /// 연결이 종료되었을 때(0바이트 수신, 소켓 오류, 취소) 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> 네트워크 I/O 스레드 풀에서 호출됩니다.
    /// 이 콜백이 반환된 후 세션 내부 리소스가 정리됩니다.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> 세션 생명주기 동안 정확히 1회만 호출됩니다.
    /// <see cref="System.IAsyncDisposable.DisposeAsync"/>가 먼저 호출된 경우에도 발화되지 않을 수 있습니다.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> 세션 수신 시작 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<ValueTask>? OnDisconnected { get; set; }

    /// <summary>
    /// 수신 처리 중 예외가 발생했을 때(손상/악성 패킷 디코드 실패, <see cref="OnReceived"/> 핸들러 예외 등) 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[목적:]</b> 수신 루프는 비-취소 예외가 새어 나가면 미관측 Task 예외로 조용히 죽어 좀비 세션이 됩니다.
    /// 라이브러리는 이런 예외를 패킷 단위로 격리하여 해당 세션만 정상 종료하는데, 이 콜백은 그 종료가
    /// <b>정상/유휴 종료와 구분되도록</b> 원인 예외를 통지합니다. 이 콜백 직후 <see cref="OnDisconnected"/>가 발화하며 세션이 해제됩니다.
    /// <br/><br/>
    /// <b>[Thread Context:]</b> 해당 세션의 수신 I/O 스레드에서 호출됩니다. 동기 블로킹 금지.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> 세션당 최대 1회(에러 종료 시에만) 호출됩니다. 정상 종료(0바이트)·유휴 타임아웃·취소 경로에서는 발화하지 않습니다.
    /// 이 콜백 자체가 throw해도 세션 정리는 계속 진행됩니다(예외는 격리됨).
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 정상 경로(에러 없음)에서는 호출되지 않으므로 Zero-allocation입니다.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> 세션 수신 시작 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<Exception, ValueTask>? OnReceiveError { get; set; }

    /// <summary>
    /// 원격 클라이언트에게 데이터를 비동기로 전송합니다.
    /// </summary>
    /// <param name="data">전송할 데이터 버퍼입니다. 메서드가 반환될 때까지 메모리가 유효해야 합니다.</param>
    /// <param name="cancellationToken">전송 취소 토큰입니다.</param>
    /// <returns>전송이 완료되면 완료되는 <see cref="ValueTask"/>입니다.</returns>
    /// <exception cref="ObjectDisposedException">세션이 이미 해제된 상태에서 호출 시 발생합니다.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">소켓 오류 발생 시 throw됩니다.</exception>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 여러 스레드에서 동시에 호출 가능합니다.
    /// 단, 실제 전송 순서는 OS 소켓 버퍼에 의해 결정됩니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> Zero-allocation. <paramref name="data"/>를 소켓에 직접 기록하며
    /// 중간 복사 버퍼를 생성하지 않습니다.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 소켓 송신 버퍼가 가득 찬 경우 비동기 대기합니다.
    /// </remarks>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
