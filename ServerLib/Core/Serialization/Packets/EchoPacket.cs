using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 에코 서버 연동용 패킷입니다. 문자열 메시지 1개를 포함합니다.
/// </summary>
// class 선택: 가변 길이 string(참조 타입) 필드를 담으므로 어차피 힙 객체가 필요 — struct로 만들어도 string 참조 때문에 무할당이 안 된다.
// 역직렬화 시 new EchoPacket() 1회 + ReadString 1회 할당이 발생(struct 패킷과 달리 본질적으로 Alloc 동반).
public sealed class EchoPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다. RpcDispatcher 등록 시 사용합니다.</summary>
    public const ushort Id = 1;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    private string _message = string.Empty;
    private int _messageBytes = -1; // UTF-8 바이트 수 캐시(-1=미계산). GetBodySize↔Serialize 간 GetByteCount 중복 스캔을 막아 CPU 절감

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
    // ReadString = Alloc: 수신 버퍼(얕은 뷰)에서 새 string을 깊은복사로 생성 — string은 불변이라 zero-copy 불가.
    public void Deserialize(ref SpanReader reader) => Message = reader.ReadString();
}
