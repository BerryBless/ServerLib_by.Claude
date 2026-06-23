using System.Diagnostics;
using ServerLib.Core.Serialization.Packets;

namespace Ticketing;

/// <summary>
/// lock-free 방식으로 2D 좌석 배치 티켓 재고를 관리합니다.
/// <para>슬롯 상태 전이: <c>Free(0) → Reserved(1) → Sold(2)</c></para>
/// <para>실패 시: <c>Reserved(1) → Free(0)</c> (결제 실패·이탈·TTL)</para>
/// <para>좌석 지정: 클라이언트가 <c>Row/Col</c>로 좌석을 지정하면 <c>seatId = Row * Cols + Col</c>로 평면화됩니다.</para>
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

    private readonly int  _rows;          // 행 수 (2D 좌석 표현)
    private readonly int  _cols;          // 열 수 (seatId = row*_cols + col)
    private readonly int  _totalTickets;  // 슬롯 배열 경계값 = _rows * _cols
    // long: Stopwatch.GetTimestamp() delta로 TTL 판단. Stopwatch 틱은 커널 전환 없는 단조 시계(시스템 시각 변경에 무관)
    private readonly long _ttlTicks;

    // ── 누적 이벤트 카운터 ─────────────────────────────────────────────────────
    // long: Interlocked.Increment(ref long)은 CPU LOCK XADD 명령으로 힙 할당 없이 원자 증가.
    // 읽기 시 Interlocked.Read(ref long) 필수 — x86 32비트에서 long torn-read 방지.
    private long _totalReserved;      // TryReserve CAS 성공 건수 (예약 누적)
    private long _totalSeatTaken;     // TryReserve CAS 실패 건수 (좌석 경합, 범위 오류 제외)
    private long _totalConfirmed;     // Confirm 성공 건수 (결제 확정 누적)
    private long _totalPaymentFailed; // Release 성공 건수 (결제 실패 반납 누적)
    private long _totalAbandoned;     // ReleaseByContext 실제 반납 건수 (이탈·연결 해제 누적)
    private long _totalExpired;       // SweepExpired 반납 건수 (TTL 만료 누적)

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

    /// <summary>
    /// 클라이언트가 지정한 좌석(<paramref name="seatId"/>)을 예약합니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션의 컨텍스트입니다.</param>
    /// <param name="seatId">
    /// 예약할 좌석의 평면 인덱스입니다(<c>seatId = row * Cols + col</c>).
    /// 유효 범위: <c>0 ≤ seatId &lt; TotalTickets</c>.
    /// </param>
    /// <returns>
    /// (<see cref="TicketStatus.Reserved"/>, <paramref name="seatId"/>): 성공 |
    /// (<see cref="TicketStatus.AlreadyReserved"/>, 기존 슬롯): 이미 예약 중 |
    /// (<see cref="TicketStatus.SeatTaken"/>, seatId or -1): 좌석 점유됨 또는 범위 밖
    /// </returns>
    /// <remarks>
    /// <b>직렬 세션 디스패치 가정</b>: 동일 세션에서 두 TicketReserveRequest는 절대 동시에 실행되지 않습니다
    /// (<c>SocketPipelineSession.ReadPipeAsync</c>가 이전 <see langword="await"/>가 완료된 후 다음 패킷을 처리).
    /// 이 가정 아래 <c>ctx.SlotIndex ≥ 0</c> 가드는 check-then-act이어도 안전합니다.
    /// <br/>
    /// <b>SeatTaken 처리:</b> 클라이언트는 <see cref="SeatMapRequestPacket"/>으로 좌석맵을 재조회한 뒤
    /// 다른 빈 좌석을 선택하고 재시도해야 합니다.
    /// </remarks>
    public (TicketStatus status, int slot) TryReserve(TicketContext ctx, int seatId)
    {
        // 직렬 디스패치 가정 아래 안전한 중복 예약 가드
        if (Volatile.Read(ref ctx.SlotIndex) >= 0)
            return (TicketStatus.AlreadyReserved, ctx.SlotIndex);

        // 좌석 범위 검증: 유효 범위 밖이면 SeatTaken(-1)
        if ((uint)seatId >= (uint)_totalTickets)
            return (TicketStatus.SeatTaken, -1);

        long nowTicks = Stopwatch.GetTimestamp();

        // CAS: Free(0) → Reserved(1). 성공하면 이 스레드가 해당 좌석을 점유한 유일한 예약자.
        // 다른 스레드가 먼저 CAS에 성공하면 이 CAS는 실패(반환값 != Free) → SeatTaken 반환.
        if (Interlocked.CompareExchange(ref _states[seatId], Reserved, Free) != Free)
        {
            // CAS 실패 = 좌석 경합: 다른 스레드가 먼저 예약 성공 — 경합 카운터만 증가(범위 오류는 카운트 안 함)
            Interlocked.Increment(ref _totalSeatTaken);
            return (TicketStatus.SeatTaken, seatId);
        }

        // 소유자 참조 기록: Volatile.Write로 스위퍼 스레드에 가시성 보장(발행 순서 중요)
        Volatile.Write(ref _owners[seatId], ctx);
        Volatile.Write(ref _reservedAtTicks[seatId], nowTicks);
        // SlotIndex를 마지막에 발행: 이 쓰기가 관찰되면 위 두 쓰기도 이미 완료된 것이 보장됨
        Volatile.Write(ref ctx.SlotIndex, seatId);
        // CAS 성공 = 예약 완료: 예약 누적 카운터 증가
        Interlocked.Increment(ref _totalReserved);
        return (TicketStatus.Reserved, seatId);
    }

    /// <summary>
    /// 클라이언트가 지정한 Row/Col 좌표로 좌석을 예약합니다.
    /// Row/Col→seatId 변환과 경계 검증이 이 메서드에 캡슐화되므로 호출자는 변환 로직을 구현하지 않아야 합니다.
    /// </summary>
    /// <param name="ctx">예약을 시도하는 세션의 컨텍스트입니다.</param>
    /// <param name="row">좌석 행 인덱스입니다(0-based). 유효 범위: <c>0 ≤ row &lt; Rows</c>.</param>
    /// <param name="col">좌석 열 인덱스입니다(0-based). 유효 범위: <c>0 ≤ col &lt; Cols</c>.</param>
    /// <returns>
    /// (<see cref="TicketStatus.Reserved"/>, seatId): 성공 |
    /// (<see cref="TicketStatus.AlreadyReserved"/>, 기존 슬롯): 이미 예약 중 |
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
    /// <br/>
    /// <b>[Span&lt;byte&gt; 소유권 및 생명주기:]</b> <paramref name="dest"/>는 호출자가 소유합니다.
    /// 이 메서드가 반환된 후에도 호출자는 <paramref name="dest"/>를 안전하게 읽을 수 있습니다.
    /// <c>stackalloc</c>으로 할당한 경우 해당 스택 프레임이 유효한 동안에만 접근 가능하며,
    /// <see langword="await"/> 경계를 넘겨 전달하면 안 됩니다(비동기 메서드에서 stackalloc Span은 await 후 무효).
    /// <br/>
    /// <b>[용도:]</b> <see cref="SeatMapResponsePacket"/> 직렬화 전에 서버가 호출합니다.
    /// </remarks>
    public void SnapshotStates(Span<byte> dest)
    {
        int n = Math.Min(dest.Length, _totalTickets);
        for (int i = 0; i < n; i++)
            // Volatile.Read: 스위퍼·TryReserve·Confirm 스레드와의 가시성 교차 — 최신값 관찰
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
    /// 배열 전체의 원자적 일관성은 없습니다 — 모니터링 스냅샷 전용.
    /// <br/>
    /// <b>[Memory Allocation:]</b> <c>int[TotalTickets]</c> 1회 힙 할당. 저빈도 모니터링 경로 전용.
    /// hot path에서는 <see cref="SnapshotStates(Span{byte})"/> + stackalloc 조합을 사용하십시오.
    /// </remarks>
    public int[] ProjectSeatStates()
    {
        var dest = new int[_totalTickets];
        for (int i = 0; i < _totalTickets; i++)
            // Volatile.Read: 최신 가시성 보장 — byte 캐스트 없이 직접 int로 읽어 int[] 반환
            dest[i] = Volatile.Read(ref _states[i]);
        return dest;
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
        // 결제 확정 완료: 확정 누적 카운터 증가
        Interlocked.Increment(ref _totalConfirmed);
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
        // 결제 실패 반납 완료: 결제실패 누적 카운터 증가
        Interlocked.Increment(ref _totalPaymentFailed);
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
        // 이탈·연결 해제로 인한 반납 완료: 이탈 누적 카운터 증가
        Interlocked.Increment(ref _totalAbandoned);
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
            // TTL 만료 반납 완료: 만료 누적 카운터 증가
            Interlocked.Increment(ref _totalExpired);
        }

        return released;
    }

    /// <summary>
    /// 모니터링용 지표 스냅샷(<see cref="TicketMetrics"/>)을 반환합니다.
    /// 현재 좌석 상태를 1-pass로 스캔하고 누적 카운터를 원자적으로 읽습니다.
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
    /// <b>Blocking:</b> Non-blocking.
    /// </description></item>
    /// <item><description>
    /// <b>비원자성 경고:</b> 좌석 상태 스캔(free/reserved/sold)과 누적 카운터 읽기는 서로 원자적이지 않습니다.
    /// 모니터링 목적으로만 사용하며,
    /// <c>Reserved == TotalReserved - TotalConfirmed - …</c> 같은 파생 불변식을 표시하거나 단언하지 마십시오.
    /// 부하 중 어느 순간에도 성립하지 않습니다.
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
            // Interlocked.Read: x86 32비트에서 long torn-read 방지 — 64비트에서도 일관성 명시적 보장
            TotalReserved:      Interlocked.Read(ref _totalReserved),
            TotalConfirmed:     Interlocked.Read(ref _totalConfirmed),
            TotalPaymentFailed: Interlocked.Read(ref _totalPaymentFailed),
            TotalAbandoned:     Interlocked.Read(ref _totalAbandoned),
            TotalExpired:       Interlocked.Read(ref _totalExpired),
            TotalSeatTaken:     Interlocked.Read(ref _totalSeatTaken));
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
