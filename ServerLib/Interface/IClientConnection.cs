namespace ServerLib.Interface;

/// <summary>
/// 원격 서버에 대한 단일 TCP 연결을 나타내는 클라이언트 연결 인터페이스입니다.
/// 연결 수립 → 데이터 송수신 → 연결 종료의 생명주기를 정의하며,
/// System.IO.Pipelines 기반 Zero-copy 수신을 계약으로 강제합니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description>수신 루프는 내부적으로 <c>PipeReader</c>를 사용하여 Zero-copy 데이터 전달을 보장합니다.</description></item>
/// <item><description>모든 콜백은 <see cref="ValueTask"/>를 반환하여 hot path 힙 할당을 제거합니다.</description></item>
/// <item><description><see cref="IAsyncDisposable"/>을 구현하므로 <c>await using</c> 패턴으로 사용하는 것을 권장합니다.</description></item>
/// </list>
/// </remarks>
public interface IClientConnection : IAsyncDisposable
{
    /// <summary>
    /// 현재 서버와 연결된 상태인지 나타냅니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 내부 소켓 상태를 반영하는 스냅샷 값입니다.
    /// <c>true</c>를 확인한 직후 연결이 끊길 수 있으므로 방어적으로 처리해야 합니다.
    /// </remarks>
    bool IsConnected { get; }

    /// <summary>
    /// 자동 하트비트 PING 송신 주기입니다. <see langword="null"/>이면 하트비트를 비활성화합니다(기본값).
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="ConnectAsync"/> 호출 전에 설정해야 합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan? PingInterval { get; set; }

    /// <summary>
    /// 단일 송신(<see cref="SendAsync"/>) 작업의 타임아웃입니다. <see langword="null"/>(기본값)이면 송신을 무한 대기합니다.
    /// </summary>
    /// <remarks>
    /// <b>[목적:]</b> 응답 불능 서버로의 송신이 소켓 버퍼 포화 상태에서 영구 블로킹되는 것을 방지합니다.
    /// 시한 초과 시 해당 송신은 <see cref="System.Net.Sockets.SocketException"/>(<see cref="System.Net.Sockets.SocketError.TimedOut"/>)으로 실패합니다.
    /// <br/><br/>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="ConnectAsync"/> 호출 전에 설정하는 것을 권장합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 활성화 시 송신 경로에서 취소 타이머 관리를 위한 내부 구조를 재사용합니다(송신당 무할당 지향).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan? SendTimeout { get; set; }

    /// <summary>
    /// 마지막으로 측정된 왕복 지연(RTT)입니다. 측정 전에는 <see cref="TimeSpan.Zero"/>입니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile read로 최신값을 반환합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation (값 타입).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. PONG 수신 시마다 갱신됩니다.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan Rtt { get; }

    /// <summary>
    /// 서버 연결이 성공적으로 수립된 직후 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> <see cref="ConnectAsync"/> 호출 스레드의 연속(continuation)에서 실행됩니다.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> <see cref="OnDisconnected"/>보다 항상 먼저 발화됩니다.
    /// 연결 실패 시에는 발화되지 않습니다.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="ConnectAsync"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<ValueTask>? OnConnected { get; set; }

    /// <summary>
    /// 서버 연결이 종료되었을 때(0바이트 수신, 소켓 오류, 취소, Dispose) 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> 수신 루프를 구동하는 I/O 스레드 풀에서 호출됩니다.
    /// <br/><br/>
    /// <b>[Guarantee:]</b> 연결 수립 후 반드시 1회 호출됩니다.
    /// <see cref="System.IAsyncDisposable.DisposeAsync"/>가 먼저 호출된 경우에도 발화될 수 있습니다.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="ConnectAsync"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<ValueTask>? OnDisconnected { get; set; }

    /// <summary>
    /// 서버로부터 데이터가 수신될 때 호출되는 콜백입니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Context:]</b> I/O 스레드 풀의 수신 루프에서 직접 호출됩니다.
    /// 콜백 내부에서 동기 블로킹 작업을 수행하면 수신이 중단됩니다.
    /// <br/><br/>
    /// <b>[Memory Policy:]</b> 전달되는 <see cref="ReadOnlyMemory{T}"/>는 내부 수신 파이프 버퍼의 슬라이스입니다.
    /// 콜백 반환 후 해당 메모리 참조는 무효화됩니다.
    /// 콜백 완료 이후에도 데이터를 보관해야 한다면 반드시 복사해야 합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 단일 세그먼트 수신 시 Zero-allocation 보장.
    /// 분산 세그먼트 수신 시 <see cref="System.Buffers.ArrayPool{T}"/> 임시 버퍼 1회 할당.
    /// <br/><br/>
    /// <b>[설정 시점:]</b> <see cref="ConnectAsync"/>() 호출 전에만 설정 가능하며, 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.
    /// </remarks>
    Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    /// <summary>
    /// 지정된 호스트와 포트로 비동기 TCP 연결을 수립합니다.
    /// 연결 성공 시 내부 수신 루프가 자동으로 시작됩니다.
    /// </summary>
    /// <param name="host">연결할 서버의 호스트명 또는 IP 주소입니다.</param>
    /// <param name="port">연결할 서버의 TCP 포트 번호입니다.</param>
    /// <param name="cancellationToken">연결 시도 취소 토큰입니다.</param>
    /// <returns>연결이 수립되고 <see cref="OnConnected"/> 콜백 호출이 완료되면 완료되는 <see cref="Task"/>입니다.</returns>
    /// <exception cref="InvalidOperationException">이미 연결된 상태에서 호출 시 발생합니다.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">연결 실패(호스트 미존재, 포트 거부 등) 시 발생합니다.</exception>
    /// <exception cref="ObjectDisposedException">이미 Dispose된 상태에서 호출 시 발생합니다.</exception>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. TCP 핸드셰이크 완료 시까지 비동기 대기합니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서만 호출해야 합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> 소켓 및 파이프 내부 구조 생성으로 일부 힙 할당이 발생합니다(1회).
    /// </remarks>
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// 서버에 데이터를 비동기로 전송합니다.
    /// </summary>
    /// <param name="data">전송할 데이터 버퍼입니다. 메서드가 반환될 때까지 메모리가 유효해야 합니다.</param>
    /// <param name="cancellationToken">전송 취소 토큰입니다.</param>
    /// <returns>전송이 완료되면 완료되는 <see cref="ValueTask"/>입니다.</returns>
    /// <exception cref="InvalidOperationException">연결되지 않은 상태에서 호출 시 발생합니다.</exception>
    /// <exception cref="ObjectDisposedException">Dispose된 상태에서 호출 시 발생합니다.</exception>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. <paramref name="data"/>를 소켓에 직접 기록합니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 소켓 버퍼가 가득 차면 비동기 대기합니다.
    /// </remarks>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결을 즉시 종료하고 수신 루프를 중단합니다.
    /// <see cref="System.IAsyncDisposable.DisposeAsync"/>와 달리 동기로 동작하며, 진행 중인 I/O를 강제 취소합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. 취소 신호를 전송하고 즉시 반환됩니다.
    /// <br/><br/>
    /// <b>[Thread Safety:]</b> Thread-safe. 어느 스레드에서도 호출 가능합니다.
    /// <br/><br/>
    /// <b>[Side Effect:]</b> 호출 후 <see cref="OnDisconnected"/> 콜백이 I/O 스레드에서 비동기 발화될 수 있습니다.
    /// </remarks>
    void Disconnect();
}
