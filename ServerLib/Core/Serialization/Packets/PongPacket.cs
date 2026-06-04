namespace ServerLib.Core.Serialization.Packets;

/// <summary>하트비트 PONG 패킷입니다. 서버가 PING의 ticks를 그대로 반사합니다. 예약 ID 0xFFFF.</summary>
/// <remarks>
/// <b>[성능 및 메모리 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Struct 선택:</b> 8바이트 본문의 초고빈도 제어 패킷이므로 역직렬화 시 new T()가 무할당(class였다면 매 퐁 Gen0 압력 발생).</description></item>
/// <item><description><b>Thread Safety:</b> IPacket은 순수 데이터 홀더이며, 직렬화/역직렬화는 호출 스레드에서 수행됩니다.</description></item>
/// </list>
/// </remarks>
public struct PongPacket : IPacket
{
    /// <summary>예약 패킷 ID.</summary>
    public const ushort Id = 0xFFFF;

    /// <summary>PING이 담아 보낸 클라이언트 송신 시각을 그대로 반사한 값입니다.</summary>
    public long ClientTicks;

    public ushort PacketId => Id;
    public int GetBodySize() => 8;
    public void Serialize(ref SpanWriter writer) => writer.WriteInt64(ClientTicks);
    public void Deserialize(ref SpanReader reader) => ClientTicks = reader.ReadInt64();
}
