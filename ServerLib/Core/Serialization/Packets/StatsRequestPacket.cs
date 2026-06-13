using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 모니터가 게임 서버의 관리 포트로 전송하는 통계 요청 패킷입니다.
/// 본문이 없는 0바이트 신호 패킷입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> struct이므로 값 복사 — 다중 스레드 공유에 안전합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. struct + 본문 0바이트, 힙 할당 없음.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking.
/// </description></item>
/// <item><description>
/// <b>프로토콜:</b> 와이어 포맷은 헤더 4바이트만입니다 (PacketId=8 LE, BodyLength=0 LE).
/// Python 측: <c>struct.pack('&lt;HH', 8, 0)</c> 으로 전송합니다.
/// 응답은 <see cref="StatsResponsePacket"/> (Id=9)으로 수신합니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문 0바이트의 단순 트리거 패킷 — 힙 할당 없이 값 복사로 안전하게 전달.
public struct StatsRequestPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다 (8). 다음 예약 구간(0xFFFE/0xFFFF) 이전의 첫 관리 패킷.</summary>
    public const ushort Id = 8;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <inheritdoc/>
    // 본문 없음 — 존재 자체가 "통계 요청" 신호이므로 페이로드 불필요
    public int GetBodySize() => 0;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) { /* 본문 없음 */ }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) { /* 본문 없음 */ }
}
