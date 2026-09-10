namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 서버의 공유 카운터 현재 값을 조회하기 위해 보내는 요청 패킷입니다. 본문 없음(0바이트).
/// 서버는 이 요청에 <see cref="CounterValuePacket"/>(Id=19)으로 응답합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> 필드가 없는 순수 마커 패킷이므로 상태 경합이 존재하지 않습니다(Thread-safe).
/// 동일 인스턴스를 여러 스레드가 동시에 직렬화해도 안전합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. struct이므로 <c>Deserialize&lt;CounterQueryPacket&gt;</c>의
/// <c>new T()</c>가 스택/인라인 생성되어 힙 할당이 발생하지 않습니다(class였다면 조회마다 Gen0 압력).
/// 본문이 0바이트라 와이어 크기는 헤더 4바이트(<c>PacketPool.HeaderSize</c>)뿐입니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 직렬화·역직렬화 모두 즉시 반환합니다.
/// </description></item>
/// <item><description>
/// <b>완료 확인 용도:</b> 서버 세션 수신 루프가 패킷별 콜백을 <b>순차</b> await하므로
/// (<c>SocketPipelineSession.DispatchPacketAsync</c>), 이 조회에 대한 응답이 도착했다는 것은
/// <b>같은 연결에서 앞서 보낸 모든 증감 명령이 이미 적용되었다</b>는 뜻입니다.
/// 다른 연결의 처리 완료는 보장하지 않으므로, 전역 최종값 검증은 모든 연결의 확인 응답을 받은
/// 뒤(=배리어 이후)에 다시 조회해야 합니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 0B의 고빈도 제어 패킷 — 역직렬화 시 힙 할당 0. class면 조회 1회마다 Gen0 객체가 생겨
// 경합 시연처럼 수천 회 왕복하는 예제에서 불필요한 GC 압력이 누적된다.
public struct CounterQueryPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 18;

    /// <summary>본문 바이트 수 상수입니다. 요청에 필드가 없으므로 0입니다.</summary>
    public const int BodySize = 0;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <inheritdoc/>
    // 본문 없음 — 조회 대상이 서버당 카운터 1개로 고정이라 식별 필드가 필요 없다.
    // 연결당 미완료 조회를 1개로 제한하므로 요청 ID도 두지 않는다(응답 매칭이 연결 단위로 자명).
    public int GetBodySize() => BodySize;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) { }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) { }
}
