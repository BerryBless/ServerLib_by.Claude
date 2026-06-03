using System.Text;
using ServerLib.Core.Serialization;

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

    private string _message = string.Empty;
    private int _messageBytes = -1;

    /// <summary>에코할 문자열 메시지입니다.</summary>
    public string Message
    {
        get => _message;
        set { _message = value; _messageBytes = -1; }
    }

    // GetBodySize와 Serialize 간 UTF-8 스캔 중복 방지 (2회 → 1회)
    private int MessageByteCount => _messageBytes >= 0
        ? _messageBytes
        : (_messageBytes = Encoding.UTF8.GetByteCount(_message));

    /// <inheritdoc/>
    public int GetBodySize() => 2 + MessageByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) => writer.WriteString(_message, MessageByteCount);

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) => Message = reader.ReadString();
}
