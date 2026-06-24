namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 서버에 N개 좌석의 티켓 배치 예약을 요청하는 패킷입니다.
/// <see cref="Rows"/>/<see cref="Cols"/> 쌍 배열로 2D 좌석들을 지정합니다.
/// 서버 내부에서는 각 좌석이 <c>seatId = Rows[i] * InventoryCols + Cols[i]</c>로 평면 인덱스로 변환됩니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> <see cref="Rows"/> / <see cref="Cols"/> 각 <c>byte[Count]</c> 1회 힙 할당.
/// <c>Count == 0</c>(빈 요청)이면 <see cref="Array.Empty{T}"/>가 반환되어 힙 할당 없음.
/// <see langword="struct"/>이므로 역직렬화 시 <c>new T()</c> 자체는 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(<c>1 + Count * 2</c>)B.
/// 본문: <c>[Count(1B)] [Row₀(1B)] [Col₀(1B)] … [Rowₙ₋₁(1B)] [Colₙ₋₁(1B)]</c>
/// Count가 0이면 본문은 <c>[0x00]</c> 1B.
/// </description></item>
/// <item><description>
/// <b>All-or-Nothing 예약:</b> 서버는 요청 좌석 중 하나라도 선점됐거나 범위를 초과하면
/// 이번 요청의 모든 예약을 롤백하고 <see cref="TicketStatus.SeatTaken"/>을 응답합니다.
/// <see cref="TicketStatus.SeatTaken"/> 수신 시 <see cref="SeatMapRequestPacket"/>으로 좌석맵을
/// 재조회하고 빈 좌석을 다시 선택하세요.
/// </description></item>
/// <item><description>
/// <b>상한(Cap):</b> <c>Count</c>는 서버의 <c>Ticket.MaxSeatsPerSession</c> 이하여야 합니다.
/// 초과 시 서버가 <see cref="TicketStatus.SeatTaken"/>으로 거부합니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 역직렬화 시 new T()가 값 타입 생성으로 처리됨. Rows/Cols 참조 필드는 Count에 비례하는 소규모 할당.
public struct TicketReserveRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 13;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>
    /// 예약 요청 좌석 수입니다. 0이면 유효하지 않으며 서버가 거부합니다.
    /// 서버의 <c>Ticket.MaxSeatsPerSession</c> 이하여야 합니다.
    /// </summary>
    public byte Count { get; set; }

    /// <summary>
    /// 예약할 좌석들의 행 인덱스 배열입니다(0-indexed).
    /// <c>length == Count</c>여야 하며 각 원소의 유효 범위: <c>0 ≤ Rows[i] &lt; InventoryRows</c>.
    /// </summary>
    // byte[]: Count에 비례하는 소규모 배열(최대 4*1B = 4B). 수명이 짧고 저빈도 경로이므로 힙 할당 허용.
    public byte[] Rows { get; set; }

    /// <summary>
    /// 예약할 좌석들의 열 인덱스 배열입니다(0-indexed).
    /// <c>length == Count</c>여야 하며 각 원소의 유효 범위: <c>0 ≤ Cols[i] &lt; InventoryCols</c>.
    /// </summary>
    // byte[]: Count에 비례하는 소규모 배열(최대 4*1B = 4B). Rows와 항상 동일 length여야 합니다.
    public byte[] Cols { get; set; }

    /// <summary>
    /// 기본 생성자입니다. <see cref="Rows"/>·<see cref="Cols"/>를 빈 배열로 초기화합니다.
    /// </summary>
    public TicketReserveRequestPacket()
    {
        Rows = Array.Empty<byte>();
        Cols = Array.Empty<byte>();
    }

    /// <summary>
    /// 단일 좌석 예약용 편의 팩토리 메서드입니다.
    /// 하위 호환 및 단일 좌석 경로를 간결하게 표현하기 위해 사용합니다.
    /// </summary>
    /// <param name="row">예약할 좌석의 행 인덱스입니다(0-indexed).</param>
    /// <param name="col">예약할 좌석의 열 인덱스입니다(0-indexed).</param>
    /// <returns>Count=1인 단일 좌석 예약 패킷입니다.</returns>
    public static TicketReserveRequestPacket Single(byte row, byte col)
        => new() { Count = 1, Rows = [row], Cols = [col] };

    /// <inheritdoc/>
    // 본문: [Count(1B)] + [Row,Col 쌍 * Count] = 1 + Count * 2
    public int GetBodySize() => 1 + Count * 2;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteByte(Count);
        int n = Math.Min(Count, Math.Min(Rows.Length, Cols.Length));
        for (int i = 0; i < n; i++)
        {
            writer.WriteByte(Rows[i]);
            writer.WriteByte(Cols[i]);
        }
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Count = reader.ReadByte();
        if (Count == 0)
        {
            Rows = Array.Empty<byte>();
            Cols = Array.Empty<byte>();
            return;
        }
        // 1회 힙 할당: Count * 2 바이트 (소규모·저빈도 경로 허용)
        Rows = new byte[Count];
        Cols = new byte[Count];
        for (int i = 0; i < Count; i++)
        {
            Rows[i] = reader.ReadByte();
            Cols[i] = reader.ReadByte();
        }
    }
}
