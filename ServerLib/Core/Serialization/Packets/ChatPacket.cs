using System.Text;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 채팅 메시지 패킷입니다. 송신자 이름과 내용 2개의 문자열을 포함합니다.
/// </summary>
public sealed class ChatPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 2;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>메시지를 보낸 플레이어 이름입니다.</summary>
    public string Sender { get; set; } = string.Empty;

    /// <summary>채팅 내용입니다.</summary>
    public string Content { get; set; } = string.Empty;

    /// <inheritdoc/>
    public int GetBodySize() =>
        2 + Encoding.UTF8.GetByteCount(Sender) +
        2 + Encoding.UTF8.GetByteCount(Content);

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteString(Sender);
        writer.WriteString(Content);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Sender = reader.ReadString();
        Content = reader.ReadString();
    }
}
