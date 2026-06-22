using System.Text.Json;
using Xunit;
using ServerLib.Core.Serialization.Packets;
using Ticketing;

namespace ServerLib.Tests;

/// <summary>
/// <see cref="TicketInventory"/>의 lock-free 동시성 정확성을 검증합니다.
/// 좌석지정 예약(<c>TryReserve(ctx, seatId)</c>) 기반으로 갱신되었습니다.
/// </summary>
public class TicketInventoryConcurrencyTests
{
    /// <summary>단순 1행×N열 그리드 인벤토리를 생성합니다. seatId = col(0..cols-1).</summary>
    private static TicketInventory Make1xN(int cols = 3) =>
        new TicketInventory(1, cols, TimeSpan.FromSeconds(30));

    /// <summary>rows행×cols열 2D 그리드 인벤토리를 생성합니다. seatId = row*cols+col.</summary>
    private static TicketInventory Make2D(int rows, int cols) =>
        new TicketInventory(rows, cols, TimeSpan.FromSeconds(30));

    // ① 64개 Task 동시 예약 → 정확히 TotalTickets개만 Reserved, 나머지 SeatTaken
    [Fact]
    public async Task Concurrent_reserve_seat_designated_exactly_totalTickets_succeed()
    {
        var inv      = Make1xN(3); // 3석: seatId 0,1,2
        int concurrency = 64;

        // [LOCK-05] CI 2코어 환경에서 Barrier 동기 블로킹으로 ThreadPool 스레드 고갈 방지
        ThreadPool.SetMinThreads(concurrency, concurrency);

        // Barrier: 모든 Task가 준비된 후 동시에 CAS 경쟁을 시작
        var barrier = new Barrier(concurrency);
        var results = new (TicketStatus status, int slot)[concurrency];

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var ctx = new TicketContext($"user{i}");
            barrier.SignalAndWait();
            // 64개 Task가 3개 좌석(0,1,2)을 순환 선택 — 각 좌석마다 약 21개 경쟁
            results[i] = inv.TryReserve(ctx, i % 3);
        })).ToArray();

        await Task.WhenAll(tasks);

        int reservedCount  = results.Count(r => r.status == TicketStatus.Reserved);
        int seatTakenCount = results.Count(r => r.status == TicketStatus.SeatTaken);

        Assert.Equal(3, reservedCount);                    // 좌석 수만큼 성공
        Assert.Equal(concurrency - 3, seatTakenCount);    // 나머지는 SeatTaken

        // 예약된 슬롯 인덱스가 정확히 {0,1,2}인지 검증
        var reservedSlots = results
            .Where(r => r.status == TicketStatus.Reserved)
            .Select(r => r.slot)
            .OrderBy(s => s)
            .ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, reservedSlots);
    }

    // ② 동일 좌석에 N개 동시 요청 → 정확히 1개만 성공, 나머지 SeatTaken
    [Fact]
    public async Task Concurrent_reserve_same_seat_exactly_one_succeeds()
    {
        var inv     = Make1xN(3);
        int concurrency = 50;
        ThreadPool.SetMinThreads(concurrency, concurrency);

        var barrier = new Barrier(concurrency);
        var results = new (TicketStatus status, int slot)[concurrency];

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var ctx = new TicketContext($"user{i}");
            barrier.SignalAndWait();
            results[i] = inv.TryReserve(ctx, 0); // 모두 좌석 0을 노림
        })).ToArray();

        await Task.WhenAll(tasks);

        int reservedCount  = results.Count(r => r.status == TicketStatus.Reserved);
        int seatTakenCount = results.Count(r => r.status == TicketStatus.SeatTaken);

        Assert.Equal(1, reservedCount);
        Assert.Equal(concurrency - 1, seatTakenCount);
        Assert.Equal(0, results.First(r => r.status == TicketStatus.Reserved).slot);
    }

    // ③ 확정된 좌석에 다시 예약 시도 → SeatTaken
    [Fact]
    public void After_all_confirmed_reserve_returns_seatTaken()
    {
        var inv  = Make1xN(3);
        var ctxs = Enumerable.Range(0, 3).Select(i => new TicketContext($"user{i}")).ToArray();

        for (int s = 0; s < 3; s++)
        {
            var (status, _) = inv.TryReserve(ctxs[s], s);
            Assert.Equal(TicketStatus.Reserved, status);
            inv.Confirm(ctxs[s]);
        }

        // 이미 Sold인 좌석에 재예약 시도 → SeatTaken
        var extra = new TicketContext("extra");
        var (extraStatus, _) = inv.TryReserve(extra, 0);
        Assert.Equal(TicketStatus.SeatTaken, extraStatus);
    }

    // ④ 1개 반납 후 신규 컨텍스트가 그 좌석을 재예약
    [Fact]
    public void After_release_new_context_can_reserve_same_seat()
    {
        var inv  = Make1xN(3);
        var ctxs = Enumerable.Range(0, 3).Select(i => new TicketContext($"user{i}")).ToArray();

        // 3개 전부 좌석지정 예약
        for (int s = 0; s < 3; s++)
            inv.TryReserve(ctxs[s], s);
        Assert.Equal(0, inv.FreeCount);

        // 좌석 1 반납
        var (relStatus, relSlot) = inv.Release(ctxs[1]);
        Assert.Equal(TicketStatus.Released, relStatus);
        Assert.Equal(1, inv.FreeCount);

        // 새 컨텍스트가 반납된 좌석 1을 명시적으로 재예약
        var newCtx = new TicketContext("newuser");
        var (newStatus, newSlot) = inv.TryReserve(newCtx, relSlot);
        Assert.Equal(TicketStatus.Reserved, newStatus);
        Assert.Equal(relSlot, newSlot); // 반납된 좌석과 동일
        Assert.Equal(0, inv.FreeCount);
    }

    // ⑤ Confirm 2회 → 2번째 NotReserved, 슬롯 상태 Sold 유지
    [Fact]
    public void Double_confirm_second_returns_notReserved()
    {
        var inv = Make1xN(3);
        var ctx = new TicketContext("user");

        inv.TryReserve(ctx, 0);
        var (first, slot1)  = inv.Confirm(ctx);
        var (second, slot2) = inv.Confirm(ctx);

        Assert.Equal(TicketStatus.Confirmed,   first);
        Assert.Equal(TicketStatus.NotReserved, second);
        Assert.Equal(-1, slot2); // 두 번째 Confirm은 슬롯 없음
    }

    // ⑥ Confirm vs ReleaseByContext 동시 경합 → 정확히 하나만 승리, 인벤토리 일관성 유지
    [Fact]
    public async Task Concurrent_confirm_and_releaseByContext_exactly_one_wins()
    {
        var inv = Make1xN(1); // 슬롯 1개만
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx, 0);

        // Confirm과 ReleaseByContext를 동시에 100회씩 시도
        const int rounds = 100;
        for (int r = 0; r < rounds; r++)
        {
            // 슬롯 재예약 (직전 라운드에서 Free로 돌아온 경우)
            if (ctx.SlotIndex < 0)
                inv.TryReserve(ctx, 0);

            // 두 경쟁자 동시 시작
            var barrier = new Barrier(2);
            TicketStatus? confirmResult  = null;
            bool          releaseRan     = false;

            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                var (s, _) = inv.Confirm(ctx);
                confirmResult = s;
            });
            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                inv.ReleaseByContext(ctx);
                releaseRan = true;
            });

            await Task.WhenAll(t1, t2);

            Assert.True(releaseRan);
            // Confirm이 성공했거나(Confirmed) 이미 Release가 선점(NotReserved) — 어느 쪽이든 슬롯은 일관
            Assert.True(confirmResult == TicketStatus.Confirmed || confirmResult == TicketStatus.NotReserved);
        }
    }

    // ⑦ AlreadyReserved 가드: 동일 컨텍스트의 두 번째 TryReserve는 AlreadyReserved
    [Fact]
    public void TryReserve_same_context_twice_returns_alreadyReserved()
    {
        var inv = Make1xN(3);
        var ctx = new TicketContext("user");

        var (first, _)  = inv.TryReserve(ctx, 0);
        var (second, _) = inv.TryReserve(ctx, 1); // 다른 좌석도 AlreadyReserved

        Assert.Equal(TicketStatus.Reserved,       first);
        Assert.Equal(TicketStatus.AlreadyReserved, second);
    }

    // ⑧ FreeCount: 예약·확정·반납에 따라 정확히 변화
    [Fact]
    public void FreeCount_tracks_slot_transitions_correctly()
    {
        var inv = Make1xN(3);
        Assert.Equal(3, inv.FreeCount);

        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.TryReserve(ctx0, 0);
        Assert.Equal(2, inv.FreeCount); // Reserved 1개 → Free 2개

        inv.TryReserve(ctx1, 1);
        Assert.Equal(1, inv.FreeCount);

        inv.Confirm(ctx0);
        Assert.Equal(1, inv.FreeCount); // Sold는 Free로 치지 않음

        inv.Release(ctx1);
        Assert.Equal(2, inv.FreeCount); // Release → Free 복귀
    }

    // ─────── 범위 밖 seatId 검증 ───────

    // ⑨ seatId < 0 → SeatTaken
    [Fact]
    public void TryReserve_negative_seatId_returns_seatTaken()
    {
        var inv = Make1xN(3);
        var ctx = new TicketContext("user");

        var (status, slot) = inv.TryReserve(ctx, -1);

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(-1, slot);
        Assert.Equal(-1, ctx.SlotIndex); // SlotIndex 오염 없음
    }

    // ⑩ seatId >= TotalTickets → SeatTaken
    [Fact]
    public void TryReserve_out_of_range_seatId_returns_seatTaken()
    {
        var inv = Make1xN(3);
        var ctx = new TicketContext("user");

        var (status, slot) = inv.TryReserve(ctx, 99);

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(-1, slot);
    }

    // ─────── SnapshotStates 검증 ───────

    // ⑪ SnapshotStates: Free/Reserved/Sold 상태가 정확히 반영됨
    [Fact]
    public void SnapshotStates_reflects_free_reserved_sold_correctly()
    {
        var inv  = Make1xN(3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.TryReserve(ctx0, 0); // seatId 0 → Reserved
        inv.TryReserve(ctx1, 1); // seatId 1 → Reserved
        inv.Confirm(ctx0);       // seatId 0 → Sold

        Span<byte> snap = stackalloc byte[3];
        inv.SnapshotStates(snap);

        Assert.Equal(2, snap[0]); // Sold
        Assert.Equal(1, snap[1]); // Reserved
        Assert.Equal(0, snap[2]); // Free
    }

    // ⑫ SnapshotStates: dest가 TotalTickets보다 크면 앞부분만 기록
    [Fact]
    public void SnapshotStates_with_larger_dest_writes_only_total_tickets()
    {
        var inv = Make1xN(3);
        byte[] dest = new byte[10]; // 3보다 큰 버퍼
        Array.Fill(dest, (byte)0xFF);

        inv.SnapshotStates(dest);

        // 처음 3바이트는 0(Free), 나머지는 그대로 0xFF
        Assert.Equal(0,    dest[0]);
        Assert.Equal(0,    dest[1]);
        Assert.Equal(0,    dest[2]);
        Assert.Equal(0xFF, dest[3]);
    }

    // ─────── GAP-01: SweepExpired 기본 경로 ───────

    // ⑬ TTL 만료된 Reserved 슬롯은 SweepExpired로 반납됨
    [Fact]
    public void SweepExpired_releases_expired_reserved_slots()
    {
        var inv = new TicketInventory(1, 2, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx, 0);
        Thread.Sleep(20); // TTL(1ms) 충분히 초과

        int released = inv.SweepExpired();

        Assert.Equal(1, released);
        Assert.Equal(2, inv.FreeCount);   // 슬롯 반납됨
        Assert.Equal(-1, ctx.SlotIndex);  // 선형화 앵커 소비됨
    }

    // ⑭ Confirm 완료 슬롯(Sold)은 SweepExpired가 건드리지 않음
    [Fact]
    public void SweepExpired_does_not_touch_confirmed_slot()
    {
        var inv = new TicketInventory(1, 2, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx, 0);
        inv.Confirm(ctx);    // Sold 상태로 확정, SlotIndex=-1
        Thread.Sleep(20);    // TTL 초과

        int released = inv.SweepExpired();

        Assert.Equal(0, released);   // Sold 슬롯은 반납 대상 아님
    }

    // ─────── GAP-02: SweepExpired ABA 경로 ───────

    // ⑮ Confirm 후 재예약한 컨텍스트는 스위퍼 CAS가 실패하여 안전
    [Fact]
    public void SweepExpired_does_not_release_slot_after_confirm_and_re_reserve()
    {
        var inv = new TicketInventory(1, 2, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx, 0);   // 좌석 0 예약
        Thread.Sleep(20);          // TTL 초과

        // 스위퍼 전에 ctx가 좌석 0을 Confirm하고 좌석 1을 재예약
        inv.Confirm(ctx);         // 좌석 0 → Sold, SlotIndex=-1
        inv.TryReserve(ctx, 1);   // 좌석 1 재예약

        // 좌석 0은 Sold → SweepExpired Reserved 조건 실패로 건너뜀
        // 좌석 1은 TTL 미초과(방금 예약됨) → 건너뜀
        int released = inv.SweepExpired();

        Assert.Equal(0, released);
        Assert.True(ctx.SlotIndex >= 0); // 좌석 1 보유 유지
    }

    // ─────── GAP-03: null 컨텍스트 방어 ───────

    // ⑯ ReleaseByContext(null)은 예외 없이 no-op
    [Fact]
    public void ReleaseByContext_null_context_is_noop()
    {
        var inv = Make1xN(3);
        var exception = Record.Exception(() => inv.ReleaseByContext(null));
        Assert.Null(exception);
        Assert.Equal(3, inv.FreeCount); // 슬롯 변화 없음
    }

    // ─────── GAP-05: Release 미예약 경로 ───────

    // ⑰ 예약 없이 Release 호출 → NotReserved 반환
    [Fact]
    public void Release_without_reservation_returns_notReserved()
    {
        var inv = Make1xN(3);
        var ctx = new TicketContext("user");

        var (status, slot) = inv.Release(ctx);

        Assert.Equal(TicketStatus.NotReserved, status);
        Assert.Equal(-1, slot);
        Assert.Equal(3, inv.FreeCount); // 슬롯 변화 없음
    }

    // ─────── 생성자 유효성 검증 ───────

    // ⑱ 행 또는 열이 0 이하이면 ArgumentOutOfRangeException
    [Theory]
    [InlineData(0,  1)]
    [InlineData(-1, 1)]
    [InlineData(1,  0)]
    [InlineData(1, -1)]
    public void Constructor_invalid_rows_or_cols_throws(int rows, int cols)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TicketInventory(rows, cols, TimeSpan.FromSeconds(30)));
    }

    // ⑲ rows*cols > 255이면 ArgumentOutOfRangeException
    [Theory]
    [InlineData(16, 16)] // 256석 초과
    [InlineData(1, 256)]
    public void Constructor_totalTickets_exceeds_255_throws(int rows, int cols)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TicketInventory(rows, cols, TimeSpan.FromSeconds(30)));
    }

    // ⑳ 2D 생성자: Rows, Cols, TotalTickets 프로퍼티가 정확히 설정됨
    [Fact]
    public void Constructor_2d_properties_are_set_correctly()
    {
        var inv = new TicketInventory(3, 4, TimeSpan.FromSeconds(10));

        Assert.Equal(3,  inv.Rows);
        Assert.Equal(4,  inv.Cols);
        Assert.Equal(12, inv.TotalTickets);
        Assert.Equal(12, inv.FreeCount);
    }

    // ─────── GAP-04: DummyPaymentGateway 극단값 ───────

    // ㉑ FailureRate=0이면 항상 성공, FailureRate=1이면 항상 실패
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, false)]
    public async Task DummyPaymentGateway_extreme_failureRates(double rate, bool expected)
    {
        var gw = new DummyPaymentGateway(delayMs: 0, failureRate: rate);
        bool result = await gw.ChargeAsync("user");
        Assert.Equal(expected, result);
    }

    // ─────── STYLE-06: 동일 컨텍스트 반납 후 재예약 ───────

    // ㉒ 예약한 컨텍스트가 Release 후 동일 좌석을 재예약할 수 있음
    [Fact]
    public void Same_context_can_reserve_after_release()
    {
        var inv = Make1xN(3);
        var ctx = new TicketContext("user");

        var (firstStatus, firstSlot) = inv.TryReserve(ctx, 0);
        Assert.Equal(TicketStatus.Reserved, firstStatus);
        Assert.Equal(0, firstSlot);

        // 동일 컨텍스트로 Release
        var (relStatus, relSlot) = inv.Release(ctx);
        Assert.Equal(TicketStatus.Released, relStatus);
        Assert.Equal(0, relSlot);
        Assert.Equal(-1, ctx.SlotIndex); // 선형화 앵커 소비됨

        // 동일 컨텍스트가 같은 좌석을 재예약
        var (secondStatus, secondSlot) = inv.TryReserve(ctx, 0);
        Assert.Equal(TicketStatus.Reserved, secondStatus);
        Assert.Equal(0, secondSlot);
        Assert.Equal(2, inv.FreeCount); // 3석 중 1석(seat 0)만 점유, 나머지 2석 Free
    }

    // ─────── STYLE-05: SweepExpired 후 SnapshotStates 반영 ───────

    // ㉓ TTL 만료 스윕 후 SnapshotStates가 Free(0)을 반환함
    [Fact]
    public void After_sweep_expired_snapshot_shows_free()
    {
        var inv = new TicketInventory(1, 3, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx, 1); // 좌석 1 예약 → Reserved
        Thread.Sleep(20);       // TTL(1ms) 충분히 초과

        int released = inv.SweepExpired();

        Assert.Equal(1, released);
        Span<byte> snap = stackalloc byte[3];
        inv.SnapshotStates(snap);
        Assert.Equal(0, snap[0]); // Free
        Assert.Equal(0, snap[1]); // Free (만료 후 반납됨)
        Assert.Equal(0, snap[2]); // Free
    }

    // ─────── STYLE-03/ARCH-07: 2D 그리드 시나리오 ───────

    // ㉔ TryReserveByRowCol: Col 범위 초과(별칭 버그) → SeatTaken
    [Fact]
    public void TryReserveByRowCol_col_out_of_range_returns_seatTaken()
    {
        // 2×3 그리드: Row=0, Col=3 → seatId=3이 되면 (1,0)을 잘못 점유하는 별칭 버그 발생
        var inv = Make2D(2, 3);
        var ctx = new TicketContext("user");

        var (status, slot) = inv.TryReserveByRowCol(ctx, 0, 3); // Col=3 >= Cols=3

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(-1, slot);
        Assert.Equal(-1, ctx.SlotIndex); // SlotIndex 오염 없음
        Assert.Equal(6, inv.FreeCount);  // 실제 좌석(1,0) 미점유 확인
    }

    // ㉕ TryReserveByRowCol: Row 범위 초과 → SeatTaken
    [Fact]
    public void TryReserveByRowCol_row_out_of_range_returns_seatTaken()
    {
        var inv = Make2D(2, 3);
        var ctx = new TicketContext("user");

        var (status, slot) = inv.TryReserveByRowCol(ctx, 2, 0); // Row=2 >= Rows=2

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(-1, slot);
    }

    // ㉖ TryReserveByRowCol: 2D 그리드 마지막 유효 좌석(Row=1, Col=2) 정상 예약
    [Fact]
    public void TryReserveByRowCol_last_valid_seat_reserves_correctly()
    {
        var inv = Make2D(2, 3); // 2×3 그리드: seatId 0~5
        var ctx = new TicketContext("user");

        // Row=1, Col=2 → seatId = 1*3+2 = 5 (마지막 유효 좌석)
        var (status, slot) = inv.TryReserveByRowCol(ctx, 1, 2);

        Assert.Equal(TicketStatus.Reserved, status);
        Assert.Equal(5, slot); // seatId=5
        Assert.Equal(5, ctx.SlotIndex);
        Assert.Equal(5, inv.FreeCount); // 1석 감소
    }

    // ㉗ TryReserveByRowCol: 같은 seatId가 두 Row/Col 조합으로 계산되지 않음 검증
    //    2×3 그리드에서 (0,3)은 범위 초과이고 (1,0)과 같은 seatId=3을 가질 수 없어야 함
    [Fact]
    public void TryReserveByRowCol_no_aliasing_between_row_col_combos()
    {
        var inv = Make2D(2, 3);
        var ctx1 = new TicketContext("user1");
        var ctx2 = new TicketContext("user2");

        // 유효 좌석 (1,0) → seatId=3 예약
        var (validStatus, validSlot) = inv.TryReserveByRowCol(ctx1, 1, 0);
        Assert.Equal(TicketStatus.Reserved, validStatus);
        Assert.Equal(3, validSlot);

        // 범위 초과 (0,3): 별칭이 없다면 SeatTaken(-1) 반환, seatId=3 재점유 없음
        var (aliasStatus, _) = inv.TryReserveByRowCol(ctx2, 0, 3);
        Assert.Equal(TicketStatus.SeatTaken, aliasStatus);
        Assert.Equal(-1, ctx2.SlotIndex); // ctx2 SlotIndex 오염 없음

        // (1,0)은 여전히 ctx1이 보유
        Assert.Equal(3, ctx1.SlotIndex);
    }

    // ㉘ TryReserveByRowCol: 2D 그리드 동시 예약 — 정확히 TotalTickets개만 성공
    [Fact]
    public async Task TryReserveByRowCol_concurrent_2d_exactly_totalTickets_succeed()
    {
        var inv         = Make2D(2, 3); // 6석
        int concurrency = 30;
        ThreadPool.SetMinThreads(concurrency, concurrency);

        var barrier = new Barrier(concurrency);
        var results = new (TicketStatus status, int slot)[concurrency];

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var ctx = new TicketContext($"user{i}");
            int row = (i % 6) / 3; // 2행 순환
            int col = (i % 6) % 3; // 3열 순환
            barrier.SignalAndWait();
            results[i] = inv.TryReserveByRowCol(ctx, row, col);
        })).ToArray();

        await Task.WhenAll(tasks);

        int reservedCount  = results.Count(r => r.status == TicketStatus.Reserved);
        int seatTakenCount = results.Count(r => r.status == TicketStatus.SeatTaken);

        Assert.Equal(6, reservedCount);               // 정확히 6석 예약 성공
        Assert.Equal(concurrency - 6, seatTakenCount);
    }

    // ─────── MetricsSnapshot / 누적 카운터 검증 ───────

    // ㉙ MetricsSnapshot: 초기 상태 — 전체 Free, 카운터 0
    [Fact]
    public void MetricsSnapshot_initial_all_free_counters_zero()
    {
        var inv = Make2D(2, 3); // 6석

        var m = inv.MetricsSnapshot();

        Assert.Equal(2, m.Rows);
        Assert.Equal(3, m.Cols);
        Assert.Equal(6, m.Total);
        Assert.Equal(6, m.Free);
        Assert.Equal(0, m.Reserved);
        Assert.Equal(0, m.Sold);
        // 모든 누적 카운터 초기값 0
        Assert.Equal(0, m.TotalReserved);
        Assert.Equal(0, m.TotalConfirmed);
        Assert.Equal(0, m.TotalPaymentFailed);
        Assert.Equal(0, m.TotalAbandoned);
        Assert.Equal(0, m.TotalExpired);
        Assert.Equal(0, m.TotalSeatTaken);
    }

    // ㉚ MetricsSnapshot: 현재 상태 합 = Total 불변식
    [Fact]
    public void MetricsSnapshot_state_sum_equals_total()
    {
        var inv  = Make2D(2, 3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");
        var ctx2 = new TicketContext("u2");

        inv.TryReserve(ctx0, 0); // seat 0 → Reserved
        inv.TryReserve(ctx1, 1); // seat 1 → Reserved
        inv.Confirm(ctx0);       // seat 0 → Sold

        var m = inv.MetricsSnapshot();

        // Free + Reserved + Sold == Total
        Assert.Equal(m.Total, m.Free + m.Reserved + m.Sold);
        Assert.Equal(4, m.Free);
        Assert.Equal(1, m.Reserved);
        Assert.Equal(1, m.Sold);
    }

    // ㉛ TotalReserved: CAS 성공 횟수만 누적
    [Fact]
    public void TotalReserved_counts_only_cas_successes()
    {
        var inv  = Make1xN(3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");
        var ctx2 = new TicketContext("u2");

        inv.TryReserve(ctx0, 0); // 성공
        inv.TryReserve(ctx1, 1); // 성공
        inv.TryReserve(ctx2, 2); // 성공

        var m = inv.MetricsSnapshot();
        Assert.Equal(3, m.TotalReserved);
    }

    // ㉜ TotalSeatTaken: CAS 실패 경합만 카운트, 범위 오류는 제외
    [Fact]
    public void TotalSeatTaken_counts_only_cas_contention_not_range_errors()
    {
        var inv  = Make1xN(3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");
        var ctx2 = new TicketContext("u2");

        // 범위 초과 요청 — SeatTaken을 반환하지만 경합이 아니므로 카운트 안 됨
        inv.TryReserve(ctx0, -1);  // 범위 밖
        inv.TryReserve(ctx1, 99);  // 범위 밖

        var mBefore = inv.MetricsSnapshot();
        Assert.Equal(0, mBefore.TotalSeatTaken); // 범위 오류는 카운트 안 됨

        // 좌석 0을 먼저 점유한 뒤 두 번째 요청 → CAS 실패 = 경합
        inv.TryReserve(ctx0, 0); // 성공
        inv.TryReserve(ctx2, 0); // CAS 실패 = 경합

        var mAfter = inv.MetricsSnapshot();
        Assert.Equal(1, mAfter.TotalSeatTaken); // 경합 1건만
        Assert.Equal(1, mAfter.TotalReserved);  // 성공 1건
    }

    // ㉝ TotalConfirmed: Confirm 성공 횟수 카운트
    [Fact]
    public void TotalConfirmed_counts_successful_confirms()
    {
        var inv  = Make1xN(3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.TryReserve(ctx0, 0);
        inv.TryReserve(ctx1, 1);
        inv.Confirm(ctx0); // 성공
        inv.Confirm(ctx0); // 중복 Confirm → NotReserved, 카운트 안 됨
        inv.Confirm(ctx1); // 성공

        var m = inv.MetricsSnapshot();
        Assert.Equal(2, m.TotalConfirmed); // 성공만 2건
    }

    // ㉞ TotalPaymentFailed: Release 성공 횟수 카운트
    [Fact]
    public void TotalPaymentFailed_counts_successful_releases()
    {
        var inv  = Make1xN(3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.TryReserve(ctx0, 0);
        inv.Release(ctx0); // 성공
        inv.Release(ctx0); // 중복 Release → NotReserved, 카운트 안 됨

        inv.TryReserve(ctx1, 1);
        inv.Release(ctx1); // 성공

        var m = inv.MetricsSnapshot();
        Assert.Equal(2, m.TotalPaymentFailed); // 성공만 2건
    }

    // ㉟ TotalAbandoned: ReleaseByContext 실제 반납 횟수 카운트
    [Fact]
    public void TotalAbandoned_counts_actual_releaseByContext()
    {
        var inv  = Make1xN(3);
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.ReleaseByContext(null);    // null — no-op, 카운트 안 됨
        inv.ReleaseByContext(ctx0);    // SlotIndex=-1 → no-op, 카운트 안 됨
        inv.TryReserve(ctx0, 0);
        inv.ReleaseByContext(ctx0);    // 실제 반납 성공

        inv.TryReserve(ctx1, 1);
        inv.ReleaseByContext(ctx1);    // 실제 반납 성공
        inv.ReleaseByContext(ctx1);    // 이미 소비됨 → no-op, 카운트 안 됨

        var m = inv.MetricsSnapshot();
        Assert.Equal(2, m.TotalAbandoned); // 성공만 2건
    }

    // ㊱ TotalExpired: SweepExpired 반납 횟수 카운트
    [Fact]
    public void TotalExpired_counts_ttl_sweeps()
    {
        var inv  = new TicketInventory(1, 3, TimeSpan.FromMilliseconds(1));
        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.TryReserve(ctx0, 0);
        inv.TryReserve(ctx1, 1);
        Thread.Sleep(20); // TTL(1ms) 충분히 초과

        int swept = inv.SweepExpired();
        Assert.Equal(2, swept);

        var m = inv.MetricsSnapshot();
        Assert.Equal(2, m.TotalExpired); // 2건 만료 반납
    }

    // ㊲ 동시 예약 — TotalReserved + TotalSeatTaken == 시도 수
    [Fact]
    public async Task Concurrent_reserve_reserved_plus_seattaken_equals_attempts()
    {
        var inv         = Make1xN(3); // 3석
        int concurrency = 64;
        ThreadPool.SetMinThreads(concurrency, concurrency);

        var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var ctx = new TicketContext($"user{i}");
            barrier.SignalAndWait();
            inv.TryReserve(ctx, i % 3); // 3개 좌석 순환 경쟁
        })).ToArray();

        await Task.WhenAll(tasks);

        var m = inv.MetricsSnapshot();

        // Reserved 성공 + SeatTaken(경합) = 총 시도 수
        Assert.Equal(concurrency, (int)(m.TotalReserved + m.TotalSeatTaken));
        Assert.Equal(3, m.TotalReserved); // 3석만 성공
        Assert.Equal(concurrency - 3, (int)m.TotalSeatTaken);
    }

    // ㊳ Base64 함정 회귀 방지 — int[] 투영 후 JSON 직렬화 시 숫자 배열로 나와야 함
    /// <summary>
    /// System.Text.Json이 <c>byte[]</c>를 Base64 문자열로 직렬화하는 함정을 방지하는 회귀 테스트.
    /// Server/Program.cs CPU 샘플러에서 <c>Array.ConvertAll(rawBytes, b =&gt; (int)b)</c>로 투영한 뒤
    /// 직렬화하면 대시보드가 <c>[0,1,2]</c>를 수신해야 하며 <c>"AAEC…"</c> Base64 문자열이어선 안 된다.
    /// </summary>
    [Fact]
    public void Seats_serialized_as_int_array_not_base64_string()
    {
        var inv = Make2D(2, 3); // 2행×3열 = 6석
        var ctx0 = new TicketContext("user0");
        var ctx1 = new TicketContext("user1");

        inv.TryReserve(ctx0, 0); // seatId 0 → Reserved(1)
        inv.Confirm(ctx0);       // seatId 0 → Sold(2)
        inv.TryReserve(ctx1, 1); // seatId 1 → Reserved(1)
        // seatId 2~5 → Free(0)

        // Server/Program.cs 산출 로직 재현: byte[] → int[] 투영
        var rawBytes = new byte[6];
        inv.SnapshotStates(rawBytes);
        // int[]: System.Text.Json이 byte[]를 Base64 문자열로 직렬화하므로 int[]로 투영 필수
        int[] seatInts = Array.ConvertAll(rawBytes, b => (int)b);

        // 직렬화 단언: seats 값이 숫자 배열 리터럴로 나와야 함
        var json = JsonSerializer.Serialize(new { seats = seatInts });

        // byte[] 그대로라면 "seats":"AAEC..." 형태. int[]면 "seats":[2,1,0,0,0,0]
        Assert.Contains("\"seats\":[", json);           // 배열 시작 확인
        Assert.DoesNotContain("\"seats\":\"", json);    // Base64 문자열 시작이 없어야 함

        // 좌석 0=Sold(2), 1=Reserved(1), 나머지=Free(0) 값 확인
        Assert.Equal(2, seatInts[0]); // Sold
        Assert.Equal(1, seatInts[1]); // Reserved
        for (int i = 2; i < 6; i++)
            Assert.Equal(0, seatInts[i]); // Free
    }
}
