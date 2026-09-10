namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 서버가 <see cref="CounterQueryPacket"/>(Id=18) 요청에 응답해 공유 카운터의 현재 상태를 돌려주는 패킷입니다.
/// 본문 16바이트 = <see cref="Value"/>(8B) + <see cref="AppliedOps"/>(8B).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> IPacket은 순수 데이터 홀더입니다(Not Thread-safe).
/// 송신 확장(<c>PacketSendExtensions.SendAsync</c>)이 직렬화를 끝내기 전까지 인스턴스를 재사용하지 마십시오.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. struct이므로 역직렬화 시 <c>new T()</c>가 스택/인라인 생성됩니다.
/// 필드가 전부 고정 크기 <see cref="long"/>이라 문자열처럼 UTF-8 디코딩 힙 할당이 끼어들 여지가 없습니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 직렬화·역직렬화 모두 즉시 반환합니다.
/// </description></item>
/// <item><description>
/// <b>두 값의 원자성(중요):</b> 서버는 <see cref="Value"/>와 <see cref="AppliedOps"/>를 각각
/// <c>Interlocked</c>로 읽지만, <b>두 값을 한 번에 읽는 연산은 원자적이지 않습니다</b>.
/// 다른 세션이 동시에 증감 중이면 이 패킷의 두 값은 서로 다른 시점의 스냅샷일 수 있습니다.
/// 따라서 <b>두 값을 함께 단언(assert)하는 것은 모든 증감이 끝난 정지 상태(배리어 이후 최종 조회)에서만</b>
/// 유효합니다. 진행 중 조회값은 진척 확인·디버깅 참고용으로만 쓰십시오.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 16B 고정 크기 응답 패킷 — 역직렬화 무할당. class였다면 조회 왕복 1회마다 Gen0 객체가 생긴다.
public struct CounterValuePacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 19;

    /// <summary>본문 바이트 수 상수입니다. <c>long Value(8) + long AppliedOps(8) = 16</c>.</summary>
    public const int BodySize = 16;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>
    /// 조회 시점의 공유 카운터 값입니다. 더하기 1회당 +1, 빼기 1회당 −1이 누적된 순(net) 결과입니다.
    /// </summary>
    public long Value { get; set; }

    /// <summary>
    /// 조회 시점까지 서버가 <b>실제로 적용한 증감 연산의 총 횟수</b>입니다(더하기 + 빼기).
    /// 조회 패킷·검증 실패로 드롭된 패킷·미지 패킷은 계수하지 않습니다.
    /// </summary>
    /// <remarks>
    /// <see cref="Value"/>만으로는 "더하기 N회 + 빼기 N회"와 "아무 것도 처리하지 않음"이 모두 0으로 보여
    /// 상쇄에 의한 결함 은폐가 생깁니다. <see cref="AppliedOps"/>는 그 은폐를 깨고,
    /// FAIL 시 원인을 <i>패킷 미도달</i>(AppliedOps 부족)과 <i>갱신 유실</i>(AppliedOps는 맞는데 Value가 틀림)로
    /// 분해하는 진단 값입니다.
    /// </remarks>
    public long AppliedOps { get; set; }

    /// <inheritdoc/>
    // 본문: long Value (8B) + long AppliedOps (8B) = 16B 고정.
    // 고정 크기이므로 수신 측이 bodyLength == 16을 상수로 검증할 수 있다(가변 길이 패킷의 파싱 분기가 불필요).
    public int GetBodySize() => BodySize;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteInt64(Value);
        writer.WriteInt64(AppliedOps);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Value = reader.ReadInt64();
        AppliedOps = reader.ReadInt64();
    }
}
