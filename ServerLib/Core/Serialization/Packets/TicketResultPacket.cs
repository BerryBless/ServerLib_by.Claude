namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 서버가 클라이언트에게 티켓 예약·결제 결과를 전달하는 패킷입니다.
/// <see cref="TicketReserveRequestPacket"/> 및 <see cref="TicketPayRequestPacket"/> 양쪽의 응답으로 공용됩니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> 내부 상태는 3바이트(byte 3개). 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. <see langword="struct"/>이므로 역직렬화 시
/// <c>new T()</c>가 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(3B) = 7B 고정.
/// 본문: <c>[Status(1B)] [Slot(1B)] [Remaining(1B)]</c>
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 3B 고정 — 역직렬화 시 new T()가 무할당. 예약·결제 모두 서버→클라 저빈도 경로이므로 무할당 효과 충분.
public struct TicketResultPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 15;

    /// <summary>슬롯 인덱스가 없음을 나타내는 상수입니다 (SoldOut·NotReserved 시).</summary>
    public const byte NoSlot = 0xFF;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>티켓 처리 결과 상태 코드입니다.</summary>
    public TicketStatus Status { get; set; }

    /// <summary>
    /// 예약·확정된 슬롯 인덱스(0~N-1)입니다.
    /// 슬롯이 없는 경우(<see cref="TicketStatus.SoldOut"/> 등)에는 <see cref="NoSlot"/>(<c>0xFF</c>)입니다.
    /// </summary>
    public byte Slot { get; set; }

    /// <summary>
    /// 결과 시점의 잔여 Free 슬롯 수(스냅샷, 참고용)입니다.
    /// 읽기 직후 변동될 수 있으며, 동시성 보장이 없습니다.
    /// </summary>
    public byte Remaining { get; set; }

    /// <inheritdoc/>
    // 본문: [Status(1B)] [Slot(1B)] [Remaining(1B)]
    public int GetBodySize() => 3;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteByte((byte)Status);
        writer.WriteByte(Slot);
        writer.WriteByte(Remaining);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Status    = (TicketStatus)reader.ReadByte();
        Slot      = reader.ReadByte();
        Remaining = reader.ReadByte();
    }
}
