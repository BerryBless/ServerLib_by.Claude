namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 서버가 클라이언트에게 티켓 예약·결제 결과를 전달하는 패킷입니다.
/// <see cref="TicketReserveRequestPacket"/> 및 <see cref="TicketPayRequestPacket"/> 양쪽의 응답으로 공용됩니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> <see cref="Slots"/> 배열(<c>Count</c> 바이트) 1회 힙 할당.
/// <c>Count == 0</c>(실패 응답)이면 <see cref="Array.Empty{T}"/>가 반환되어 힙 할당 없음.
/// <see langword="struct"/>이므로 역직렬화 시 <c>new T()</c> 자체는 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(<c>3 + Count</c>)B.
/// 본문: <c>[Status(1B)] [Count(1B)] [Slot₀(1B)] … [Slotₙ₋₁(1B)] [Remaining(1B)]</c>
/// 실패 시 <c>Count == 0</c>이며 본문은 3B.
/// </description></item>
/// <item><description>
/// <b>All-or-Nothing 의미론:</b>
/// <list type="bullet">
/// <item><description>예약 성공: <see cref="TicketStatus.Reserved"/>, Count = 요청 수, Slots = 배정된 seatId 목록.</description></item>
/// <item><description>예약 실패: <see cref="TicketStatus.SeatTaken"/>·<see cref="TicketStatus.AlreadyReserved"/>·<see cref="TicketStatus.SoldOut"/>, Count = 0, Slots 빈 배열.</description></item>
/// <item><description>결제 성공: <see cref="TicketStatus.Confirmed"/>, Count = 확정 수, Slots = 확정된 seatId 목록.</description></item>
/// <item><description>결제 실패: <see cref="TicketStatus.PaymentFailed"/>, Count = 해제 수, Slots = 반납된 seatId 목록.</description></item>
/// </list>
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 역직렬화 시 new T()가 값 타입 생성으로 처리됨. Slots 참조 필드는 Count에 비례하는 소규모 할당.
public struct TicketResultPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 15;

    /// <summary>
    /// 슬롯 인덱스가 없음을 나타내는 레거시 상수입니다 (단일 좌석 경로 호환용).
    /// 배치 모드에서는 <see cref="Count"/> == 0으로 실패를 표현합니다.
    /// </summary>
    public const byte NoSlot = 0xFF;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>티켓 처리 결과 상태 코드입니다.</summary>
    public TicketStatus Status { get; set; }

    /// <summary>
    /// 이번 응답에 포함된 좌석 수입니다.
    /// 성공 시 예약/확정/해제된 좌석 수, 실패 시 0입니다.
    /// </summary>
    public byte Count { get; set; }

    /// <summary>
    /// 처리된 좌석의 평면 인덱스(seatId) 배열입니다.
    /// <c>length == Count</c>이며, <c>Count == 0</c>이면 <see cref="Array.Empty{T}"/>입니다.
    /// <c>seatId = row * InventoryCols + col</c>로 계산됩니다.
    /// </summary>
    // byte[]: Count에 비례하는 소규모 배열(최대 4*1B = 4B). 수명이 짧고 저빈도 응답 경로이므로 힙 할당 허용.
    public byte[] Slots { get; set; }

    /// <summary>
    /// 결과 시점의 잔여 Free 슬롯 수(스냅샷, 참고용)입니다.
    /// 읽기 직후 변동될 수 있으며, 동시성 보장이 없습니다.
    /// </summary>
    public byte Remaining { get; set; }

    /// <summary>
    /// 기본 생성자입니다. <see cref="Slots"/>를 빈 배열로 초기화합니다.
    /// </summary>
    public TicketResultPacket() { Slots = Array.Empty<byte>(); }

    /// <inheritdoc/>
    // 본문: [Status(1B)] [Count(1B)] [Slots...] [Remaining(1B)] = 3 + Count
    public int GetBodySize() => 3 + Count;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteByte((byte)Status);
        writer.WriteByte(Count);
        if (Count > 0 && Slots.Length >= Count)
            writer.WriteBytes(Slots.AsSpan(0, Count));
        writer.WriteByte(Remaining);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Status    = (TicketStatus)reader.ReadByte();
        Count     = reader.ReadByte();
        // Count > 0이면 힙 할당 1회(소규모·저빈도 허용), 0이면 빈 배열로 zero-allocation
        Slots     = Count > 0 ? reader.ReadBytes(Count).ToArray() : Array.Empty<byte>();
        Remaining = reader.ReadByte();
    }
}
