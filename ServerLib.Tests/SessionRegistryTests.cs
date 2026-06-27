using Xunit;
using ServerLib.Core;
using ServerLib.Interface;
using ServerLib.Tests.Fakes;

namespace ServerLib.Tests;

public sealed class SessionRegistryTests
{
    // ── 1. 생성 직후 Count == 0 ────────────────────────────────────────────────
    [Fact]
    public void Count_is_zero_after_creation()
    {
        var registry = new SessionRegistry();
        Assert.Equal(0, registry.Count);
    }

    // ── 2. Register → Count == 1 ───────────────────────────────────────────────
    [Fact]
    public void Register_increments_Count()
    {
        var registry = new SessionRegistry();
        var session = new FakeSession();

        registry.Register(session);

        Assert.Equal(1, registry.Count);
    }

    // ── 3. Register → Unregister → Count == 0 ─────────────────────────────────
    [Fact]
    public void Unregister_decrements_Count()
    {
        var registry = new SessionRegistry();
        var session = new FakeSession();

        registry.Register(session);
        registry.Unregister(session.SessionId);

        Assert.Equal(0, registry.Count);
    }

    // ── 4. TryGet: 등록된 세션을 반환한다 ─────────────────────────────────────
    [Fact]
    public void TryGet_returns_registered_session()
    {
        var registry = new SessionRegistry();
        var session = new FakeSession();

        registry.Register(session);
        bool found = registry.TryGet(session.SessionId, out var result);

        Assert.True(found);
        Assert.Same(session, result);
    }

    // ── 5. TryGet: 존재하지 않는 ID는 false ───────────────────────────────────
    [Fact]
    public void TryGet_returns_false_for_unknown_id()
    {
        var registry = new SessionRegistry();

        bool found = registry.TryGet(Guid.NewGuid(), out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    // ── 6. GetAll: 등록된 3개 세션 모두 반환 ──────────────────────────────────
    [Fact]
    public void GetAll_returns_all_registered_sessions()
    {
        var registry = new SessionRegistry();
        var s1 = new FakeSession();
        var s2 = new FakeSession();
        var s3 = new FakeSession();

        registry.Register(s1);
        registry.Register(s2);
        registry.Register(s3);

        var all = registry.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Contains(s1, all);
        Assert.Contains(s2, all);
        Assert.Contains(s3, all);
    }

    // ── 7. 전체 Unregister 후 GetAll → 빈 컬렉션 ─────────────────────────────
    [Fact]
    public void GetAll_returns_empty_after_all_unregistered()
    {
        var registry = new SessionRegistry();
        var s1 = new FakeSession();
        var s2 = new FakeSession();

        registry.Register(s1);
        registry.Register(s2);
        registry.Unregister(s1.SessionId);
        registry.Unregister(s2.SessionId);

        Assert.Empty(registry.GetAll());
    }

    // ── 8. BroadcastAsync: 전체 세션에 데이터 전달 ────────────────────────────
    [Fact]
    public async Task BroadcastAsync_sends_to_all_sessions()
    {
        var registry = new SessionRegistry();
        var s1 = new FakeSession();
        var s2 = new FakeSession();
        var s3 = new FakeSession();

        registry.Register(s1);
        registry.Register(s2);
        registry.Register(s3);

        var data = new byte[] { 0x01, 0x02, 0x03 };
        await registry.BroadcastAsync(data);

        // 각 세션이 정확히 1번 수신
        Assert.Single(s1.SentBuffers);
        Assert.Single(s2.SentBuffers);
        Assert.Single(s3.SentBuffers);

        // 전달된 데이터 일치
        Assert.Equal(data, s1.SentBuffers[0]);
        Assert.Equal(data, s2.SentBuffers[0]);
        Assert.Equal(data, s3.SentBuffers[0]);
    }

    // ── 9. BroadcastAsync: 세션 없으면 예외 없음 ──────────────────────────────
    [Fact]
    public async Task BroadcastAsync_with_no_sessions_does_not_throw()
    {
        var registry = new SessionRegistry();
        var data = new byte[] { 0xFF };

        var ex = await Record.ExceptionAsync(() => registry.BroadcastAsync(data).AsTask());
        Assert.Null(ex);
    }

    // ── 10. 동일 세션 2회 Register → Count == 1 (ConcurrentDictionary 덮어쓰기) ─
    [Fact]
    public void Register_same_session_twice_keeps_count_as_1()
    {
        var registry = new SessionRegistry();
        var session = new FakeSession();

        registry.Register(session);
        registry.Register(session);

        Assert.Equal(1, registry.Count);
    }

    // ── GAP-I-10: 존재하지 않는 ID로 Unregister → 예외 없음, Count 불변 ────────────
    [Fact]
    public void Unregister_nonexistent_id_does_not_throw()
    {
        // ConcurrentDictionary.TryRemove는 미존재 키를 조용히 무시한다
        var registry = new SessionRegistry();
        var ex = Record.Exception(() => registry.Unregister(Guid.NewGuid()));
        Assert.Null(ex);
        Assert.Equal(0, registry.Count);
    }

    // ── GAP-I-11: N개 동시 Register/Unregister → Count 정확성 ───────────────────────
    [Fact]
    public async Task ConcurrentRegisterUnregister_CountIsNonNegative()
    {
        // ConcurrentDictionary: N개 동시 Register 후 Count==N, N개 동시 Unregister 후 Count==0
        var registry = new SessionRegistry();
        const int N = 100;
        var sessions = Enumerable.Range(0, N).Select(_ => new FakeSession()).ToArray();

        await Task.WhenAll(sessions.Select(s => Task.Run(() => registry.Register(s))));
        Assert.Equal(N, registry.Count);

        await Task.WhenAll(sessions.Select(s => Task.Run(() => registry.Unregister(s.SessionId))));
        Assert.True(registry.Count >= 0, "Count는 음수가 될 수 없습니다.");
        Assert.Equal(0, registry.Count);
    }
}
