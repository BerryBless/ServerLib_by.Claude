namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 서버에 특정 좌석의 티켓 예약을 요청하는 패킷입니다.
/// <see cref="Row"/>/<see cref="Col"/> 필드로 2D 좌석을 지정합니다.
/// 서버 내부에서는 <c>seatId = Row * Cols + Col</c>로 평면 인덱스로 변환됩니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> 내부 상태가 byte 2개. 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. <see langword="struct"/>이므로 역직렬화 시
/// <c>new T()</c>가 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(2B) = 6B 고정.
/// 본문: <c>[Row(1B)] [Col(1B)]</c>
/// </description></item>
/// <item><description>
/// <b>Seat Map 선행 조회 권장:</b> 예약 전에 <see cref="SeatMapRequestPacket"/>으로 현재 좌석 상태를
/// 조회하여 Free 좌석만 지정하면 <see cref="TicketStatus.SeatTaken"/> 응답을 최소화할 수 있습니다.
/// SeatTaken 수신 시 좌석맵을 재조회하고 다른 빈 좌석을 선택하세요.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 2B 고정 — 역직렬화 시 new T()가 무할당.
public struct TicketReserveRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 13;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>예약할 좌석의 행(0-indexed)입니다.</summary>
    public byte Row { get; set; }

    /// <summary>예약할 좌석의 열(0-indexed)입니다.</summary>
    public byte Col { get; set; }

    /// <inheritdoc/>
    // 본문: [Row(1B)] [Col(1B)]
    public int GetBodySize() => 2;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteByte(Row);
        writer.WriteByte(Col);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Row = reader.ReadByte();
        Col = reader.ReadByte();
    }
}
