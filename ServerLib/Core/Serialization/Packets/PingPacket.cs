namespace ServerLib.Core.Serialization.Packets;

/// <summary>하트비트 PING 패킷입니다. 클라이언트가 송신 시각(ticks)을 담아 보냅니다. 예약 ID 0xFFFE.</summary>
/// <remarks>
/// <b>[성능 및 메모리 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Struct 선택:</b> 8바이트 본문의 초고빈도 제어 패킷이므로 역직렬화 시 new T()가 무할당(class였다면 매 핑 Gen0 압력 발생).</description></item>
/// <item><description><b>Thread Safety:</b> IPacket은 순수 데이터 홀더이며, 직렬화/역직렬화는 호출 스레드에서 수행됩니다.</description></item>
/// </list>
/// </remarks>
public struct PingPacket : IPacket
{
    /// <summary>예약 패킷 ID. 앱 패킷과 충돌하지 않도록 상위 영역을 사용합니다.</summary>
    public const ushort Id = 0xFFFE;

    /// <summary>클라이언트가 PING을 송신한 시각(DateTimeOffset.UtcNow.UtcTicks).</summary>
    public long ClientTicks;

    public ushort PacketId => Id;
    public int GetBodySize() => 8;
    public void Serialize(ref SpanWriter writer) => writer.WriteInt64(ClientTicks);
    public void Deserialize(ref SpanReader reader) => ClientTicks = reader.ReadInt64();
}
