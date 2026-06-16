using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 게임 서버에 제시하는 세션 토큰 패킷입니다.
/// 인증 서버(AuthServer)에서 발급받은 토큰을 게임 서버에 전달해 인증 게이트를 통과할 때 사용합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서 직렬화/역직렬화해야 합니다.
/// BinaryPacketSerializer는 Thread-safe이지만 패킷 인스턴스 자체는 공유하지 마십시오.</description></item>
/// <item><description><b>Memory Allocation:</b> 역직렬화(Deserialize) 시 Token string 1개 힙 할당 발생.
/// 직렬화(Serialize) 경로는 Zero-allocation.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 직렬화·역직렬화는 CPU 바운드 동기 연산(µs 미만)입니다.</description></item>
/// </list>
/// </remarks>
// sealed class 선택: Token string(참조 타입)을 담으므로 struct여도 힙 할당 발생 — LoginResponsePacket과 동일 패턴.
// new() 제약 필수: BinaryPacketSerializer.Deserialize<T>()가 new AuthTokenPacket()으로 인스턴스 생성.
public sealed class AuthTokenPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 12;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    private string _token = string.Empty;
    // UTF-8 바이트 수 캐시(-1=미계산) — setter 호출 시 무효화. GetBodySize와 Serialize 간 이중 스캔 방지.
    private int _tokenBytes = -1;

    /// <summary>게임 서버에 제시할 세션 토큰(base64url 인코딩)입니다.</summary>
    public string Token
    {
        get => _token;
        set { _token = value; _tokenBytes = -1; }
    }

    private int TokenByteCount => _tokenBytes >= 0
        ? _tokenBytes
        : (_tokenBytes = Encoding.UTF8.GetByteCount(_token));

    /// <inheritdoc/>
    // 바디 레이아웃: [Token 길이(2B ushort) | Token UTF-8]
    public int GetBodySize() => 2 + TokenByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteString(_token, TokenByteCount);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        // ReadString = Alloc: 수신 버퍼 뷰에서 새 string을 힙에 생성. string은 불변이라 zero-copy 불가.
        Token = reader.ReadString();
    }
}
