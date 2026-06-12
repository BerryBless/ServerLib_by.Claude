namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 클라이언트가 몹에게 입힌 데미지를 서버로 전달하는 패킷입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> IPacket은 순수 데이터 홀더이며, 직렬화/역직렬화는 호출 스레드에서만 수행됩니다.
/// 동일 인스턴스를 여러 스레드에서 동시에 사용하지 마십시오.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. struct이므로 역직렬화 시 <c>new T()</c>가 스택/인라인 생성됩니다.
/// class였다면 고빈도 공격 패킷에서 매 수신마다 Gen0 힙 압력이 발생합니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 모든 직렬화/역직렬화 연산은 즉시 반환합니다.
/// </description></item>
/// <item><description>
/// <b>보안:</b> 서버는 <c>Amount</c> 값이 0 이하이거나 상한(<c>MobManager.MaxHitDamage</c>)을 초과하는 경우
/// 무시·클램프 처리해야 합니다. 클라이언트가 임의로 큰 값을 보내 몹을 1샷하는 것을 방지합니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 본문이 4B 고정인 고빈도 공격 패킷 — 역직렬화 시 new T()가 무할당. class였다면 초당 수천 번 Gen0 압력.
public struct DamagePacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 5;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>이 공격으로 입힌 데미지 량입니다.</summary>
    public int Amount { get; set; }

    /// <inheritdoc/>
    // 본문: int Amount (4B LE) 고정
    public int GetBodySize() => 4;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer) => writer.WriteInt32(Amount);

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader) => Amount = reader.ReadInt32();
}
