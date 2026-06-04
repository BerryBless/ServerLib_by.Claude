namespace ServerLib.Core.Serialization.Packets;

/// <summary>서버의 test 변수를 1 증가시키는 패킷입니다. 본문 없음.</summary>
// struct 선택: 본문 없는 초고빈도 패킷이므로 역직렬화 시 new T()가 스택/인라인 생성되어 힙 할당 0 (class였다면 매 패킷 Gen0 압력).
public struct IncrementPacket : IPacket
{
    public const ushort Id = 3;
    public ushort PacketId => Id;
    public int GetBodySize() => 0;
    public void Serialize(ref SpanWriter writer) { }
    public void Deserialize(ref SpanReader reader) { }
}
