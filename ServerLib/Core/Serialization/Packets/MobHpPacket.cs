namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 서버가 몹의 현재 HP 상태를 전체 클라이언트에 주기적으로 브로드캐스트하는 패킷입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> IPacket은 순수 데이터 홀더입니다. 브로드캐스트 루프가 직렬화 후 버퍼를
/// <see cref="System.Buffers.ArrayPool{T}"/>에 반납하기 전까지 인스턴스를 재사용하지 마십시오.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. struct이므로 역직렬화 시 <c>new T()</c>가 스택/인라인 생성됩니다.
/// 200ms 주기 브로드캐스트에서 매 틱마다 힙 할당이 없습니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 모든 직렬화/역직렬화 연산은 즉시 반환합니다.
/// </description></item>
/// <item><description>
/// <b>HP 범위:</b> <see cref="Hp"/>는 0 이상 <see cref="MaxHp"/> 이하임을 보장합니다(서버 측 <c>Math.Max(0, ...)</c> 클램프).
/// 클라이언트는 음수 HP를 방어적으로 처리할 필요가 없습니다.
/// </description></item>
/// </list>
/// </remarks>
// struct 선택: 20B 고정 크기 주기 브로드캐스트 패킷 — 역직렬화 무할당, class였다면 200ms마다 각 클라에서 Gen0 압력.
public struct MobHpPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 6;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>현재 몹의 남은 HP입니다. 0 이상 <see cref="MaxHp"/> 이하.</summary>
    public long Hp { get; set; }

    /// <summary>몹의 최대 HP입니다.</summary>
    public long MaxHp { get; set; }

    /// <summary>
    /// 현재 몹 세대 번호입니다. 리스폰할 때마다 1씩 증가합니다.
    /// 클라이언트는 이 값으로 HP 패킷이 어느 몹 인스턴스의 정보인지 판별합니다.
    /// </summary>
    public int Generation { get; set; }

    /// <inheritdoc/>
    // 본문: long Hp (8B) + long MaxHp (8B) + int Generation (4B) = 20B 고정
    public int GetBodySize() => 20;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteInt64(Hp);
        writer.WriteInt64(MaxHp);
        writer.WriteInt32(Generation);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Hp = reader.ReadInt64();
        MaxHp = reader.ReadInt64();
        Generation = reader.ReadInt32();
    }
}
