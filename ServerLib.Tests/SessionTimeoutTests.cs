using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

public sealed class SessionTimeoutTests
{
    [Fact]
    public void StubSession_LastReceivedAt_InitialValue_IsRecentUtcTime()
    {
        var before = DateTimeOffset.UtcNow;
        var session = new StubSession();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(session.LastReceivedAt, before, after);
    }

    [Fact]
    public void StubSession_LastReceivedAt_IsSettable()
    {
        var session = new StubSession();
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);

        session.LastReceivedAt = past;

        Assert.Equal(past, session.LastReceivedAt);
    }
}
