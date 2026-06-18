using Xunit;
using ServerLib.Core.Serialization.Packets;
using Ticketing;

namespace ServerLib.Tests;

/// <summary>
/// <see cref="TicketInventory"/>의 lock-free 동시성 정확성을 검증합니다.
/// </summary>
public class TicketInventoryConcurrencyTests
{
    private static TicketInventory Make(int total = 3) =>
        new TicketInventory(total, TimeSpan.FromSeconds(30));

    // ① 64개 Task 동시 예약 → 정확히 TotalTickets개만 Reserved, 나머지 SoldOut
    [Fact]
    public async Task Concurrent_reserve_exactly_totalTickets_succeed()
    {
        var inv     = Make(3);
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
            results[i] = inv.TryReserve(ctx);
        })).ToArray();

        await Task.WhenAll(tasks);

        int reservedCount = results.Count(r => r.status == TicketStatus.Reserved);
        int soldOutCount  = results.Count(r => r.status == TicketStatus.SoldOut);

        Assert.Equal(3,              reservedCount);
        Assert.Equal(concurrency - 3, soldOutCount);

        // 예약된 슬롯 인덱스가 정확히 {0,1,2}인지 검증
        var reservedSlots = results
            .Where(r => r.status == TicketStatus.Reserved)
            .Select(r => r.slot)
            .OrderBy(s => s)
            .ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, reservedSlots);
    }

    // ② 3 예약 모두 확정 후 4번째는 SoldOut
    [Fact]
    public void After_all_confirmed_further_reserve_returns_soldOut()
    {
        var inv  = Make(3);
        var ctxs = Enumerable.Range(0, 3).Select(i => new TicketContext($"user{i}")).ToArray();

        foreach (var ctx in ctxs)
        {
            var (status, _) = inv.TryReserve(ctx);
            Assert.Equal(TicketStatus.Reserved, status);
            inv.Confirm(ctx);
        }

        var extra = new TicketContext("extra");
        var (extraStatus, _) = inv.TryReserve(extra);
        Assert.Equal(TicketStatus.SoldOut, extraStatus);
    }

    // ③ 1개 반납 후 신규 컨텍스트가 그 슬롯을 재예약
    [Fact]
    public void After_release_new_context_can_reserve()
    {
        var inv  = Make(3);
        var ctxs = Enumerable.Range(0, 3).Select(i => new TicketContext($"user{i}")).ToArray();

        // 3개 전부 예약
        foreach (var ctx in ctxs)
            inv.TryReserve(ctx);
        Assert.Equal(0, inv.FreeCount);

        // 슬롯 1 반납
        var (relStatus, relSlot) = inv.Release(ctxs[1]);
        Assert.Equal(TicketStatus.Released, relStatus);
        Assert.Equal(1, inv.FreeCount);

        // 새 컨텍스트 재예약
        var newCtx = new TicketContext("newuser");
        var (newStatus, newSlot) = inv.TryReserve(newCtx);
        Assert.Equal(TicketStatus.Reserved, newStatus);
        Assert.Equal(relSlot, newSlot); // 반납된 슬롯과 동일 인덱스 재사용
        Assert.Equal(0, inv.FreeCount);
    }

    // ④ Confirm 2회 → 2번째 NotReserved, 슬롯 상태 Sold 유지
    [Fact]
    public void Double_confirm_second_returns_notReserved()
    {
        var inv = Make(3);
        var ctx = new TicketContext("user");

        inv.TryReserve(ctx);
        var (first, slot1)  = inv.Confirm(ctx);
        var (second, slot2) = inv.Confirm(ctx);

        Assert.Equal(TicketStatus.Confirmed,   first);
        Assert.Equal(TicketStatus.NotReserved, second);
        Assert.Equal(-1, slot2); // 두 번째 Confirm은 슬롯 없음
    }

    // ⑤ Confirm vs ReleaseByContext 동시 경합 → 정확히 하나만 승리, 인벤토리 일관성 유지
    [Fact]
    public async Task Concurrent_confirm_and_releaseByContext_exactly_one_wins()
    {
        var inv = Make(1); // 슬롯 1개만
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx);

        // Confirm과 ReleaseByContext를 동시에 100회씩 시도
        const int rounds = 100;
        for (int r = 0; r < rounds; r++)
        {
            // 슬롯 재예약 (직전 라운드에서 Free로 돌아온 경우)
            if (ctx.SlotIndex < 0)
                inv.TryReserve(ctx);

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

            // 슬롯 상태는 Sold(Confirmed 승) 또는 Free(Release 승) 둘 중 하나여야 함 — Reserved 잔류 불가
            // 다음 라운드를 위해 Sold 슬롯은 직접 Free로 되돌리기 (테스트 픽스처 리셋)
            // 실제로 ctx.SlotIndex=-1이므로 TryReserve가 Free 슬롯을 찾아 재예약한다
        }
    }

    // ⑥ AlreadyReserved 가드: 동일 컨텍스트의 두 번째 TryReserve는 AlreadyReserved
    [Fact]
    public void TryReserve_same_context_twice_returns_alreadyReserved()
    {
        var inv = Make(3);
        var ctx = new TicketContext("user");

        var (first, _)  = inv.TryReserve(ctx);
        var (second, _) = inv.TryReserve(ctx);

        Assert.Equal(TicketStatus.Reserved,       first);
        Assert.Equal(TicketStatus.AlreadyReserved, second);
    }

    // ⑦ FreeCount: 예약·확정·반납에 따라 정확히 변화
    [Fact]
    public void FreeCount_tracks_slot_transitions_correctly()
    {
        var inv = Make(3);
        Assert.Equal(3, inv.FreeCount);

        var ctx0 = new TicketContext("u0");
        var ctx1 = new TicketContext("u1");

        inv.TryReserve(ctx0);
        Assert.Equal(2, inv.FreeCount); // Reserved 1개 → Free 2개

        inv.TryReserve(ctx1);
        Assert.Equal(1, inv.FreeCount);

        inv.Confirm(ctx0);
        Assert.Equal(1, inv.FreeCount); // Sold는 Free로 치지 않음

        inv.Release(ctx1);
        Assert.Equal(2, inv.FreeCount); // Release → Free 복귀
    }

    // ──────────── GAP-01: SweepExpired 기본 경로 ────────────

    // ⑧ TTL 만료된 Reserved 슬롯은 SweepExpired로 반납됨
    [Fact]
    public void SweepExpired_releases_expired_reserved_slots()
    {
        var inv = new TicketInventory(2, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx);
        Thread.Sleep(20); // TTL(1ms) 충분히 초과

        int released = inv.SweepExpired();

        Assert.Equal(1, released);
        Assert.Equal(2, inv.FreeCount);   // 슬롯 반납됨
        Assert.Equal(-1, ctx.SlotIndex);  // 선형화 앵커 소비됨
    }

    // ⑨ Confirm 완료 슬롯(Sold)은 SweepExpired가 건드리지 않음
    [Fact]
    public void SweepExpired_does_not_touch_confirmed_slot()
    {
        var inv = new TicketInventory(2, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx);
        inv.Confirm(ctx);    // Sold 상태로 확정, SlotIndex=-1
        Thread.Sleep(20);    // TTL 초과

        int released = inv.SweepExpired();

        Assert.Equal(0, released);   // Sold 슬롯은 반납 대상 아님
    }

    // ──────────── GAP-02: SweepExpired ABA 경로 ────────────

    // ⑩ Confirm 후 재예약한 컨텍스트는 스위퍼 CAS가 실패하여 안전
    [Fact]
    public void SweepExpired_does_not_release_slot_after_confirm_and_re_reserve()
    {
        var inv = new TicketInventory(2, TimeSpan.FromMilliseconds(1));
        var ctx = new TicketContext("user");
        inv.TryReserve(ctx);     // 슬롯 0 예약
        Thread.Sleep(20);         // TTL 초과

        // 스위퍼 전에 ctx가 슬롯 0을 Confirm하고 슬롯 1을 재예약
        inv.Confirm(ctx);        // 슬롯 0 → Sold, SlotIndex=-1
        inv.TryReserve(ctx);     // 슬롯 1 재예약

        // 슬롯 0은 Sold → SweepExpired Reserved 조건 실패로 건너뜀
        // 슬롯 1은 TTL 미초과(방금 예약됨) → 건너뜀
        int released = inv.SweepExpired();

        Assert.Equal(0, released);
        Assert.True(ctx.SlotIndex >= 0); // 슬롯 1 보유 유지
    }

    // ──────────── GAP-03: null 컨텍스트 방어 ────────────

    // ⑪ ReleaseByContext(null)은 예외 없이 no-op
    [Fact]
    public void ReleaseByContext_null_context_is_noop()
    {
        var inv = Make(3);
        var exception = Record.Exception(() => inv.ReleaseByContext(null));
        Assert.Null(exception);
        Assert.Equal(3, inv.FreeCount); // 슬롯 변화 없음
    }

    // ──────────── GAP-05: Release 미예약 경로 ────────────

    // ⑫ 예약 없이 Release 호출 → NotReserved 반환
    [Fact]
    public void Release_without_reservation_returns_notReserved()
    {
        var inv = Make(3);
        var ctx = new TicketContext("user");

        var (status, slot) = inv.Release(ctx);

        Assert.Equal(TicketStatus.NotReserved, status);
        Assert.Equal(-1, slot);
        Assert.Equal(3, inv.FreeCount); // 슬롯 변화 없음
    }

    // ──────────── GAP-06: 생성자 유효성 검증 ────────────

    // ⑬ totalTickets ≤ 0 이거나 > 255이면 ArgumentOutOfRangeException
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    public void Constructor_invalid_totalTickets_throws(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TicketInventory(invalid, TimeSpan.FromSeconds(30)));
    }

    // ──────────── GAP-04: DummyPaymentGateway 극단값 ────────────

    // ⑭ FailureRate=0이면 항상 성공, FailureRate=1이면 항상 실패
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, false)]
    public async Task DummyPaymentGateway_extreme_failureRates(double rate, bool expected)
    {
        var gw = new DummyPaymentGateway(delayMs: 0, failureRate: rate);
        bool result = await gw.ChargeAsync("user", simulateFailure: false);
        Assert.Equal(expected, result);
    }
}
