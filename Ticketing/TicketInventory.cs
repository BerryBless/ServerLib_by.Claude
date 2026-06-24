using System.Diagnostics;
using ServerLib.Core.Serialization.Packets;

namespace Ticketing;

/// <summary>
/// lock-free 방식으로 2D 좌석 배치 티켓 재고를 관리합니다.
/// <para>슬롯 상태 전이: <c>Free(0) → Reserved(1) → Sold(2)</c></para>
/// <para>실패 시: <c>Reserved(1) → Free(0)</c> (결제 실패·이탈·TTL)</para>
/// <para>좌석 지정: 클라이언트가 <c>Row/Col</c>로 좌석을 지정하면 <c>seatId = Row * Cols + Col</c>로 평면화됩니다.</para>
/// <para>배치 예약: 한 요청에 N개 좌석을 All-or-nothing으로 예약합니다. 일부 실패 시 이번 요청에서 예약한 모든 좌석을 롤백합니다.</para>
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Thread-safe. 모든 슬롯 상태 전이는 <c>Interlocked.CompareExchange</c> /
/// <c>Interlocked.Exchange</c>를 통해 원자적으로 수행됩니다.
/// </description></item>
/// <item><description>
/// <b>직렬 세션 디스패치 가정:</b> <see cref="TryReserveBatch"/>의 중복 예약 가드(AlreadyReserved)와
/// 빈 슬롯 탐색 루프는 "동일 세션에서 패킷이 직렬(serial)로 처리된다"는 가정 위에서 안전합니다.
/// <c>SocketPipelineSession.ReadPipeAsync</c>가 각 패킷을 <c>await DispatchPacketAsync</c>로 처리하고
/// 완료 후 다음 패킷을 읽으므로, 같은 세션의 두 <c>TicketReserveRequest</c>는 절대 동시에 실행되지 않습니다.
/// </description></item>
/// <item><description>
/// <b>per-element 선형화:</b> <see cref="ConfirmAll"/> / <see cref="ReleaseAll"/> / <see cref="ReleaseAllByContext"/>는
/// 각 <c>ctx.Slots[k]</c>를 <c>Interlocked.Exchange(ref ctx.Slots[k], -1)</c>로 독립적으로 소비합니다.
/// 중복 결제·이탈-결제 경합에서 각 슬롯마다 정확히 하나만 승리합니다.
/// </description></item>
/// <item><description>
/// <b>ABA 안전(SweepExpired):</b> TTL 스위퍼는
/// <c>Interlocked.CompareExchange(ref owner.Slots[k], -1, seatId)</c>를 사용해
/// 특정 seatId와 일치할 때만 슬롯을 해제합니다.
/// 소유자가 이미 확정·반납·재예약한 경우 CAS가 실패하여 no-op입니다.
/// </description></item>
/// <item><description>
/// <b>롤백 ABA 안전(TryReserveBatch):</b> 배치 롤백 시 각 슬롯에
/// <c>Interlocked.Exchange(ref ctx.Slots[entry], -1)</c>를 먼저 적용합니다.
/// 반환값이 해당 seatId일 때만 <c>_states[seat]</c>를 Free로 변경합니다 —
/// 스위퍼가 먼저 소비했다면 skip(스위퍼가 이미 _states를 Free로 변경함).
/// </description></item>
/// <item><description>
/// <b>Memory:</b> 배열 필드 3개(int / TicketContext? / long)이며 <c>TotalTickets</c> 수에 비례합니다.
/// 일반 경로에서 힙 할당 없음(Zero-allocation).
/// </description></item>
/// </list>
/// </remarks>
public sealed class TicketInventory
{
    private const int Free     = 0; // 슬롯 비어있음 — TryReserveOne CAS 대상 초기값
    private const int Reserved = 1; // 예약됨 — 결제 대기 중
    private const int Sold     = 2; // 확정 — 결제 성공

    // int[]: Interlocked.CompareExchange/Exchange의 직접 CAS 대상. 32bit 정렬 int만 원자 보장(64bit 필드 불가)
    private readonly int[] _states;

    // TicketContext?[]: 슬롯별 소유자 참조. 참조 읽기/쓰기는 64bit에서 원자적(포인터 크기).
    // TTL 스위퍼 전용 — CAS 대상이 아니므로 Volatile.Read/Write로 가시성만 보장한다.
    private readonly TicketContext?[] _owners;

    // long[]: Stopwatch.GetTimestamp() 기반 예약 시각(단조증가). TTL 판단에만 사용한다.
    // Volatile.Read/Write: 스위퍼 스레드에서 읽고 TryReserveOne 스레드에서 쓰는 교차 접근이므로 가시성 보장 필요.
    private readonly long[] _reservedAtTicks;

    private readonly int  _rows;         // 행 수 (2D 좌석 표현)
    private readonly int  _cols;         // 열 수 (seatId = row*_cols + col)
    private readonly int  _totalTickets; // 슬롯 배열 경계값 = _rows * _cols
    // long: Stopwatch.GetTimestamp() delta로 TTL 판단. Stopwatch 틱은 커널 전환 없는 단조 시계(시스템 시각 변경에 무관)
    private readonly long _ttlTicks;

    // ── 누적 이벤트 카운터 ─────────────────────────────────────────────────────
    // long: Interlocked.Increment(ref long)은 CPU LOCK XADD 명령으로 힙 할당 없이 원자 증가.
    // 읽기 시 Interlocked.Read(ref long) 필수 — x86 32비트에서 long torn-read 방지.
    private long _totalReserved;      // TryReserveBatch 성공 시 n만큼 가산 (예약 누적)
    private long _totalSeatTaken;     // TryReserveBatch/TryReserve 실패 건수 (좌석 경합, 롤백 포함)
    private long _totalConfirmed;     // ConfirmAll/Confirm 좌석 확정 누적
    private long _totalPaymentFailed; // ReleaseAll/Release 결제 실패 반납 누적
    private long _totalAbandoned;     // ReleaseAllByContext 실제 반납 누적 (이탈·연결 해제)
    private long _totalExpired;       // SweepExpired 반납 누적 (TTL 만료)

    /// <summary>행 수입니다.</summary>
    public int Rows => _rows;

    /// <summary>열 수입니다. 전체 좌석 수 = <see cref="Rows"/> × <see cref="Cols"/>.</summary>
    public int Cols => _cols;

    /// <summary>전체 슬롯(좌석) 수입니다. <see cref="Rows"/> × <see cref="Cols"/>와 동일합니다.</summary>
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

    /// <summary>2D 좌석 배치로 티켓 재고를 초기화합니다.</summary>
    /// <param name="rows">좌석 행 수입니다. 1 이상이어야 합니다.</param>
    /// <param name="cols">좌석 열 수입니다. 1 이상이어야 합니다.</param>
    /// <param name="reservationTtl">예약 후 결제하지 않으면 자동 반납되는 시간입니다.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rows"/> 또는 <paramref name="cols"/>가 1 미만이거나,
    /// <paramref name="rows"/> × <paramref name="cols"/>가 255를 초과하는 경우.
    /// (제약: <see cref="TicketResultPacket"/>의 <c>Remaining</c>·<c>Slot</c> 필드가 1바이트)
    /// </exception>
    public TicketInventory(int rows, int cols, TimeSpan reservationTtl)
    {
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "행 수는 1 이상이어야 합니다.");
        if (cols <= 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "열 수는 1 이상이어야 합니다.");
        int totalTickets = rows * cols;
        // [ARCH-07] TicketResultPacket.Remaining·Slot이 byte(0~255)이므로 슬롯 수 상한을 byte.MaxValue로 제한
        if (totalTickets > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rows),
                $"rows*cols={totalTickets}은 1–{byte.MaxValue} 범위여야 합니다. (TicketResultPacket 필드가 1바이트)");
        _rows            = rows;
        _cols            = cols;
        _totalTickets    = totalTickets;
        _states          = new int[totalTickets];              // 기본값 0 = Free
        _owners          = new TicketContext?[totalTickets];
        _reservedAtTicks = new long[totalTickets];
        // Stopwatch.Frequency: 초당 GetTimestamp 틱 수 — 플랫폼 독립적 시간 변환
        _ttlTicks        = (long)(reservationTtl.TotalSeconds * Stopwatch.Frequency);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 핵심 배치 API
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 단일 좌석을 <c>Reserved</c>로 원자적으로 전이합니다.
    /// <see cref="TryReserveBatch"/>의 내부 프리미티브로, KPI 증가 없음 — 배치 커밋 후 일괄 가산합니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션 컨텍스트입니다.</param>
    /// <param name="seatId">예약할 좌석의 평면 인덱스입니다.</param>
    /// <param name="slotEntry">ctx.Slots에서 이 좌석을 기록할 원소 인덱스입니다.</param>
    /// <param name="nowTicks"><c>Stopwatch.GetTimestamp()</c> 기준 현재 시각입니다.</param>
    /// <returns>CAS 성공(예약 완료)이면 <see langword="true"/>; 이미 점유됐으면 <see langword="false"/>.</returns>
    private bool TryReserveOne(TicketContext ctx, int seatId, int slotEntry, long nowTicks)
    {
        // CAS: Free(0) → Reserved(1). 다른 스레드가 먼저 성공했으면 false.
        if (Interlocked.CompareExchange(ref _states[seatId], Reserved, Free) != Free)
            return false;

        // 발행 순서: _owners → _reservedAtTicks → 마지막에 Slots[slotEntry].
        // Slots[slotEntry]가 가시화되면 이전 두 쓰기도 이미 완료됨이 보장(스위퍼의 가시성 요건).
        Volatile.Write(ref _owners[seatId], ctx);
        Volatile.Write(ref _reservedAtTicks[seatId], nowTicks);
        Volatile.Write(ref ctx.Slots[slotEntry], seatId);
        return true;
    }

    /// <summary>
    /// 복수의 좌석을 All-or-nothing으로 예약합니다.
    /// 요청 좌석 중 하나라도 이미 점유됐거나 범위 초과이면 이번 요청의 모든 예약을 롤백하고 실패를 반환합니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션의 컨텍스트입니다.</param>
    /// <param name="seatIds">예약할 좌석 ID 목록입니다(seatId = row * Cols + col, 0-based).</param>
    /// <param name="reservedOut">
    /// 성공 시 실제로 예약된 seatId를 순서대로 기록하는 버퍼입니다.
    /// 길이는 <paramref name="seatIds"/>.Length 이상이어야 합니다.
    /// </param>
    /// <returns>
    /// (<see cref="TicketStatus.Reserved"/>, n): n개 전체 예약 성공 |
    /// (<see cref="TicketStatus.AlreadyReserved"/>, 0): 이미 다른 배치를 보유 중 |
    /// (<see cref="TicketStatus.SeatTaken"/>, 0): 범위 초과·중복·경합 실패(롤백 완료)
    /// </returns>
    /// <remarks>
    /// <b>직렬 세션 디스패치 가정:</b> AlreadyReserved 가드와 빈 슬롯 탐색은 동일 세션의 패킷이
    /// 직렬로 처리된다는 가정 위에서 안전합니다.
    /// <br/>
    /// <b>롤백 ABA 안전:</b> 롤백 시 <c>Interlocked.Exchange(ref ctx.Slots[entry], -1)</c>로 슬롯을 먼저 claim합니다.
    /// 반환값이 해당 seatId일 때만 <c>_states[seat]</c>를 Free로 변경합니다 —
    /// 스위퍼가 이미 소비했다면 skip(이중 해제 방지).
    /// </remarks>
    public (TicketStatus status, int reservedCount) TryReserveBatch(
        TicketContext ctx, ReadOnlySpan<int> seatIds, Span<int> reservedOut)
    {
        // 1. AlreadyReserved 가드 (직렬 디스패치 보장으로 안전)
        for (int k = 0; k < ctx.Slots.Length; k++)
            if (Volatile.Read(ref ctx.Slots[k]) >= 0)
                return (TicketStatus.AlreadyReserved, 0);

        // 2. 개수·상한 검증
        int n = seatIds.Length;
        if (n == 0 || n > ctx.Slots.Length)
            return (TicketStatus.SeatTaken, 0);

        // 3. 범위·중복 검증 (stackalloc: 최대 255석, 255B — 스택 안전)
        Span<bool> seen = stackalloc bool[_totalTickets];
        for (int i = 0; i < n; i++)
        {
            int s = seatIds[i];
            if ((uint)s >= (uint)_totalTickets || seen[s])
                return (TicketStatus.SeatTaken, 0);
            seen[s] = true;
        }

        long nowTicks = Stopwatch.GetTimestamp();

        // 4. 예약 루프 (All-or-nothing)
        // entryForIndex: i번째 seat가 ctx.Slots의 어느 entry에 저장됐는지 — 롤백에 필요
        Span<int> entryForIndex = stackalloc int[n];
        int successCount = 0;
        bool allOk = true;

        for (int i = 0; i < n; i++)
        {
            // 빈 슬롯 탐색: Slots[k] < 0인 첫 번째 원소
            // 직렬 세션 가정: 이 루프 중에 다른 TryReserveBatch가 동시에 Slots를 쓸 수 없음.
            int entry = -1;
            for (int k = 0; k < ctx.Slots.Length; k++)
                if (Volatile.Read(ref ctx.Slots[k]) < 0) { entry = k; break; }

            if (entry < 0 || !TryReserveOne(ctx, seatIds[i], entry, nowTicks))
            {
                allOk = false;
                break;
            }

            entryForIndex[i] = entry;
            reservedOut[successCount++] = seatIds[i];
        }

        if (!allOk)
        {
            // 5. 롤백: 이번 호출에서 예약에 성공한 좌석만 대상
            for (int i = 0; i < successCount; i++)
            {
                int seat  = reservedOut[i];
                int entry = entryForIndex[i];
                // ABA-safe claim: Exchange가 -1을 반환하면 스위퍼가 이미 소비 → no-op
                int prev = Interlocked.Exchange(ref ctx.Slots[entry], -1);
                if (prev == seat)
                {
                    // 이 스레드가 슬롯을 단독으로 회수 → _states를 Free로 복원
                    Volatile.Write(ref _owners[seat], null);
                    Interlocked.Exchange(ref _states[seat], Free);
                }
                // prev != seat: 스위퍼가 먼저 CAS로 슬롯을 소비하고 _states를 이미 Free로 변경함
            }
            Interlocked.Increment(ref _totalSeatTaken); // 배치 요청 단위 1회 카운트
            return (TicketStatus.SeatTaken, 0);
        }

        // 6. 전체 성공 — KPI를 n만큼 일괄 가산
        Interlocked.Add(ref _totalReserved, n);
        return (TicketStatus.Reserved, n);
    }

    /// <summary>
    /// 세션이 보유 중인 모든 좌석을 <c>Sold</c>로 확정합니다(결제 성공).
    /// </summary>
    /// <param name="ctx">결제를 완료한 세션의 컨텍스트입니다.</param>
    /// <param name="confirmedOut">확정된 seatId를 순서대로 기록하는 버퍼입니다.</param>
    /// <returns>실제로 확정된 좌석 수입니다. 0이면 예약이 없거나 이미 소비됐습니다(TTL 경합 등).</returns>
    /// <remarks>
    /// 각 <c>ctx.Slots[k]</c>를 <c>Interlocked.Exchange(ref ctx.Slots[k], -1)</c>로 소비합니다.
    /// 동시에 들어온 <see cref="ReleaseAll"/> / <see cref="ReleaseAllByContext"/> / <see cref="SweepExpired"/>와
    /// 슬롯마다 독립적으로 경쟁하며, 정확히 하나만 승리합니다.
    /// </remarks>
    public int ConfirmAll(TicketContext ctx, Span<int> confirmedOut)
    {
        int count = 0;
        for (int k = 0; k < ctx.Slots.Length; k++)
        {
            // 선형화 지점: Exchange로 Slots[k]를 단 한 번만 소비
            int seat = Interlocked.Exchange(ref ctx.Slots[k], -1);
            if (seat < 0) continue; // 빈 슬롯 — skip

            // _owners 먼저 null: 스위퍼가 null 참조를 읽어 불필요한 처리를 시도하는 것을 방지
            Volatile.Write(ref _owners[seat], null);
            Interlocked.Exchange(ref _states[seat], Sold);
            Interlocked.Increment(ref _totalConfirmed);
            if (count < confirmedOut.Length)
                confirmedOut[count] = seat;
            count++;
        }
        return count;
    }

    /// <summary>
    /// 세션이 보유 중인 모든 좌석을 <c>Free</c>로 반납합니다(결제 실패).
    /// </summary>
    /// <param name="ctx">결제에 실패한 세션의 컨텍스트입니다.</param>
    /// <param name="releasedOut">반납된 seatId를 순서대로 기록하는 버퍼입니다.</param>
    /// <returns>실제로 반납된 좌석 수입니다.</returns>
    public int ReleaseAll(TicketContext ctx, Span<int> releasedOut)
    {
        int count = 0;
        for (int k = 0; k < ctx.Slots.Length; k++)
        {
            int seat = Interlocked.Exchange(ref ctx.Slots[k], -1);
            if (seat < 0) continue;

            Volatile.Write(ref _owners[seat], null);
            Interlocked.Exchange(ref _states[seat], Free);
            Interlocked.Increment(ref _totalPaymentFailed);
            if (count < releasedOut.Length)
                releasedOut[count] = seat;
            count++;
        }
        return count;
    }

    /// <summary>
    /// 세션 이탈 또는 연결 해제 시 보유 중인 모든 좌석을 <c>Free</c>로 반납합니다.
    /// 이미 소비됐거나 예약이 없는 슬롯은 no-op입니다.
    /// </summary>
    /// <param name="ctx">반납할 세션의 컨텍스트입니다. <see langword="null"/>이면 no-op입니다.</param>
    /// <remarks>
    /// <b>void 반환 이유:</b> 이탈 경로에서는 결과를 클라이언트에 전달할 세션이 없으므로
    /// 반환값이 불필요합니다. 결제 실패 응답이 필요한 경우에는 <see cref="ReleaseAll"/>을 사용하세요.
    /// </remarks>
    public void ReleaseAllByContext(TicketContext? ctx)
    {
        if (ctx is null) return; // 로그인 전 이탈 — no-op
        for (int k = 0; k < ctx.Slots.Length; k++)
        {
            // Exchange: 단일 소비 — 이탈 핸들러와 동시에 들어온 결제 Confirm 중 하나만 승리
            int seat = Interlocked.Exchange(ref ctx.Slots[k], -1);
            if (seat < 0) continue; // 이미 소비됨 또는 빈 슬롯 — no-op

            Volatile.Write(ref _owners[seat], null);
            Interlocked.Exchange(ref _states[seat], Free);
            // 이탈·연결 해제로 인한 반납 완료: 이탈 누적 카운터 증가
            Interlocked.Increment(ref _totalAbandoned);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 단일 좌석 하위호환 래퍼 (기존 테스트·호출 코드 최소 수정)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 클라이언트가 지정한 좌석(<paramref name="seatId"/>)을 예약합니다.
    /// 내부적으로 <see cref="TryReserveBatch"/>에 1-원소 배치를 위임합니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션의 컨텍스트입니다.</param>
    /// <param name="seatId">예약할 좌석의 평면 인덱스입니다(seatId = row * Cols + col).</param>
    /// <returns>
    /// (<see cref="TicketStatus.Reserved"/>, seatId): 성공 |
    /// (<see cref="TicketStatus.AlreadyReserved"/>, 기존 slot): 이미 예약 중 |
    /// (<see cref="TicketStatus.SeatTaken"/>, -1 or seatId): 범위 밖 또는 좌석 점유됨
    /// </returns>
    public (TicketStatus status, int slot) TryReserve(TicketContext ctx, int seatId)
    {
        // 범위 이탈: 하위호환 — 직접 -1을 반환(TryReserveBatch는 SeatTaken+0 반환 시 seatId 구분 불가)
        if ((uint)seatId >= (uint)_totalTickets)
            return (TicketStatus.SeatTaken, -1);

        // 1-원소 스택 배치로 배치 API에 위임
        Span<int> seatBuf    = stackalloc int[1];
        Span<int> reservedOut = stackalloc int[1];
        seatBuf[0] = seatId;
        var (status, count) = TryReserveBatch(ctx, seatBuf, reservedOut);
        return status switch
        {
            TicketStatus.Reserved       => (TicketStatus.Reserved, reservedOut[0]),
            TicketStatus.AlreadyReserved => (TicketStatus.AlreadyReserved, FirstHeldSlot(ctx)),
            _                           => (TicketStatus.SeatTaken, seatId) // CAS 실패 경합
        };
    }

    /// <summary>
    /// 클라이언트가 지정한 Row/Col 좌표로 좌석을 예약합니다.
    /// Row/Col→seatId 변환과 경계 검증이 이 메서드에 캡슐화됩니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션의 컨텍스트입니다.</param>
    /// <param name="row">좌석 행 인덱스입니다(0-based). 유효 범위: <c>0 ≤ row &lt; Rows</c>.</param>
    /// <param name="col">좌석 열 인덱스입니다(0-based). 유효 범위: <c>0 ≤ col &lt; Cols</c>.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Reserved"/>, seatId): 성공 |
    /// (<see cref="TicketStatus.AlreadyReserved"/>, 기존 slot): 이미 예약 중 |
    /// (<see cref="TicketStatus.SeatTaken"/>, -1): 범위 초과 또는 좌석 점유됨
    /// </returns>
    /// <remarks>
    /// Row/Col 경계를 검증한 뒤 <see cref="TryReserve"/>에 위임합니다.
    /// Col 범위 초과 입력(예: 2×3 그리드에서 Row=0, Col=3)이 별칭 버그(aliasing)를 유발하지 않도록
    /// 이 메서드에서 사전 차단합니다.
    /// </remarks>
    public (TicketStatus status, int slot) TryReserveByRowCol(TicketContext ctx, int row, int col)
    {
        // Row/Col 경계 검증: 부호 없는 비교로 음수도 단번에 거부 — 별칭 버그 방지
        if ((uint)row >= (uint)_rows || (uint)col >= (uint)_cols)
            return (TicketStatus.SeatTaken, -1);

        int seatId = row * _cols + col;
        return TryReserve(ctx, seatId);
    }

    /// <summary>
    /// 결제 성공 후 첫 번째 보유 슬롯을 <c>Sold</c>로 확정합니다(단일 좌석 하위호환).
    /// 배치 결제에는 <see cref="ConfirmAll"/>을 사용하세요.
    /// </summary>
    /// <param name="ctx">결제를 완료한 세션의 컨텍스트입니다.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Confirmed"/>, 슬롯): 성공 |
    /// (<see cref="TicketStatus.NotReserved"/>, -1): 예약 없음·중복 결제
    /// </returns>
    public (TicketStatus status, int slot) Confirm(TicketContext ctx)
    {
        Span<int> confirmed = stackalloc int[ctx.Slots.Length];
        int count = ConfirmAll(ctx, confirmed);
        return count == 0
            ? (TicketStatus.NotReserved, -1)
            : (TicketStatus.Confirmed, confirmed[0]);
    }

    /// <summary>
    /// 결제 실패 후 첫 번째 보유 슬롯을 <c>Free</c>로 반납합니다(단일 좌석 하위호환).
    /// 배치 반납에는 <see cref="ReleaseAll"/>을 사용하세요.
    /// </summary>
    /// <param name="ctx">결제에 실패한 세션의 컨텍스트입니다.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Released"/>, 슬롯): 성공 |
    /// (<see cref="TicketStatus.NotReserved"/>, -1): 예약 없음
    /// </returns>
    public (TicketStatus status, int slot) Release(TicketContext ctx)
    {
        Span<int> released = stackalloc int[ctx.Slots.Length];
        int count = ReleaseAll(ctx, released);
        return count == 0
            ? (TicketStatus.NotReserved, -1)
            : (TicketStatus.Released, released[0]);
    }

    /// <summary>
    /// 세션 이탈 시 보유 슬롯을 <c>Free</c>로 반납합니다(단일 좌석 하위호환).
    /// 배치 반납에는 <see cref="ReleaseAllByContext"/>를 사용하세요.
    /// </summary>
    /// <param name="ctx">슬롯을 반납할 세션의 컨텍스트입니다. <see langword="null"/>이면 no-op입니다.</param>
    public void ReleaseByContext(TicketContext? ctx) => ReleaseAllByContext(ctx);

    // ══════════════════════════════════════════════════════════════════════════
    // 좌석 상태 조회
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 현재 모든 좌석의 상태 스냅샷을 <paramref name="dest"/>에 기록합니다.
    /// </summary>
    /// <param name="dest">
    /// 상태를 기록할 대상 버퍼입니다. 길이는 <see cref="TotalTickets"/> 이상이어야 합니다.
    /// 기록되는 값: <c>0=Free, 1=Reserved, 2=Sold</c>.
    /// </param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 각 슬롯을 <c>Volatile.Read</c>로 읽어 최신 가시성을 보장합니다.
    /// 전체 배열에 걸친 원자적 일관성은 없습니다 — 스냅샷이므로 읽기 도중 다른 스레드가 상태를 바꿀 수 있습니다.
    /// <br/>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 호출 측에서 <c>stackalloc</c> 버퍼를 전달하면
    /// 이 메서드 내부에서 힙 할당이 발생하지 않습니다.
    /// </remarks>
    public void SnapshotStates(Span<byte> dest)
    {
        int n = Math.Min(dest.Length, _totalTickets);
        for (int i = 0; i < n; i++)
            // Volatile.Read: 스위퍼·TryReserveOne·ConfirmAll 스레드와의 가시성 교차 — 최신값 관찰
            dest[i] = (byte)Volatile.Read(ref _states[i]);
    }

    /// <summary>
    /// 좌석 상태를 <c>int[]</c>로 변환하여 반환합니다.
    /// <c>System.Text.Json</c>이 <c>byte[]</c>를 Base64 문자열로 직렬화하는 함정을 방지하기 위해
    /// JSON 직렬화 전 반드시 이 메서드를 사용하십시오.
    /// </summary>
    /// <returns>길이 <see cref="TotalTickets"/>의 <c>int[]</c>. 값: <c>0=Free, 1=Reserved, 2=Sold</c>.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 각 슬롯을 <c>Volatile.Read</c>로 읽습니다.
    /// <br/>
    /// <b>[Memory Allocation:]</b> <c>int[TotalTickets]</c> 1회 힙 할당. 저빈도 모니터링 경로 전용.
    /// </remarks>
    public int[] ProjectSeatStates()
    {
        var dest = new int[_totalTickets];
        for (int i = 0; i < _totalTickets; i++)
            // Volatile.Read: 최신 가시성 보장 — byte 캐스트 없이 직접 int로 읽어 int[] 반환
            dest[i] = Volatile.Read(ref _states[i]);
        return dest;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TTL 스위퍼
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TTL이 초과된 Reserved 슬롯을 스캔하여 자동 반납합니다.
    /// </summary>
    /// <returns>이번 스윕에서 반납된 슬롯 수입니다.</returns>
    /// <remarks>
    /// <para>
    /// <b>ABA 안전 설계:</b> 소유자의 <c>ctx.Slots</c>에서 seatId <c>i</c>를 가진 원소 entry를 탐색한 뒤
    /// <c>Interlocked.CompareExchange(ref owner.Slots[entry], -1, i)</c>로 소유권을 원자적으로 검증합니다.
    /// <br/>
    /// CAS 대상 값이 <c>i</c>(해당 seatId)와 정확히 일치할 때만 성공합니다.
    /// 소유자가 이미 Confirm·Release로 슬롯을 소비(-1)했거나, 새 좌석을 재예약(다른 seatId)한 경우
    /// CAS가 실패하여 no-op입니다 — ABA 방지.
    /// </para>
    /// <para>
    /// <b>배치 컨텍스트 지원:</b> <c>ctx.Slots</c>가 여러 원소를 가질 때도
    /// 특정 seatId <c>i</c>를 가진 원소 하나만 정확히 해제합니다.
    /// 다른 원소(다른 좌석)는 영향받지 않습니다.
    /// </para>
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
            // Interlocked.Read: x86 32비트에서 long torn-read 방지
            long reservedAt = Interlocked.Read(ref _reservedAtTicks[i]);
            if (now - reservedAt < _ttlTicks) continue;

            // 3. 소유자 참조 획득
            var owner = Volatile.Read(ref _owners[i]);
            if (owner is null) continue; // 이미 다른 경로가 정리 중 — 건너뜀

            // 4. ctx.Slots에서 seatId i를 가진 원소 탐색
            int slotEntry = -1;
            for (int k = 0; k < owner.Slots.Length; k++)
            {
                if (Volatile.Read(ref owner.Slots[k]) == i) { slotEntry = k; break; }
            }
            if (slotEntry < 0) continue; // 이미 다른 경로가 소비 — no-op

            // 5. ABA 안전 CAS: owner.Slots[slotEntry] == i인 경우에만 -1로 교체
            //    소유자가 이미 Confirm/Release로 슬롯을 소비(-1)했거나
            //    새 좌석을 재예약(!=i)했으면 CAS 실패 → no-op
            if (Interlocked.CompareExchange(ref owner.Slots[slotEntry], -1, i) != i) continue;

            // 6. CAS 성공: 스위퍼가 단독으로 슬롯 정리
            Debug.Assert(Volatile.Read(ref _states[i]) == Reserved,
                $"SweepExpired: 슬롯 {i} CAS 성공 후 Reserved 상태가 아님 — 예상치 못한 상태 전이");
            // _owners 먼저 null: _states가 아직 Reserved이므로 신규 TryReserveOne이 불가 — null 쓰기 안전
            Volatile.Write(ref _owners[i], null);
            Interlocked.Exchange(ref _states[i], Free);
            released++;
            // TTL 만료 반납 완료: 만료 누적 카운터 증가
            Interlocked.Increment(ref _totalExpired);
        }

        return released;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 모니터링
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 모니터링용 지표 스냅샷(<see cref="TicketMetrics"/>)을 반환합니다.
    /// </summary>
    /// <returns>
    /// 현재 Free/Reserved/Sold 좌석 수와 누적 이벤트 카운터를 담은 <see cref="TicketMetrics"/> 구조체입니다.
    /// </returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Thread Safety:</b> Thread-safe. 슬롯 상태는 <c>Volatile.Read</c>,
    /// 누적 카운터는 <c>Interlocked.Read</c>로 읽어 가시성을 보장합니다.
    /// </description></item>
    /// <item><description>
    /// <b>Memory Allocation:</b> Zero-allocation.
    /// <see cref="TicketMetrics"/>는 stack-allocated readonly record struct이므로 힙 할당 없음.
    /// </description></item>
    /// <item><description>
    /// <b>비원자성 경고:</b> 좌석 상태 스캔(free/reserved/sold)과 누적 카운터 읽기는 서로 원자적이지 않습니다.
    /// 모니터링 목적으로만 사용하며,
    /// <c>Reserved == TotalReserved - TotalConfirmed - …</c> 같은 파생 불변식을 단언하지 마십시오.
    /// </description></item>
    /// </list>
    /// </remarks>
    public TicketMetrics MetricsSnapshot()
    {
        int free = 0, reserved = 0, sold = 0;
        for (int i = 0; i < _totalTickets; i++)
        {
            // Volatile.Read: 최신 가시성 보장 — 완전한 원자적 일관성은 없지만 모니터링 스냅샷으로 충분
            switch (Volatile.Read(ref _states[i]))
            {
                case Free:     free++;     break;
                case Reserved: reserved++; break;
                case Sold:     sold++;     break;
            }
        }
        return new TicketMetrics(
            Rows:               _rows,
            Cols:               _cols,
            Total:              _totalTickets,
            Free:               free,
            Reserved:           reserved,
            Sold:               sold,
            // Interlocked.Read: x86 32비트에서 long torn-read 방지
            TotalReserved:      Interlocked.Read(ref _totalReserved),
            TotalConfirmed:     Interlocked.Read(ref _totalConfirmed),
            TotalPaymentFailed: Interlocked.Read(ref _totalPaymentFailed),
            TotalAbandoned:     Interlocked.Read(ref _totalAbandoned),
            TotalExpired:       Interlocked.Read(ref _totalExpired),
            TotalSeatTaken:     Interlocked.Read(ref _totalSeatTaken));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 내부 헬퍼
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>ctx.Slots에서 첫 번째 보유 슬롯 값을 반환합니다. 없으면 -1.</summary>
    private static int FirstHeldSlot(TicketContext ctx)
    {
        for (int k = 0; k < ctx.Slots.Length; k++)
        {
            int v = Volatile.Read(ref ctx.Slots[k]);
            if (v >= 0) return v;
        }
        return -1;
    }
}

/// <summary>
/// <see cref="TicketInventory.MetricsSnapshot"/>이 반환하는 모니터링용 지표 스냅샷입니다.
/// 현재 상태별 좌석 수(Free/Reserved/Sold)와 누적 이벤트 카운터를 포함합니다.
/// </summary>
/// <remarks>
/// <b>비원자성:</b> 누적 카운터와 현재 상태 스캔은 서로 원자적이지 않습니다.
/// 모니터링 용도로 충분하나, <c>Reserved == TotalReserved - TotalConfirmed - …</c> 같은
/// 파생 불변식을 표시하거나 단언하지 마십시오 — 부하 중 어느 순간에도 성립하지 않습니다.
/// </remarks>
public readonly record struct TicketMetrics(
    int Rows, int Cols, int Total,
    int Free, int Reserved, int Sold,
    long TotalReserved, long TotalConfirmed,
    long TotalPaymentFailed, long TotalAbandoned,
    long TotalExpired, long TotalSeatTaken);
