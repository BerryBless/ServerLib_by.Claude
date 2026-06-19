namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 서버에 티켓 결제를 요청하는 패킷입니다.
/// 페이로드 없이 패킷 ID만 전달하며, 서버는 세션 컨텍스트(예약 슬롯·사용자명)로 결제를 처리합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. <see langword="struct"/>이므로 역직렬화 시
/// <c>new T()</c>가 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(0B) = 4B 고정.
/// 결제 실패 시뮬레이션은 서버 <c>appsettings.json</c>의 <c>Ticket.FailingUsername</c>으로 제어합니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 0B — 역직렬화 시 new T()가 무할당.
public struct TicketPayRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 14;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <inheritdoc/>
    // 결제 요청은 세션 컨텍스트(예약 슬롯·사용자명)만으로 처리 — 추가 페이로드 없음
    public int GetBodySize() => 0;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) { }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) { }
}
