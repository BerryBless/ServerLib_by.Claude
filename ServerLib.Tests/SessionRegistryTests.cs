using ServerLib.Core;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

public sealed class SessionRegistryTests
{
    [Fact]
    public void Count_WhenEmpty_ReturnsZero()
    {
        var registry = new SessionRegistry();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Register_SingleSession_CountBecomesOne()
    {
        var registry = new SessionRegistry();
        registry.Register(new StubSession());
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_SameSessionTwice_CountRemainsOne()
    {
        var registry = new SessionRegistry();
        var session = new StubSession();

        registry.Register(session);
        registry.Register(session); // 동일 SessionId 재등록

        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Unregister_RegisteredSession_CountBecomesZero()
    {
        var registry = new SessionRegistry();
        var session = new StubSession();
        registry.Register(session);

        registry.Unregister(session.SessionId);

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Unregister_UnknownId_DoesNotThrow()
    {
        var registry = new SessionRegistry();
        var act = () => registry.Unregister(Guid.NewGuid());
        var ex = Record.Exception(act);
        Assert.Null(ex);
    }

    [Fact]
    public void TryGet_RegisteredSession_ReturnsTrueAndSameInstance()
    {
        var registry = new SessionRegistry();
        var session = new StubSession();
        registry.Register(session);

        bool found = registry.TryGet(session.SessionId, out var result);

        Assert.True(found);
        Assert.Same(session, result);
    }

    [Fact]
    public void TryGet_UnknownId_ReturnsFalse()
    {
        var registry = new SessionRegistry();
        bool found = registry.TryGet(Guid.NewGuid(), out _);
        Assert.False(found);
    }

    [Fact]
    public void GetAll_WhenEmpty_ReturnsEmptyCollection()
    {
        var registry = new SessionRegistry();
        var all = registry.GetAll();
        Assert.Empty(all);
    }

    [Fact]
    public void GetAll_TwoRegisteredSessions_ReturnsBoth()
    {
        var registry = new SessionRegistry();
        var s1 = new StubSession();
        var s2 = new StubSession();
        registry.Register(s1);
        registry.Register(s2);

        var all = registry.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(s1, all);
        Assert.Contains(s2, all);
    }

    [Fact]
    public void GetAll_IsSnapshot_LaterRegistrationNotReflected()
    {
        var registry = new SessionRegistry();
        registry.Register(new StubSession());

        var snapshot = registry.GetAll();   // 1개 시점 스냅샷
        registry.Register(new StubSession()); // 이후 추가

        Assert.Single(snapshot);  // 스냅샷은 이전 상태 유지
    }

    [Fact]
    public async Task BroadcastAsync_EmptyRegistry_CompletesWithoutException()
    {
        var registry = new SessionRegistry();
        var ex = await Record.ExceptionAsync(() => registry.BroadcastAsync(new byte[] { 1 }).AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task BroadcastAsync_TwoSessions_BothReceiveData()
    {
        var registry = new SessionRegistry();
        var s1 = new StubSession();
        var s2 = new StubSession();
        registry.Register(s1);
        registry.Register(s2);
        var data = new byte[] { 0x01, 0x02, 0x03 };

        await registry.BroadcastAsync(data);

        Assert.Single(s1.SentBuffers);
        Assert.Equal(data, s1.SentBuffers.Single());
        Assert.Single(s2.SentBuffers);
        Assert.Equal(data, s2.SentBuffers.Single());
    }

    [Fact]
    public async Task BroadcastAsync_OneSessionThrows_OtherSessionStillReceives()
    {
        var registry = new SessionRegistry();
        var good = new StubSession();
        var bad  = new StubSession { ThrowOnSend = true };
        registry.Register(good);
        registry.Register(bad);

        var ex = await Record.ExceptionAsync(() => registry.BroadcastAsync(new byte[] { 1 }).AsTask());

        Assert.Null(ex);
        Assert.Single(good.SentBuffers);
    }
}
