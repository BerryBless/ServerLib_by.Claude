using System.Diagnostics;
using ServerLib.Core.Serialization.Packets;

namespace Ticketing;

/// <summary>
/// lock-free 방식으로 고정 슬롯 티켓 재고를 관리합니다.
/// <para>슬롯 상태 전이: <c>Free(0) → Reserved(1) → Sold(2)</c></para>
/// <para>실패 시: <c>Reserved(1) → Free(0)</c> (결제 실패·이탈·TTL)</para>
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Thread-safe. 모든 슬롯 상태 전이는 <c>Interlocked.CompareExchange</c> /
/// <c>Interlocked.Exchange</c>를 통해 원자적으로 수행됩니다.
/// </description></item>
/// <item><description>
/// <b>직렬 세션 디스패치 가정:</b> <see cref="TryReserve"/>의 중복 예약 가드(<c>ctx.SlotIndex ≥ 0</c>)는
/// "동일 세션에서 패킷이 직렬(serial)로 처리된다"는 가정 위에서 안전합니다.
/// <c>SocketPipelineSession.ReadPipeAsync</c>가 각 패킷을 <c>await DispatchPacketAsync</c>로 처리하고
/// 완료 후 다음 패킷을 읽으므로, 같은 세션의 두 <c>TicketReserveRequest</c>는 절대 동시에 실행되지 않습니다.
/// </description></item>
/// <item><description>
/// <b>단일 소비 선형화:</b> <see cref="Confirm"/> / <see cref="Release"/> / <see cref="ReleaseByContext"/>는
/// 모두 <c>Interlocked.Exchange(ref ctx.SlotIndex, -1)</c>을 선형화 지점으로 삼습니다.
/// 중복 결제·이탈-결제 경합에서 정확히 하나만 승리합니다.
/// </description></item>
/// <item><description>
/// <b>ABA 대응:</b> TTL 스위퍼는 <c>Interlocked.CompareExchange(ref owner.SlotIndex, -1, i)</c>를 사용해
/// 슬롯 소유권을 원자적으로 검증합니다. 소유자가 이미 확정·반납·재예약한 경우 CAS가 실패하여 no-op입니다.
/// </description></item>
/// <item><description>
/// <b>Memory:</b> 배열 필드 3개(int / TicketContext? / long)이며 <c>TotalTickets</c> 수에 비례합니다.
/// 일반 경로에서 힙 할당 없음(Zero-allocation).
/// </description></item>
/// </list>
/// </remarks>
public sealed class TicketInventory
{
    private const int Free     = 0; // 슬롯 비어있음 — TryReserve CAS 대상 초기값
    private const int Reserved = 1; // 예약됨 — 결제 대기 중
    private const int Sold     = 2; // 확정 — 결제 성공

    // int[]: Interlocked.CompareExchange/Exchange의 직접 CAS 대상. 32bit 정렬 int만 원자 보장(64bit 필드 불가)
    private readonly int[] _states;

    // TicketContext?[]: 슬롯별 소유자 참조. 참조 읽기/쓰기는 64bit에서 원자적(포인터 크기).
    // TTL 스위퍼 전용 — CAS 대상이 아니므로 Volatile.Read/Write로 가시성만 보장한다.
    private readonly TicketContext?[] _owners;

    // long[]: Stopwatch.GetTimestamp() 기반 예약 시각(단조증가). TTL 판단에만 사용한다.
    // Volatile.Read/Write: 스위퍼 스레드에서 읽고 TryReserve 스레드에서 쓰는 교차 접근이므로 가시성 보장 필요.
    private readonly long[] _reservedAtTicks;

    private readonly int  _totalTickets; // 슬롯 배열 경계값 — 루프 상한으로만 사용
    // long: Stopwatch.GetTimestamp() delta로 TTL 판단. Stopwatch 틱은 커널 전환 없는 단조 시계(시스템 시각 변경에 무관)
    private readonly long _ttlTicks;

    /// <summary>전체 슬롯 수입니다.</summary>
    public int TotalTickets => _totalTickets;

    /// <summary>현재 Free 상태인 슬롯 수(스냅샷)입니다. 읽기 직후 변동될 수 있습니다.</summary>
    public int FreeCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _totalTickets; i++)
                // Volatile.Read: 최신 상태를 관찰 — 완전한 원자적 일관성은 아니지만 스냅샷으로 충분
                if (Volatile.Read(ref _states[i]) == Free) count++;
            return count;
        }
    }

    /// <summary>티켓 재고를 초기화합니다.</summary>
    /// <param name="totalTickets">재고 슬롯 수입니다. 1–255 범위여야 합니다.</param>
    /// <param name="reservationTtl">예약 후 결제하지 않으면 자동 반납되는 시간입니다.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalTickets"/>가 1 미만이거나 255 초과인 경우.</exception>
    public TicketInventory(int totalTickets, TimeSpan reservationTtl)
    {
        // [ARCH-07] TicketResultPacket.Remaining이 byte(0~255)이므로 슬롯 수 상한을 byte.MaxValue로 제한
        if (totalTickets <= 0 || totalTickets > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(totalTickets),
                $"totalTickets은 1–{byte.MaxValue} 범위여야 합니다. (TicketResultPacket.Remaining 필드가 1바이트)");
        _totalTickets    = totalTickets;
        _states          = new int[totalTickets];              // 기본값 0 = Free
        _owners          = new TicketContext?[totalTickets];
        _reservedAtTicks = new long[totalTickets];
        // Stopwatch.Frequency: 초당 GetTimestamp 틱 수 — 플랫폼 독립적 시간 변환
        _ttlTicks        = (long)(reservationTtl.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// 슬롯 하나를 선착순으로 예약합니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션의 컨텍스트입니다.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Reserved"/>, 슬롯 인덱스): 성공 |
    /// (<see cref="TicketStatus.SoldOut"/>, -1): 슬롯 없음 |
    /// (<see cref="TicketStatus.AlreadyReserved"/>, 기존 슬롯): 중복
    /// </returns>
    /// <remarks>
    /// <b>직렬 세션 디스패치 가정</b>: 동일 세션에서 두 TicketReserveRequest는 절대 동시에 실행되지 않습니다
    /// (<c>SocketPipelineSession.ReadPipeAsync</c>가 이전 <see langword="await"/>가 완료된 후 다음 패킷을 처리).
    /// 이 가정 아래 <c>ctx.SlotIndex ≥ 0</c> 가드는 check-then-act이어도 안전합니다.
    /// </remarks>
    public (TicketStatus status, int slot) TryReserve(TicketContext ctx)
    {
        // 직렬 디스패치 가정 아래 안전한 중복 예약 가드
        if (Volatile.Read(ref ctx.SlotIndex) >= 0)
            return (TicketStatus.AlreadyReserved, ctx.SlotIndex);

        long nowTicks = Stopwatch.GetTimestamp();
        for (int i = 0; i < _totalTickets; i++)
        {
            // CAS: Free(0) → Reserved(1). 성공하면 이 스레드가 슬롯 i를 점유한 유일한 예약자.
            // 다른 스레드가 먼저 CAS에 성공하면 이 CAS는 실패(반환값 != Free) → 다음 슬롯으로 이동.
            if (Interlocked.CompareExchange(ref _states[i], Reserved, Free) == Free)
            {
                // 소유자 참조 기록: Volatile.Write로 스위퍼 스레드에 가시성 보장(발행 순서 중요)
                Volatile.Write(ref _owners[i], ctx);
                Volatile.Write(ref _reservedAtTicks[i], nowTicks);
                // SlotIndex를 마지막에 발행: 이 쓰기가 관찰되면 위 두 쓰기도 이미 완료된 것이 보장됨
                Volatile.Write(ref ctx.SlotIndex, i);
                return (TicketStatus.Reserved, i);
            }
        }
        return (TicketStatus.SoldOut, -1);
    }

    /// <summary>
    /// 결제 성공 후 슬롯을 <c>Sold</c>로 확정합니다.
    /// </summary>
    /// <param name="ctx">결제를 완료한 세션의 컨텍스트입니다.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Confirmed"/>, 슬롯): 성공 |
    /// (<see cref="TicketStatus.NotReserved"/>, -1): 예약 없음·중복 결제
    /// </returns>
    public (TicketStatus status, int slot) Confirm(TicketContext ctx)
    {
        // 선형화 지점: Exchange로 SlotIndex를 단 한 번만 소비한다.
        // 동시에 들어오는 Release / ReleaseByContext / SweepExpired 경합에서 정확히 하나만 승리.
        int slot = Interlocked.Exchange(ref ctx.SlotIndex, -1);
        if (slot < 0)
            return (TicketStatus.NotReserved, -1);

        // _owners[slot]=null 먼저: _states[slot]이 아직 Reserved인 동안 null로 교체하여
        // 스위퍼가 null 참조를 읽어 불필요한 ReleaseByContext를 시도하는 것을 방지한다.
        Volatile.Write(ref _owners[slot], null);
        Interlocked.Exchange(ref _states[slot], Sold);
        return (TicketStatus.Confirmed, slot);
    }

    /// <summary>
    /// 결제 실패 후 슬롯을 <c>Free</c>로 반납합니다.
    /// </summary>
    /// <param name="ctx">결제에 실패한 세션의 컨텍스트입니다.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Released"/>, 슬롯): 성공 |
    /// (<see cref="TicketStatus.NotReserved"/>, -1): 예약 없음
    /// </returns>
    public (TicketStatus status, int slot) Release(TicketContext ctx)
    {
        int slot = Interlocked.Exchange(ref ctx.SlotIndex, -1);
        if (slot < 0)
            return (TicketStatus.NotReserved, -1);

        Volatile.Write(ref _owners[slot], null);
        Interlocked.Exchange(ref _states[slot], Free);
        return (TicketStatus.Released, slot);
    }

    /// <summary>
    /// 세션 이탈 또는 TTL 스위퍼에서 슬롯을 <c>Free</c>로 반납합니다.
    /// <see cref="TicketContext.SlotIndex"/>가 이미 소비됐거나 예약이 없으면 no-op입니다.
    /// </summary>
    /// <param name="ctx">슬롯을 반납할 세션의 컨텍스트입니다. <see langword="null"/>이면 no-op입니다.</param>
    /// <remarks>
    /// <b>void 반환 이유:</b> 이탈·TTL 경로에서는 결과를 클라이언트에 전달할 세션이 없으므로
    /// 반환값이 불필요합니다. 결제 실패 응답이 필요한 경우에는 <see cref="Release"/>를 사용하세요.
    /// </remarks>
    public void ReleaseByContext(TicketContext? ctx)
    {
        if (ctx is null) return; // 로그인 전 이탈 — no-op
        // Exchange: 단일 소비 — 이탈 핸들러와 동시에 들어온 결제 Confirm 중 하나만 승리한다.
        int slot = Interlocked.Exchange(ref ctx.SlotIndex, -1);
        if (slot < 0) return; // 이미 소비됨(확정·반납됨) — no-op

        Volatile.Write(ref _owners[slot], null);
        Interlocked.Exchange(ref _states[slot], Free);
    }

    /// <summary>
    /// TTL이 초과된 Reserved 슬롯을 스캔하여 자동 반납합니다.
    /// </summary>
    /// <returns>이번 스윕에서 반납된 슬롯 수입니다.</returns>
    /// <remarks>
    /// <para>ABA 안전 설계: <c>Interlocked.CompareExchange(ref owner.SlotIndex, -1, i)</c>를 사용합니다.
    /// 스위퍼가 소유자 참조를 읽은 뒤 해당 소유자가 다른 경로(Confirm/Release)로 SlotIndex를 소비하고
    /// 새 슬롯을 예약한 경우, CAS는 현재 SlotIndex 값이 <c>i</c>가 아니므로 실패하여 no-op입니다.
    /// Exchange 기반 <see cref="ReleaseByContext"/>와 달리, 스위퍼만 CAS 방식을 사용하는 이유가 여기 있습니다.</para>
    /// </remarks>
    public int SweepExpired()
    {
        int released = 0;
        long now = Stopwatch.GetTimestamp();

        for (int i = 0; i < _totalTickets; i++)
        {
            // 1. Reserved 상태가 아니면 건너뜀(Free·Sold)
            if (Volatile.Read(ref _states[i]) != Reserved) continue;

            // 2. TTL 미초과 슬롯 건너뜀
            // Interlocked.Read: Transport 계층과 일관성 유지 — x86 32비트에서 long torn-read 방지
            long reservedAt = Interlocked.Read(ref _reservedAtTicks[i]);
            if (now - reservedAt < _ttlTicks) continue;

            // 3. 소유자 참조 획득
            var owner = Volatile.Read(ref _owners[i]);
            if (owner is null) continue; // 이미 다른 경로가 정리 중 — 건너뜀

            // 4. ABA 안전 CAS: owner.SlotIndex == i인 경우에만 -1로 교체.
            //    소유자가 이미 Confirm/Release로 SlotIndex를 소비했거나(-1),
            //    새 슬롯을 재예약했으면(!=i) CAS 실패 → no-op.
            if (Interlocked.CompareExchange(ref owner.SlotIndex, -1, i) != i) continue;

            // 5. CAS 성공: 스위퍼가 단독으로 슬롯 정리
            Debug.Assert(Volatile.Read(ref _states[i]) == Reserved,
                $"SweepExpired: 슬롯 {i} CAS 성공 후 Reserved 상태가 아님 — 예상치 못한 상태 전이");
            // _owners 먼저 null: _states가 아직 Reserved이므로 신규 TryReserve가 불가 — null 쓰기 안전
            Volatile.Write(ref _owners[i], null);
            Interlocked.Exchange(ref _states[i], Free);
            released++;
        }

        return released;
    }
}
