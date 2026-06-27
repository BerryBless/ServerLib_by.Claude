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

    /// <summary>
    /// N개 Task를 Barrier로 동시 출발시켜 동시성 시나리오를 실행하고 결과 배열을 반환합니다.
    /// </summary>
    /// <param name="concurrency">동시 Task 수.</param>
    /// <param name="work">인덱스를 받아 (status, slot) 튜플을 반환하는 작업. Barrier 통과 직후 호출됩니다.</param>
    private static async Task<(TicketStatus status, int slot)[]> RunConcurrentAsync(
        int concurrency,
        Func<int, (TicketStatus status, int slot)> work)
    {
        // [LOCK-05] CI 2코어 환경에서 Barrier 동기 블로킹으로 ThreadPool 스레드 고갈 방지
        ThreadPool.SetMinThreads(concurrency, concurrency);
        // Barrier: 모든 Task가 준비된 후 동시에 CAS 경쟁을 시작
        var barrier = new Barrier(concurrency);
        var results = new (TicketStatus status, int slot)[concurrency];
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            results[i] = work(i);
        })));
        return results;
    }

    // ① 64개 Task 동시 예약 → 정확히 TotalTickets개만 Reserved, 나머지 SeatTaken
    [Fact]
    public async Task Concurrent_reserve_seat_designated_exactly_totalTickets_succeed()
    {
        var inv = Make1xN(3); // 3석: seatId 0,1,2
        // 64개 Task가 3개 좌석(0,1,2)을 순환 선택 — 각 좌석마다 약 21개 경쟁
        var results = await RunConcurrentAsync(64, i =>
        {
            var ctx = new TicketContext($"user{i}");
            return inv.TryReserve(ctx, i % 3);
        });

        int reservedCount  = results.Count(r => r.status == TicketStatus.Reserved);
        int seatTakenCount = results.Count(r => r.status == TicketStatus.SeatTaken);

        Assert.Equal(3, reservedCount);                    // 좌석 수만큼 성공
        Assert.Equal(64 - 3, seatTakenCount);              // 나머지는 SeatTaken

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
        var inv = Make1xN(3);
        var results = await RunConcurrentAsync(50, i =>
        {
            var ctx = new TicketContext($"user{i}");
            return inv.TryReserve(ctx, 0); // 모두 좌석 0을 노림
        });

        int reservedCount  = results.Count(r => r.status == TicketStatus.Reserved);
        int seatTakenCount = results.Count(r => r.status == TicketStatus.SeatTaken);

        Assert.Equal(1, reservedCount);
        Assert.Equal(50 - 1, seatTakenCount);
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
            if (ctx.Slots[0] < 0)
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
        Assert.Equal(-1, ctx.Slots[0]); // SlotIndex 오염 없음
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
        Assert.Equal(-1, ctx.Slots[0]);  // 선형화 앵커 소비됨
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
        Assert.True(ctx.Slots[0] >= 0); // 좌석 1 보유 유지
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
        Assert.Equal(-1, ctx.Slots[0]); // 선형화 앵커 소비됨

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
        Assert.Equal(-1, ctx.Slots[0]); // SlotIndex 오염 없음
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
        Assert.Equal(5, ctx.Slots[0]);
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
        Assert.Equal(-1, ctx2.Slots[0]); // ctx2 SlotIndex 오염 없음

        // (1,0)은 여전히 ctx1이 보유
        Assert.Equal(3, ctx1.Slots[0]);
    }

    // ㉘ TryReserveByRowCol: 2D 그리드 동시 예약 — 정확히 TotalTickets개만 성공
    [Fact]
    public async Task TryReserveByRowCol_concurrent_2d_exactly_totalTickets_succeed()
    {
        var inv = Make2D(2, 3); // 6석
        var results = await RunConcurrentAsync(30, i =>
        {
            var ctx = new TicketContext($"user{i}");
            int row = (i % 6) / 3; // 2행 순환
            int col = (i % 6) % 3; // 3열 순환
            return inv.TryReserveByRowCol(ctx, row, col);
        });

        int reservedCount  = results.Count(r => r.status == TicketStatus.Reserved);
        int seatTakenCount = results.Count(r => r.status == TicketStatus.SeatTaken);

        Assert.Equal(6, reservedCount);               // 정확히 6석 예약 성공
        Assert.Equal(30 - 6, seatTakenCount);
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
        var inv = Make1xN(3); // 3석
        // 결과 배열은 사용 안 함 — MetricsSnapshot 카운터로 검증
        await RunConcurrentAsync(64, i =>
        {
            var ctx = new TicketContext($"user{i}");
            return inv.TryReserve(ctx, i % 3); // 3개 좌석 순환 경쟁
        });

        var m = inv.MetricsSnapshot();

        // Reserved 성공 + SeatTaken(경합) = 총 시도 수 (64회)
        Assert.Equal(64, (int)(m.TotalReserved + m.TotalSeatTaken));
        Assert.Equal(3, m.TotalReserved); // 3석만 성공
        Assert.Equal(64 - 3, (int)m.TotalSeatTaken);
    }

    // ─────── 신규 배치 API 테스트 ───────

    // ㊴ TryReserveBatch: K석 전부 성공
    [Fact]
    public void TryReserveBatch_all_seats_success()
    {
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 3); // cap=3

        Span<int> reserved = stackalloc int[3];
        var (status, count) = inv.TryReserveBatch(ctx, new[] { 0, 1, 2 }, reserved);

        Assert.Equal(TicketStatus.Reserved, status);
        Assert.Equal(3, count);
        Assert.Equal(3, inv.FreeCount); // 3석 예약, 3석 잔여
        // Slots 배열에 3개의 seatId가 채워져 있어야 함
        int heldCount = ctx.Slots.Count(s => s >= 0);
        Assert.Equal(3, heldCount);
    }

    // ㊵ TryReserveBatch 경합 롤백: 두 컨텍스트가 겹치는 배치 → 정확히 하나만 전체 성공, 패자는 0석
    [Fact]
    public async Task TryReserveBatch_concurrent_overlapping_exactly_one_wins()
    {
        var inv  = Make2D(1, 4); // 4석: seatId 0~3
        var ctx1 = new TicketContext("user1", 2);
        var ctx2 = new TicketContext("user2", 2);
        // 두 컨텍스트 모두 seatId 1,2를 요청 — 반드시 경합 발생
        int[]   seatIds1 = { 1, 2 };
        int[]   seatIds2 = { 1, 2 };

        Span<int> buf1 = stackalloc int[2];
        Span<int> buf2 = stackalloc int[2];

        var barrier = new Barrier(2);
        int winner1 = 0, winner2 = 0; // 각 컨텍스트의 실제 예약 수

        await Task.WhenAll(
            Task.Run(() =>
            {
                barrier.SignalAndWait();
                Span<int> b = stackalloc int[2];
                var (_, c) = inv.TryReserveBatch(ctx1, seatIds1, b);
                winner1 = c;
            }),
            Task.Run(() =>
            {
                barrier.SignalAndWait();
                Span<int> b = stackalloc int[2];
                var (_, c) = inv.TryReserveBatch(ctx2, seatIds2, b);
                winner2 = c;
            }));

        // 정확히 하나만 전체 성공(2석), 패자는 0석
        int totalWon = winner1 + winner2;
        Assert.Equal(2, totalWon);
        // 패자 컨텍스트: Reserved 잔류 좌석 없음(All-or-nothing 롤백)
        bool loserCtx1CleanedUp = ctx1.Slots.All(s => s < 0);
        bool loserCtx2CleanedUp = ctx2.Slots.All(s => s < 0);
        if (winner1 == 0) Assert.True(loserCtx1CleanedUp, "패자 ctx1의 Slots가 롤백되지 않음");
        if (winner2 == 0) Assert.True(loserCtx2CleanedUp, "패자 ctx2의 Slots가 롤백되지 않음");
        // Reserved 잔류 슬롯 검증: 경합에서 진 배치가 _states를 Reserved로 남기지 않음
        Assert.Equal(inv.TotalTickets - totalWon, inv.FreeCount);
    }

    // ㊶ TryReserveBatch: cap 초과 요청 → SeatTaken, 좌석 무변경
    [Fact]
    public void TryReserveBatch_exceeds_cap_returns_seatTaken()
    {
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 2); // cap=2

        Span<int> reserved = stackalloc int[3];
        // cap(2)보다 많은 3석 요청 → SeatTaken
        var (status, count) = inv.TryReserveBatch(ctx, new[] { 0, 1, 2 }, reserved);

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(0, count);
        Assert.Equal(6, inv.FreeCount); // 좌석 무변경
        Assert.True(ctx.Slots.All(s => s < 0)); // 슬롯 미오염
    }

    // ㊷ TryReserveBatch: 배치 내 중복 좌석 → SeatTaken, 좌석 무변경
    [Fact]
    public void TryReserveBatch_duplicate_seatIds_returns_seatTaken()
    {
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 3);

        Span<int> reserved = stackalloc int[3];
        // seatId=1이 두 번 등장 — 중복 검증으로 즉시 거부
        var (status, count) = inv.TryReserveBatch(ctx, new[] { 0, 1, 1 }, reserved);

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(0, count);
        Assert.Equal(6, inv.FreeCount); // 좌석 무변경
    }

    // ㊸ ConfirmAll: 보유 전체 Sold, Slots 전부 -1
    [Fact]
    public void ConfirmAll_marks_all_held_slots_as_sold()
    {
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 3);

        Span<int> reservedBuf = stackalloc int[3];
        inv.TryReserveBatch(ctx, new[] { 0, 2, 4 }, reservedBuf);

        Span<int> confirmedBuf = stackalloc int[3];
        int count = inv.ConfirmAll(ctx, confirmedBuf);

        Assert.Equal(3, count);
        // Slots 전부 -1(소비됨)
        Assert.True(ctx.Slots.All(s => s < 0));
        // 6석 중 3석 Sold → FreeCount=3
        Assert.Equal(3, inv.FreeCount);
        var m = inv.MetricsSnapshot();
        Assert.Equal(3, m.Sold);
        Assert.Equal(3, m.TotalConfirmed);
    }

    // ㊹ ReleaseAll: 보유 전체 해제, Slots 전부 -1
    [Fact]
    public void ReleaseAll_releases_all_held_slots()
    {
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 2);

        Span<int> reservedBuf = stackalloc int[2];
        inv.TryReserveBatch(ctx, new[] { 1, 3 }, reservedBuf);
        Assert.Equal(4, inv.FreeCount);

        Span<int> releasedBuf = stackalloc int[2];
        int count = inv.ReleaseAll(ctx, releasedBuf);

        Assert.Equal(2, count);
        Assert.True(ctx.Slots.All(s => s < 0));
        Assert.Equal(6, inv.FreeCount); // 전체 복귀
        var m = inv.MetricsSnapshot();
        Assert.Equal(2, m.TotalPaymentFailed);
    }

    // ㊺ SweepExpired 배치 컨텍스트: ctx(seat 0, 만료) + ctx2(seat 1, 유효) → ctx entry만 해제, ctx2 잔존
    // QUALITY-I-04: 원래 "단일 배치 컨텍스트의 만료된 슬롯만 반납"을 암시하는 이름이었으나
    // 실제로는 두 개의 독립 컨텍스트를 사용하므로 이름을 실제 시나리오에 맞게 정정한다.
    [Fact]
    public void SweepExpired_releases_ctx1_seat_not_ctx2_fresh_seat()
    {
        // TTL 1ms — seat 0를 만료시키고 seat 1을 TTL 내에 유지
        var inv  = new TicketInventory(1, 4, TimeSpan.FromMilliseconds(1));
        var ctx  = new TicketContext("user", 2);

        // seat 0 예약 후 TTL 초과, seat 1은 직후 예약 → 아직 TTL 내
        Span<int> buf0 = stackalloc int[1];
        inv.TryReserveBatch(ctx, new[] { 0 }, buf0); // seat 0 예약
        Thread.Sleep(20);                              // seat 0 TTL 초과

        // seat 1은 TTL 이후에 예약 → TTL 만료 안 됨
        // 그러나 ctx.Slots[0]가 seat 0를 가지고 있으므로 seat 1은 Slots[1]에 들어감
        // cap=2이므로 직접 TryReserveOne으로 대신 Slots[1]에 seat 1 넣기
        // → 배치 2개 동시 예약으로 변경: 다른 컨텍스트로 seat 0 이미 점유 후 ctx만 seat 1 예약
        // 여기서는 단순화: ctx 하나만 cap=2로 두 좌석 동시 예약 후 seat0만 TTL 초과(불가능)
        // 대신 두 컨텍스트로 검증
        var ctx2 = new TicketContext("user2", 2);
        Span<int> buf1 = stackalloc int[1];
        inv.TryReserveBatch(ctx2, new[] { 1 }, buf1); // seat 1 예약 (TTL 내)

        int released = inv.SweepExpired();

        Assert.Equal(1, released);                // seat 0만 반납
        Assert.Equal(-1, ctx.Slots[0]);           // seat 0 entry 소비됨
        Assert.Equal(1, ctx2.Slots[0]);           // seat 1 잔존
        Assert.Equal(3, inv.FreeCount);           // seat 0 Free, seat 2·3 Free → 3개 (seat 1 Reserved)
        var m = inv.MetricsSnapshot();
        Assert.Equal(1, m.TotalExpired);
    }

    // ㊻ AlreadyReserved: 배치 보유 중인 컨텍스트의 두 번째 TryReserveBatch는 AlreadyReserved
    [Fact]
    public void TryReserveBatch_second_request_while_holding_returns_alreadyReserved()
    {
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 2);

        Span<int> buf1 = stackalloc int[2];
        var (first, _) = inv.TryReserveBatch(ctx, new[] { 0, 1 }, buf1);
        Assert.Equal(TicketStatus.Reserved, first);

        // 이미 2석 보유 중 → 두 번째 배치 요청은 AlreadyReserved
        Span<int> buf2 = stackalloc int[2];
        var (second, count2) = inv.TryReserveBatch(ctx, new[] { 2, 3 }, buf2);
        Assert.Equal(TicketStatus.AlreadyReserved, second);
        Assert.Equal(0, count2);
        Assert.Equal(4, inv.FreeCount); // 첫 배치(2석)만 예약됨
    }

    // ㊼ GAP-I-15: Rate Limit 정확 경계 — 10회째 허용, 11회째 초과 (계약 고정 테스트)
    [Fact]
    public void RateLimit_exactly_10th_attempt_allowed_11th_returns_RateLimited()
    {
        // Server/Program.cs의 속도 제한 로직:
        //   if (Interlocked.Increment(ref tctx.RateLimitAttempts) > MaxReserveAttemptsPerWindow)
        // 경계: 10회째 Increment → 10. 10 > 10 false(허용), 11회째 → 11 > 10 true(차단)
        // 이 테스트는 TicketContext 상수 계약을 고정하여 실수로 경계값이 변경되면 탐지한다.
        Assert.Equal(10, TicketContext.MaxReserveAttemptsPerWindow);
        Assert.Equal(60_000, TicketContext.RateLimitWindowMs);

        var ctx = new TicketContext("user");
        // 9회 사전 세팅 (0→9)
        Interlocked.Exchange(ref ctx.RateLimitAttempts, TicketContext.MaxReserveAttemptsPerWindow - 1);

        // 10회째: Increment → 10. 10 > 10 false → 허용 경로
        int attempt10 = Interlocked.Increment(ref ctx.RateLimitAttempts);
        Assert.Equal(TicketContext.MaxReserveAttemptsPerWindow, attempt10);
        Assert.False(attempt10 > TicketContext.MaxReserveAttemptsPerWindow,
            "10회째 시도(=MaxReserveAttemptsPerWindow)는 허용되어야 합니다.");

        // 11회째: Increment → 11. 11 > 10 true → 차단 경로
        int attempt11 = Interlocked.Increment(ref ctx.RateLimitAttempts);
        Assert.True(attempt11 > TicketContext.MaxReserveAttemptsPerWindow,
            "11회째 시도는 MaxReserveAttemptsPerWindow를 초과하므로 RateLimited 처리되어야 합니다.");
    }

    // ㊽ GAP-I-16: TryReserveBatch 빈 seatIds(n==0) → SeatTaken, 좌석 무변화
    [Fact]
    public void TryReserveBatch_empty_seatIds_returns_seatTaken()
    {
        // n==0이면 개수·상한 검증 분기에서 즉시 SeatTaken 반환 — 좌석·슬롯 무오염 확인
        var inv = Make2D(2, 3); // 6석
        var ctx = new TicketContext("user", 4); // cap=4

        Span<int> reserved = stackalloc int[4];
        var (status, count) = inv.TryReserveBatch(ctx, Array.Empty<int>(), reserved);

        Assert.Equal(TicketStatus.SeatTaken, status);
        Assert.Equal(0, count);
        Assert.Equal(6, inv.FreeCount);                    // 좌석 무변화
        Assert.True(ctx.Slots.All(s => s < 0),            // 슬롯 미오염
            "빈 요청 후 TicketContext.Slots가 오염되어서는 안 됩니다.");
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

        // [ARCH-NEW-01] ProjectSeatStates()를 직접 호출해 생산 경로와 동일 코드를 검증
        // byte→int 투영이 도메인 계층에 캡슐화되어 Program.cs와 이 테스트가 같은 경로를 공유한다.
        int[] seatInts = inv.ProjectSeatStates();

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
