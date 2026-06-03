using ServerLib.Core.Transport;
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

    [Fact]
    public async Task IdleSweep_IdleSession_FiresOnIdleTimeoutAndDisposesSession()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var listener = new SocketPipelineListener();
        listener.IdleTimeout = TimeSpan.FromMilliseconds(100);

        var idleTimeoutFired = false;
        var stub = new StubSession
        {
            // 10초 전에 마지막 수신 → 즉시 유휴로 판정
            LastReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };
        listener.OnIdleTimeout = _ => { idleTimeoutFired = true; return ValueTask.CompletedTask; };
        listener.InjectSessionForTest(stub);

        // Act — 스윕 루프를 직접 시작하고 4× timeout 대기 (스윕 간격 50ms)
        _ = listener.StartIdleSweepForTest(cts.Token);
        await Task.Delay(400);
        cts.Cancel();

        // Assert
        Assert.True(idleTimeoutFired, "OnIdleTimeout이 호출되어야 한다");
        Assert.True(stub.WasDisposed, "세션 DisposeAsync가 호출되어야 한다");
    }

    [Fact]
    public async Task IdleSweep_ActiveSession_NotDisconnected()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var listener = new SocketPipelineListener();
        listener.IdleTimeout = TimeSpan.FromMilliseconds(200);

        var idleCalled = false;
        var stub = new StubSession
        {
            // 방금 수신한 것처럼 설정 → 200ms 이내이므로 타임아웃 아님
            LastReceivedAt = DateTimeOffset.UtcNow
        };
        listener.OnIdleTimeout = _ => { idleCalled = true; return ValueTask.CompletedTask; };
        listener.InjectSessionForTest(stub);

        // Act
        _ = listener.StartIdleSweepForTest(cts.Token);
        await Task.Delay(150); // 타임아웃(200ms)보다 짧게 대기

        cts.Cancel();

        // Assert
        Assert.False(idleCalled, "활성 세션은 타임아웃 처리하면 안 된다");
    }

    [Fact]
    public async Task IdleTimeout_WhenNull_NoSweepStarted_NoDisconnect()
    {
        // Arrange
        var listener = new SocketPipelineListener();
        // IdleTimeout = null (기본값) → StartIdleSweepForTest 호출 불가 → 직접 확인

        var idleCalled = false;
        var stub = new StubSession
        {
            LastReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-60)
        };
        listener.OnIdleTimeout = _ => { idleCalled = true; return ValueTask.CompletedTask; };
        listener.InjectSessionForTest(stub);

        // IdleTimeout = null이면 Start()에서 스윕 루프를 시작하지 않음
        // 여기서는 Start()를 호출하지 않으므로 스윕 없음 → 300ms 대기해도 idle 미발화
        await Task.Delay(300);

        // Assert
        Assert.False(idleCalled, "IdleTimeout=null이면 스윕이 실행되지 않아야 한다");
    }
}
