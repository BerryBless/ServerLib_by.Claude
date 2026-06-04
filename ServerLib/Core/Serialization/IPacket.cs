namespace ServerLib.Core.Serialization;

/// <summary>
/// 바이너리 직렬화 가능한 네트워크 패킷의 계약을 정의하는 인터페이스입니다.
/// 구현체는 헤더를 제외한 본문(body)만 직렬화·역직렬화합니다.
/// 헤더(PacketId + BodyLength) 기록은 <see cref="BinaryPacketSerializer"/>가 담당합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 구현체에 따라 다릅니다. 단일 세션 전용 패킷 객체는 Not Thread-safe.
/// <b>[Memory Allocation:]</b> 구현체가 ref struct 기반 <see cref="SpanWriter"/>/<see cref="SpanReader"/>를
/// 사용하므로 직렬화 자체는 Zero-allocation입니다. 문자열 필드의 <c>ReadString</c>은 예외입니다.
/// </remarks>
// 구현체는 struct(무할당) 또는 class(역직렬화 시 힙 1회) 중 선택 — 빈번한 소형 패킷은 struct, 가변 문자열 패킷은 class가 보통 유리.
// Serialize/Deserialize가 ref SpanWriter/SpanReader를 받는 이유: 이들은 ref struct라 값으로 복사하면 내부 _position 진행이
// 호출자에 반영되지 않는다. 저장 위치(lvalue) 자체를 ref로 넘겨야 누적 쓰기/읽기 위치가 공유된다.
public interface IPacket
{
    /// <summary>
    /// 패킷 식별자입니다. <c>RpcDispatcher</c> 라우팅 키로 사용되며
    /// 전체 패킷 헤더의 첫 2바이트에 기록됩니다.
    /// </summary>
    ushort PacketId { get; }

    /// <summary>
    /// 패킷 본문 필드를 <paramref name="writer"/>에 순서대로 기록합니다.
    /// 헤더는 포함하지 않습니다.
    /// </summary>
    /// <param name="writer">기록 대상 SpanWriter입니다.</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Not Thread-safe.
    /// <b>[Memory Allocation:]</b> 문자열 필드 외 Zero-allocation.
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    void Serialize(ref SpanWriter writer);

    /// <summary>
    /// <paramref name="reader"/>에서 본문 필드를 순서대로 읽어 인스턴스를 채웁니다.
    /// <see cref="Serialize"/>와 동일한 필드 순서를 유지해야 합니다.
    /// </summary>
    /// <param name="reader">읽기 대상 SpanReader입니다.</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Not Thread-safe.
    /// <b>[Memory Allocation:]</b> 문자열 필드 외 Zero-allocation.
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    void Deserialize(ref SpanReader reader);

    /// <summary>
    /// 직렬화에 필요한 본문(body) 바이트 수를 반환합니다.
    /// 헤더 크기(<see cref="Memory.PacketPool.HeaderSize"/>)는 포함하지 않습니다.
    /// 버퍼를 미리 대여할 때 사용합니다.
    /// </summary>
    /// <returns>본문 직렬화에 필요한 최소 바이트 수입니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation.
    /// 문자열 필드는 <c>Encoding.UTF8.GetByteCount(value)</c>로 정확한 크기를 계산해야 합니다.
    /// </remarks>
    int GetBodySize();
}
