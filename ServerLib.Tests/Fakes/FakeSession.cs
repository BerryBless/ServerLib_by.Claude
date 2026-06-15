using System.Net;
using ServerLib.Interface;

namespace ServerLib.Tests.Fakes;

/// <summary>
/// <see cref="ISession"/> 캡처 페이크. 단위 테스트에서 실제 소켓 없이 송신 버퍼를 검증할 때 사용합니다.
/// </summary>
public sealed class FakeSession : ISession
{
    /// <inheritdoc />
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; } = null;

    /// <inheritdoc />
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset LastReceivedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset LastProgressAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public SessionState State { get; private set; } = SessionState.Connected;

    /// <inheritdoc />
    public object? Context { get; set; }

    /// <inheritdoc />
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    /// <inheritdoc />
    public Func<ValueTask>? OnDisconnected { get; set; }

    /// <inheritdoc />
    public Func<Exception, ValueTask>? OnReceiveError { get; set; }

    /// <summary>
    /// <see cref="SendAsync"/> 호출마다 캡처된 바이트 배열 목록입니다.
    /// </summary>
    public List<byte[]> SentBuffers { get; } = new();

    /// <inheritdoc />
    public bool TransitionTo(SessionState newState)
    {
        State = newState;
        return true;
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        SentBuffers.Add(data.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
