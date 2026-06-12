using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 몹이 사망했을 때 서버가 전체 클라이언트에 즉시 브로드캐스트하는 패킷입니다.
/// 사망 세대, 최다 딜러(MVP) 정보를 담습니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> IPacket은 순수 데이터 홀더입니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> class이므로 역직렬화 시 <c>new MobDeathPacket()</c> 1회 + SpanReader.ReadString 1회
/// 힙 할당이 발생합니다. 그러나 이 패킷은 몹 사망 시에만(저빈도) 송수신되므로 GC 영향은 무시할 수 있습니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking.
/// </description></item>
/// <item><description>
/// <b>MVP 정의:</b> <see cref="MvpName"/>은 마지막 타격자가 아닌, 해당 세대에서
/// 누적 데미지가 가장 높은 세션의 닉네임입니다(<see cref="TopDamage"/> 참조).
/// </description></item>
/// </list>
/// </remarks>
// class 선택: 가변 길이 string(MvpName)을 담으므로 struct로 만들어도 string 참조 때문에 무할당이 불가.
// 사망은 저빈도 이벤트이므로 1회 할당이 GC 압력에 미치는 영향은 미미하다.
public sealed class MobDeathPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 7;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>
    /// 사망한 몹의 세대 번호입니다. <see cref="MobHpPacket.Generation"/>과 대응합니다.
    /// </summary>
    public int Generation { get; set; }

    /// <summary>이번 세대에서 MVP가 누적으로 입힌 총 데미지입니다.</summary>
    public long TopDamage { get; set; }

    /// <summary>
    /// 이번 세대 최다 딜러(MVP)의 닉네임입니다.
    /// 딜러가 없으면 "없음"을 반환합니다.
    /// </summary>
    public string MvpName
    {
        get => _mvpName;
        set { _mvpName = value; _mvpNameBytes = -1; } // 캐시 무효화
    }

    private string _mvpName = string.Empty;

    // UTF-8 바이트 수 캐시(-1=미계산): GetBodySize↔Serialize 이중 스캔 방지 — setter에서 무효화
    private int _mvpNameBytes = -1;

    // GetBodySize와 Serialize 간 UTF-8 인코딩 스캔 중복 방지 (2회 → 1회)
    private int MvpNameByteCount => _mvpNameBytes >= 0
        ? _mvpNameBytes
        : (_mvpNameBytes = Encoding.UTF8.GetByteCount(_mvpName));

    /// <inheritdoc/>
    // 본문: int Generation (4B) + long TopDamage (8B) + ushort len (2B) + UTF-8 MvpName
    public int GetBodySize() => 4 + 8 + 2 + MvpNameByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteInt32(Generation);
        writer.WriteInt64(TopDamage);
        writer.WriteString(MvpName, MvpNameByteCount);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Generation = reader.ReadInt32();
        TopDamage = reader.ReadInt64();
        // ReadString = Alloc: 수신 버퍼(얕은 뷰)에서 새 string을 깊은복사로 생성 — string은 불변이라 zero-copy 불가.
        MvpName = reader.ReadString();
    }
}
