using ServerLib.Interface;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public void SessionState_CustomValue_WorksCorrectly()
    {
        var custom = new SessionState(100);

        Assert.Equal(100, custom.Value);
        Assert.Equal("Custom(100)", custom.ToString());
        Assert.NotEqual(SessionState.Connected, custom);
    }

    [Fact]
    public void SessionState_PredefinedConstants_HaveExpectedValues()
    {
        Assert.Equal(0, SessionState.Connecting.Value);
        Assert.Equal(1, SessionState.Connected.Value);
        Assert.Equal(2, SessionState.Authenticated.Value);
        Assert.Equal(3, SessionState.Disconnecting.Value);
        Assert.Equal(4, SessionState.Disconnected.Value);
    }

    [Fact]
    public void SessionState_ToString_ReturnsName()
    {
        Assert.Equal("Connecting", SessionState.Connecting.ToString());
        Assert.Equal("Authenticated", SessionState.Authenticated.ToString());
    }

    [Fact]
    public void SessionState_Equality_ComparesByValue()
    {
        Assert.True(SessionState.Connected == new SessionState(1));
        Assert.True(SessionState.Connected != SessionState.Authenticated);
    }

    [Fact]
    public void StubSession_Context_SetAndGet_ReturnsSameReference()
    {
        var stub = new StubSession();
        var payload = new object();

        stub.Context = payload;

        Assert.Same(payload, stub.Context);
    }

    [Fact]
    public void StubSession_Context_Default_IsNull()
    {
        var stub = new StubSession();
        Assert.Null(stub.Context);
    }

    [Fact]
    public void Session_InitialState_IsConnecting()
    {
        var stub = new StubSession();
        Assert.Equal(SessionState.Connecting, stub.State);
    }

    [Fact]
    public void Session_TransitionTo_ChangesStateAndReturnsTrue()
    {
        var stub = new StubSession();

        bool result = stub.TransitionTo(SessionState.Authenticated);

        Assert.True(result);
        Assert.Equal(SessionState.Authenticated, stub.State);
    }

    [Fact]
    public void Session_TransitionTo_FromDisconnected_IsRejected()
    {
        var stub = new StubSession();
        stub.TransitionTo(SessionState.Disconnected);

        // 종착 상태에서 다른 상태로의 부활 시도는 거부되어야 한다.
        bool result = stub.TransitionTo(SessionState.Authenticated);

        Assert.False(result);
        Assert.Equal(SessionState.Disconnected, stub.State);
    }

    [Fact]
    public void SessionState_Custom_BelowReservedRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionState.Custom(4));
    }

    [Fact]
    public void SessionState_Custom_AboveReservedRange_Succeeds()
    {
        var custom = SessionState.Custom(100);

        Assert.Equal(100, custom.Value);
        Assert.Equal("Custom(100)", custom.ToString());
    }
}
