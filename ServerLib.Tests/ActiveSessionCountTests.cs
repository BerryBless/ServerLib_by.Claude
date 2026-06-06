using ServerLib.Core.Transport;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

public class ActiveSessionCountTests
{
    [Fact]
    public void ActiveSessionCount_reflects_injected_sessions()
    {
        var listener = new SocketPipelineListener();
        Assert.Equal(0, listener.ActiveSessionCount);

        var s1 = new StubSession();
        var s2 = new StubSession();
        listener.InjectSessionForTest(s1);
        listener.InjectSessionForTest(s2);

        Assert.Equal(2, listener.ActiveSessionCount);
    }
}
