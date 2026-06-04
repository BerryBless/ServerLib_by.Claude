using ServerLib.Core;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

/// <summary>E2: ISession.Context 타입 안전 접근 확장 메서드 검증.</summary>
public sealed class SessionContextExtensionsTests
{
    private sealed record TestContext(int Id, string Name);

    [Fact]
    public void GetContext_WhenSetToMatchingType_ReturnsSameInstance()
    {
        var session = new StubSession();
        var ctx = new TestContext(7, "neo");
        session.Context = ctx;

        var result = session.GetContext<TestContext>();

        Assert.Same(ctx, result);
    }

    [Fact]
    public void GetContext_WhenContextNull_ReturnsDefault()
    {
        var session = new StubSession(); // Context 미설정(null)
        Assert.Null(session.GetContext<TestContext>());
    }

    [Fact]
    public void GetContext_WhenContextIsDifferentType_ReturnsDefault()
    {
        var session = new StubSession { Context = "a string" };
        Assert.Null(session.GetContext<TestContext>());
    }

    [Fact]
    public void TryGetContext_WhenMatchingType_ReturnsTrueAndValue()
    {
        var session = new StubSession();
        var ctx = new TestContext(1, "a");
        session.Context = ctx;

        bool ok = session.TryGetContext<TestContext>(out var value);

        Assert.True(ok);
        Assert.Same(ctx, value);
    }

    [Fact]
    public void TryGetContext_WhenNullOrMismatch_ReturnsFalseAndDefault()
    {
        var session = new StubSession(); // null context
        bool ok = session.TryGetContext<TestContext>(out var value);

        Assert.False(ok);
        Assert.Null(value);
    }
}
