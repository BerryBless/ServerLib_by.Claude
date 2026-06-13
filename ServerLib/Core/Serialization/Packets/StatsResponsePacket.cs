using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 모니터의 <see cref="StatsRequestPacket"/> (Id=8)에 대한 서버 응답 패킷입니다.
/// UTF-8 JSON 원시 바이트를 본문으로 싣습니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Not Thread-safe. 생성 → 단일 송신 경로에서만 사용합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> sealed class이므로 인스턴스 1회 힙 할당.
/// <see cref="Json"/> setter에서 UTF-8 인코딩 결과를 byte[]로 저장(추가 할당 1회).
/// 역직렬화 시 <c>ReadRemainingBytes().ToArray()</c> 1회 복사 할당.
/// 이 패킷은 모니터의 <see cref="StatsRequestPacket"/>에만 응답하는 <b>저빈도 관리 경로</b>에서
/// 사용되므로 GC 압력은 무시할 수 있습니다(JSON 직렬화 자체도 할당 허용 경로).
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking.
/// </description></item>
/// <item><description>
/// <b>프로토콜 계약:</b> 본문은 UTF-8 JSON 원시 바이트입니다(길이 접두어 없음).
/// 패킷 헤더의 BodyLength(2B LE ushort)가 전체 본문 길이를 나타냅니다.
/// Python 측: 헤더 4바이트 → <c>struct.unpack('&lt;HH', hdr)</c> → body_len 바이트 읽기
/// → <c>json.loads(body.decode('utf-8'))</c>.
/// 본문은 반드시 65535 바이트 이하여야 합니다(ushort 한계). 초과 시 오류 JSON으로 대체됩니다.
/// </description></item>
/// </list>
/// </remarks>
// class 선택: 가변 길이 byte[] 페이로드를 보유하므로 struct는 부적합(복사 비용, mutable 한계).
// sealed: 상속 없이 단일 구체 타입 — devirtualization 최적화 허용.
public sealed class StatsResponsePacket : IPacket
{
    /// <summary>패킷 ID 상수입니다 (9).</summary>
    public const ushort Id = 9;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    // UTF-8 인코딩 결과를 미리 보관: GetBodySize()와 Serialize()가 동일 배열을 참조해
    // 이중 인코딩 없이 정확한 크기를 보고한다(MobDeathPacket._mvpNameBytes 캐시 선례).
    private byte[] _utf8Json = Array.Empty<byte>();

    /// <summary>
    /// 응답에 담을 JSON 문자열입니다. 설정 즉시 UTF-8 인코딩·캐시됩니다.
    /// 인코딩 결과가 65535 바이트를 초과하면 오류 JSON으로 안전하게 대체됩니다.
    /// </summary>
    public string Json
    {
        set
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            // PacketPool.WriteHeader 계약: BodyLength는 ushort(최대 65535).
            // 초과 시 빈 오류 JSON으로 대체하여 헤더 쓰기에서 ArgumentOutOfRangeException을 방지한다.
            _utf8Json = encoded.Length <= ushort.MaxValue
                ? encoded
                : Encoding.UTF8.GetBytes("{\"error\":\"stats_too_large\"}");
        }
    }

    /// <inheritdoc/>
    // 본문 = UTF-8 JSON 바이트 그대로(길이 접두어 없음)
    // — 헤더 BodyLength(2B)가 이미 전체 길이를 제공하므로 이중 접두어 불필요
    public int GetBodySize() => _utf8Json.Length;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        // WriteBytes: Zero-copy — 내부 byte[]를 목적지 Span에 직접 복사, 임시 버퍼 없음
        writer.WriteBytes(_utf8Json);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        // 본문 전체 = JSON 바이트 (길이 접두어 없음) → Remaining 바이트 전부 읽기.
        // ToArray(): ReadOnlySpan은 패킷 프레임 버퍼 수명에 종속 — 보관하려면 깊은복사 필요.
        // 역직렬화는 Python측 전용(클라이언트); C# 서버는 이 패킷을 수신하지 않아 GC 허용 경로.
        _utf8Json = reader.ReadRemainingBytes().ToArray();
    }
}
