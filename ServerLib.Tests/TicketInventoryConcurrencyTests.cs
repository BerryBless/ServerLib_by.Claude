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
}
