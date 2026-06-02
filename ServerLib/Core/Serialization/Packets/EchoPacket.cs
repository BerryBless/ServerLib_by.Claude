using System.Text;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 에코 서버 연동용 패킷입니다. 문자열 메시지 1개를 포함합니다.
/// </summary>
public sealed class EchoPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다. RpcDispatcher 등록 시 사용합니다.</summary>
    public const ushort Id = 1;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>에코할 문자열 메시지입니다.</summary>
    public string Message { get; set; } = string.Empty;

    /// <inheritdoc/>
    public int GetBodySize() => 2 + Encoding.UTF8.GetByteCount(Message);

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) => writer.WriteString(Message);

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) => Message = reader.ReadString();
}
