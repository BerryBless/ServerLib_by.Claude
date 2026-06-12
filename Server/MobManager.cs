using System.Collections.Concurrent;
using ServerLib.Core.Serialization.Packets;

/// <summary>
/// 서버 측 보스 몹 1마리의 상태를 캡슐화하는 클래스입니다.
/// 여러 I/O 스레드가 동시에 데미지를 적용할 수 있도록 모든 변경 연산을 lock-free로 구현합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Thread-safe. <see cref="ApplyDamage"/>, <see cref="Snapshot"/> 모두 동시 호출 안전합니다.
/// 내부적으로 <see cref="Interlocked"/> 연산과 <see cref="ConcurrentDictionary{TKey,TValue}"/>로 락 경합을 최소화합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> <see cref="ApplyDamage"/>는 <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate"/>
/// 내부 분할 잠금(Striped Lock) 구조로 인해 첫 삽입 시 버킷 항목을 힙에 할당합니다.
/// 갱신은 ValueTuple 교체로 처리됩니다. <see cref="Snapshot"/>은 Zero-allocation입니다.
/// </description></item>
/// <item><description>
/// <b>세대 경계 미세 오차:</b> 사망 처리(리스폰) 중에 도착한 공격은 새 세대로 귀속되거나 유실될 수 있습니다.
/// 이는 lock 없는 설계의 의도된 트레이드오프이며, 데모 범위에서 허용됩니다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class MobManager
{
    // Interlocked.Add: CPU LOCK XADD 명령으로 다수 I/O 스레드의 동시 감소가 정확히 원자 반영된다.
    // long: 단일 타격이 int 범위라도 누적 HP 연산에서 오버플로를 방지하기 위해 64비트 사용.
    private long _hp;

    private readonly long _maxHp;

    // Interlocked.CompareExchange: 사망 처리 진입을 1회로 제한하는 CAS 게이트.
    // 0=열림(다음 사망 처리 가능), 1=닫힘(현재 사망 처리 중). int는 CAS의 가장 저비용 타입.
    private int _deathHandled;

    // Interlocked.Increment: 리스폰 시 단조 증가 — 클라이언트가 MobHpPacket의 Generation으로 어느 몹 인스턴스 정보인지 판별.
    private int _generation;

    // ConcurrentDictionary: 세분 잠금(Striped Locking) + CAS 기반 내부 구조로
    // 다수 I/O 스레드의 동시 AddOrUpdate를 락 경합 최소화하며 처리한다. 세션 단위 딜 집계에 적합.
    private readonly ConcurrentDictionary<Guid, (string label, long dmg)> _damageBySession = new();

    private readonly Action<MobDeathPacket> _onDeath;

    /// <summary>안티치트: 1타 데미지 상한. 이를 초과하는 값은 이 값으로 클램프됩니다.</summary>
    public const int MaxHitDamage = 10_000;

    /// <summary>
    /// 몹 관리자를 초기화합니다.
    /// </summary>
    /// <param name="maxHp">몹의 최대 HP입니다. 리스폰 시 이 값으로 복구됩니다.</param>
    /// <param name="onDeath">
    /// 몹 사망 확정 시 호출되는 콜백입니다. I/O 스레드에서 동기 호출되므로
    /// 콜백 내부에서 장기 블로킹 작업은 피하십시오. 브로드캐스트는 <c>Task.Run</c>으로 위임하십시오.
    /// </param>
    public MobManager(long maxHp, Action<MobDeathPacket> onDeath)
    {
        _maxHp = maxHp;
        _hp = maxHp;
        _onDeath = onDeath;
    }

    /// <summary>
    /// 지정 세션이 몹에게 데미지를 적용합니다.
    /// </summary>
    /// <param name="sessionId">공격한 클라이언트의 세션 ID입니다.</param>
    /// <param name="label">MVP 표시에 사용할 세션 레이블(닉네임 또는 엔드포인트)입니다.</param>
    /// <param name="amount">적용할 데미지 량입니다. 0 이하이면 무시됩니다. <see cref="MaxHitDamage"/>를 초과하면 클램프됩니다.</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 다수 I/O 스레드에서 동시 호출 안전합니다.
    /// <b>[Blocking:]</b> Non-blocking.
    /// <b>[Memory:]</b> 첫 삽입 시 ConcurrentDictionary 버킷 항목 1회 할당, 이후 갱신은 ValueTuple 교체.
    /// </remarks>
    public void ApplyDamage(Guid sessionId, string label, int amount)
    {
        // 안티치트: 음수·0 데미지 → HP 힐 또는 분모 0 방지. 상한 초과 → 1샷 방지.
        if (amount <= 0) return;
        if (amount > MaxHitDamage) amount = MaxHitDamage;

        // TArg 오버로드: static 람다로 클로저 캡처를 제거해 델리게이트 인스턴스 재사용(무할당 경로).
        // ValueTuple<string,int>를 팩토리 인수로 전달해 힙 할당 없이 label·amount를 전달한다.
        _damageBySession.AddOrUpdate(
            sessionId,
            static (_, arg) => (arg.label, (long)arg.amount),
            static (_, prev, arg) => (prev.label, prev.dmg + arg.amount),
            (label, amount));

        // Interlocked.Add: 원자 감소 후 남은 HP를 원자적으로 읽는다 — 별도 Volatile.Read 없이 단일 명령으로 처리.
        long remaining = Interlocked.Add(ref _hp, -(long)amount);
        if (remaining <= 0) TryHandleDeath();
    }

    private void TryHandleDeath()
    {
        // CAS 진입 가드: remaining≤0을 동시에 본 여러 스레드 중 오직 CAS 승자만 사망 처리.
        // 패자는 이미 _deathHandled=1이므로 즉시 반환해 이중 리스폰을 방지한다.
        if (Interlocked.CompareExchange(ref _deathHandled, 1, 0) != 0) return;

        // 딜 스냅샷: Clear() 이전에 읽어야 현 세대 딜이 반영된다.
        var snapshot = _damageBySession.ToArray();
        string mvpName = "없음";
        long topDmg = 0;
        foreach (var kv in snapshot)
        {
            if (kv.Value.dmg > topDmg)
            {
                topDmg = kv.Value.dmg;
                mvpName = kv.Value.label;
            }
        }

        var deathPkt = new MobDeathPacket
        {
            Generation = _generation,
            TopDamage = topDmg,
            MvpName = mvpName,
        };

        // 브로드캐스트(콜백): onDeath는 호출자(Program.cs)가 Task.Run으로 비동기 위임해 I/O 스레드 블로킹을 회피.
        _onDeath(deathPkt);

        // 사망/리스폰 순서 (이 순서를 반드시 지켜야 한다):
        // 1. 세대 집계 초기화 — 새 세대의 딜이 이전 세대에 합산되지 않도록.
        _damageBySession.Clear();
        // 2. HP 복구 — 이후 도착하는 공격은 새 몹에 적용.
        Interlocked.Exchange(ref _hp, _maxHp);
        // 3. 세대 번호 증가 — 클라이언트가 HP 패킷으로 리스폰을 인지.
        Interlocked.Increment(ref _generation);
        // 4. 마지막으로 게이트 해제 — HP 복구 이후에 열어야 다음 사망이 새 몹에 대해 1회만 처리됨.
        Volatile.Write(ref _deathHandled, 0);
    }

    /// <summary>
    /// 브로드캐스트용 현재 몹 상태 스냅샷을 반환합니다.
    /// </summary>
    /// <returns>원자적으로 읽은 (hp, maxHp, gen) 튜플입니다. hp는 항상 0 이상입니다.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe.
    /// <b>[Memory Allocation:]</b> Zero-allocation (ValueTuple 반환, 스택 할당).
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public (long hp, long maxHp, int gen) Snapshot()
    {
        // Interlocked.Read: 64비트 값의 명시적 원자 읽기 — 32비트 플랫폼에서 찢긴 읽기(torn read) 방지.
        // Math.Max(0,...): 사망 처리 창(window)에서 HP가 음수일 때 클라이언트에 0으로 표시.
        long hp = Math.Max(0L, Interlocked.Read(ref _hp));
        // Volatile.Read: int는 32비트이므로 원자 읽기 보장되지만 Volatile로 메모리 순서를 명시.
        int gen = Volatile.Read(ref _generation);
        return (hp, _maxHp, gen);
    }
}
