namespace ServerLib.Core.Serialization.Packets;

/// <summary>서버의 test 변수를 1 감소시키는 패킷입니다. 본문 없음.</summary>
public struct DecrementPacket : IPacket
{
    public const ushort Id = 4;
    public ushort PacketId => Id;
    public int GetBodySize() => 0;
    public void Serialize(ref SpanWriter writer) { }
    public void Deserialize(ref SpanReader reader) { }
}
