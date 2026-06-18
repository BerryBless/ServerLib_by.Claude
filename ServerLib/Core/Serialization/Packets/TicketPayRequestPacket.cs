namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 서버에 티켓 결제를 요청하는 패킷입니다.
/// <see cref="SimulateFailure"/> 플래그로 결제 실패를 결정론적으로 시연할 수 있습니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> 내부 상태는 단순 bool 1개. 단일 스레드에서만 사용해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. <see langword="struct"/>이므로 역직렬화 시
/// <c>new T()</c>가 스택/인라인 생성됩니다.
/// </description></item>
/// <item><description>
/// <b>Wire Format:</b> 헤더(4B) + 본문(1B) = 5B 고정.
/// 본문: <c>[SimulateFailure(1B bool)]</c>
/// </description></item>
/// <item><description>
/// <b>async 메모리 안전:</b> 서버 핸들러에서 이 패킷을 수신 후 결제 API를 <see langword="await"/>하기 전에
/// 반드시 <c>bool sim = pay.SimulateFailure</c>를 스택 변수로 복사해야 합니다.
/// <c>data.Span</c>은 <see langword="await"/> 이후 Pipe 내부 버퍼가 재사용되면 무효화됩니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 1B 고정 — 역직렬화 시 new T()가 무할당.
public struct TicketPayRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 14;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>
    /// <see langword="true"/>이면 서버 결제 게이트웨이가 반드시 실패를 반환합니다.
    /// 데모에서 <c>PaymentFailed → 슬롯 반납 → 재예약 → 재결제</c> 흐름을 결정론적으로 시연합니다.
    /// </summary>
    public bool SimulateFailure { get; set; }

    /// <inheritdoc/>
    // 본문: bool SimulateFailure (1B)
    public int GetBodySize() => 1;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) => writer.WriteBool(SimulateFailure);

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) => SimulateFailure = reader.ReadBool();
}
