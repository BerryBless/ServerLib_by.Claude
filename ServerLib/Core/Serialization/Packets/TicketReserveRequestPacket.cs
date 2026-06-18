namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 서버에 티켓 예약을 요청하는 패킷입니다.
/// 본문이 없으며, 패킷 ID 자체가 "예약 요청"을 의미합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> 내부 상태 없음. 동일 인스턴스를 여러 스레드에서 공유해도 안전합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. <see langword="struct"/>이므로 역직렬화 시
/// <c>new T()</c>가 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(0B) = 4B 고정. 본문이 없으므로 <see cref="Serialize"/> /
/// <see cref="Deserialize"/>는 no-op입니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 0B, 내부 상태 없음 — 역직렬화 시 new T()가 무할당.
public struct TicketReserveRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 13;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <inheritdoc/>
    // 본문 없음: 패킷 ID만으로 "예약 요청" 의미 전달
    public int GetBodySize() => 0;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) { /* 본문 없음 */ }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) { /* 본문 없음 */ }
}
