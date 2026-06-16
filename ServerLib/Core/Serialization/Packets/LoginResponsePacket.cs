using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 로그인 응답 패킷입니다. 성공 여부와 세션 토큰을 포함합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서 직렬화/역직렬화해야 합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 역직렬화(Deserialize) 시 Token string 1개 힙 할당 발생.
/// 직렬화(Serialize) 경로는 Zero-allocation.</description></item>
/// <item><description><b>Token:</b> 로그인 실패 시 Token은 빈 문자열입니다. Success가 false이면 Token을 사용하지 마세요.</description></item>
/// </list>
/// </remarks>
// sealed class 선택: Token string(참조 타입)을 담으므로 struct여도 힙 할당 발생 — class로 선택.
// new() 제약 필수: BinaryPacketSerializer.Deserialize<T>()가 new LoginResponsePacket()으로 인스턴스 생성.
public sealed class LoginResponsePacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 11;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    private string _token = string.Empty;
    // UTF-8 바이트 수 캐시(-1=미계산) — setter 호출 시 무효화. GetBodySize와 Serialize 간 이중 스캔 방지.
    private int _tokenBytes = -1;

    /// <summary>로그인 성공 여부입니다.</summary>
    public bool Success { get; set; }

    /// <summary>발급된 세션 토큰입니다. 실패 시 빈 문자열입니다.</summary>
    public string Token
    {
        get => _token;
        set { _token = value; _tokenBytes = -1; }
    }

    private int TokenByteCount => _tokenBytes >= 0
        ? _tokenBytes
        : (_tokenBytes = Encoding.UTF8.GetByteCount(_token));

    /// <inheritdoc/>
    // 바디 레이아웃: [Success(1B bool)] [Token 길이(2B ushort) | Token UTF-8]
    public int GetBodySize() => 1 + 2 + TokenByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteBool(Success);
        writer.WriteString(_token, TokenByteCount);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Success = reader.ReadBool();
        // ReadString = Alloc: Token string을 힙에 생성. 실패 응답은 빈 문자열이므로 할당 최소.
        Token = reader.ReadString();
    }
}
