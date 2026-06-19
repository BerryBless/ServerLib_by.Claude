namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 서버가 클라이언트에게 좌석 배치도(행·열 크기 + 좌석별 상태 배열)를 전달하는 패킷입니다.
/// <see cref="SeatMapRequestPacket"/> 요청에 대한 응답이며, 클라이언트가 예약할 빈 좌석을 선택하는 데 사용됩니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation (수신/역직렬화):</b> <see cref="States"/> 배열 생성으로 1회 힙 할당(<c>Rows*Cols</c>바이트).
/// 전형적으로 2~255B 규모의 소규모 할당이며, 저빈도 응답 경로이므로 허용됩니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation (서버 송신):</b> 서버 측에서 <c>stackalloc byte[total]</c>으로 상태 버퍼를 채운 뒤
/// <c>states.ToArray()</c>로 <see cref="States"/>를 할당합니다. 소규모·저빈도 경로이므로 허용됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(2 + Rows*Cols)B.
/// 본문: <c>[Rows(1B)] [Cols(1B)] [States[0]…States[Rows*Cols-1](각 1B)]</c>
/// States 값: <c>0=Free, 1=Reserved, 2=Sold</c>
/// </description></item>
/// <item><description>
/// <b>스냅샷 주의:</b> <see cref="States"/>는 서버가 응답 시점에 촬영한 순간 스냅샷입니다.
/// 네트워크 왕복 중 다른 클라이언트가 좌석을 선점할 수 있으므로, 클라이언트는
/// <see cref="TicketStatus.SeatTaken"/> 수신 시 좌석맵을 재조회하고 빈 좌석을 재선택해야 합니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 역직렬화 경로에서 new T()가 값 타입 생성으로 처리됨. States 참조 필드는 별도 할당이지만 소규모.
public struct SeatMapResponsePacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 17;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>좌석 행 수입니다.</summary>
    public byte Rows { get; set; }

    /// <summary>좌석 열 수입니다. 전체 좌석 수 = <see cref="Rows"/> × <see cref="Cols"/>.</summary>
    public byte Cols { get; set; }

    /// <summary>
    /// 좌석별 상태 배열입니다. 평면 순서(<c>seatId = row * Cols + col</c>)로 저장됩니다.
    /// <list type="bullet">
    /// <item><description>0 = Free — 예약 가능</description></item>
    /// <item><description>1 = Reserved — 예약됨(TTL 만료 전 결제 대기 중)</description></item>
    /// <item><description>2 = Sold — 결제 확정</description></item>
    /// </list>
    /// 길이는 <see cref="Rows"/> × <see cref="Cols"/>와 같아야 합니다.
    /// 역직렬화 전 기본값은 <see cref="Array.Empty{T}"/>이며 <see langword="null"/>이 될 수 없습니다.
    /// </summary>
    // byte[]: 좌석 상태 스냅샷 — Rows*Cols 소규모 배열(최대 255B). 참조 타입이지만 수명이 짧고 소규모임.
    public byte[] States { get; set; }

    /// <summary>기본 생성자. <see cref="States"/>를 빈 배열로 초기화합니다.</summary>
    public SeatMapResponsePacket() { States = Array.Empty<byte>(); }

    /// <inheritdoc/>
    // 본문: [Rows(1B)] [Cols(1B)] + States[Rows*Cols]
    public int GetBodySize() => 2 + Rows * Cols;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteByte(Rows);
        writer.WriteByte(Cols);
        // States는 서버 측에서 SnapshotStates로 Rows*Cols 바이트를 정확히 채운 후 전달해야 합니다.
        if (States.Length >= Rows * Cols)
            writer.WriteBytes(States.AsSpan(0, Rows * Cols));
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Rows = reader.ReadByte();
        Cols = reader.ReadByte();
        // 와이어 공급 길이 상한 검증: TicketInventory 생성자의 255 제약과 동일한 불변식
        if ((int)Rows * Cols > byte.MaxValue)
            throw new InvalidDataException(
                $"SeatMapResponsePacket: Rows*Cols={Rows * Cols}가 {byte.MaxValue}를 초과합니다.");
        // States: Rows*Cols 바이트 읽기 — 1회 힙 할당(소규모·저빈도 허용)
        States = reader.ReadBytes(Rows * Cols).ToArray();
    }
}
