using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 로그인 요청 패킷입니다. 사용자 이름과 비밀번호 2개의 문자열을 포함합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 단일 스레드에서 직렬화/역직렬화해야 합니다.
/// BinaryPacketSerializer는 스레드 안전하지만 패킷 인스턴스 자체는 공유하지 마십시오.</description></item>
/// <item><description><b>Memory Allocation:</b> 역직렬화(Deserialize) 시 string 2개(Username, Password)
/// 힙 할당이 발생합니다. 직렬화(Serialize) 경로는 Zero-allocation.</description></item>
/// <item><description><b>보안:</b> Password 필드는 평문 전송됩니다. 운영 환경에서는 TLS를 반드시 사용하세요.</description></item>
/// </list>
/// </remarks>
// sealed class 선택: string 2개(참조 타입)를 담으므로 struct로도 힙 할당이 불가피 — ChatPacket과 동일 패턴.
// new() 제약 필수: BinaryPacketSerializer.Deserialize<T>()가 new LoginRequestPacket()으로 인스턴스 생성 후 Deserialize 호출.
public sealed class LoginRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 10;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    private string _username = string.Empty;
    private string _password = string.Empty;
    // UTF-8 바이트 수 캐시(-1=미계산) — setter 호출 시 무효화. GetBodySize와 Serialize 간 이중 스캔 방지.
    private int _usernameBytes = -1;
    private int _passwordBytes = -1;

    /// <summary>사용자 이름입니다.</summary>
    public string Username
    {
        get => _username;
        set { _username = value; _usernameBytes = -1; }
    }

    /// <summary>비밀번호(평문)입니다. TLS 없이 전송되지 않도록 주의하세요.</summary>
    public string Password
    {
        get => _password;
        set { _password = value; _passwordBytes = -1; }
    }

    // GetBodySize↔Serialize UTF-8 스캔 중복 방지(4회 → 2회)
    private int UsernameByteCount => _usernameBytes >= 0
        ? _usernameBytes
        : (_usernameBytes = Encoding.UTF8.GetByteCount(_username));

    private int PasswordByteCount => _passwordBytes >= 0
        ? _passwordBytes
        : (_passwordBytes = Encoding.UTF8.GetByteCount(_password));

    /// <inheritdoc/>
    // 바디 레이아웃: [Username 길이(2B ushort) | Username UTF-8] [Password 길이(2B ushort) | Password UTF-8]
    public int GetBodySize() => 2 + UsernameByteCount + 2 + PasswordByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteString(_username, UsernameByteCount);
        writer.WriteString(_password, PasswordByteCount);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        // ReadString = Alloc: 수신 버퍼 뷰에서 새 string을 힙에 생성. string은 불변이라 zero-copy 불가.
        Username = reader.ReadString();
        Password = reader.ReadString();
    }
}
