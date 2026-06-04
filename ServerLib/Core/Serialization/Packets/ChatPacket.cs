using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 채팅 메시지 패킷입니다. 송신자 이름과 내용 2개의 문자열을 포함합니다.
/// </summary>
// class 선택: 가변 길이 string 2개(참조 타입)를 담으므로 struct여도 무할당이 불가 — 어차피 힙 객체가 필요하다.
// 역직렬화 시 new ChatPacket() 1회 + ReadString 2회 할당이 동반된다(struct 패킷과 본질적으로 다른 메모리 특성).
public sealed class ChatPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 2;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    private string _sender = string.Empty;
    private string _content = string.Empty;
    private int _senderBytes = -1;  // UTF-8 바이트 수 캐시(-1=미계산) — setter에서 무효화. GetBodySize↔Serialize 이중 스캔 방지
    private int _contentBytes = -1;

    /// <summary>메시지를 보낸 플레이어 이름입니다.</summary>
    public string Sender
    {
        get => _sender;
        set { _sender = value; _senderBytes = -1; }
    }

    /// <summary>채팅 내용입니다.</summary>
    public string Content
    {
        get => _content;
        set { _content = value; _contentBytes = -1; }
    }

    // GetBodySize와 Serialize 간 UTF-8 스캔 중복 방지 (4회 → 2회)
    private int SenderByteCount => _senderBytes >= 0
        ? _senderBytes
        : (_senderBytes = Encoding.UTF8.GetByteCount(_sender));

    private int ContentByteCount => _contentBytes >= 0
        ? _contentBytes
        : (_contentBytes = Encoding.UTF8.GetByteCount(_content));

    /// <inheritdoc/>
    public int GetBodySize() => 2 + SenderByteCount + 2 + ContentByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteString(_sender, SenderByteCount);
        writer.WriteString(_content, ContentByteCount);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        // ReadString = Alloc: 수신 버퍼(얕은 뷰)에서 새 string 2개를 깊은복사로 생성 — string은 불변이라 zero-copy 불가.
        Sender = reader.ReadString();
        Content = reader.ReadString();
    }
}
