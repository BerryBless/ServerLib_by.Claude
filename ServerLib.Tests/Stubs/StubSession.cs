using System.Collections.Concurrent;
using System.Net;
using ServerLib.Interface;

namespace ServerLib.Tests.Stubs;

internal sealed class StubSession : ISession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint => null;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
    public bool ThrowOnSend { get; init; }
    public ConcurrentQueue<byte[]> SentBuffers { get; } = new();
    public bool WasDisposed { get; private set; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnSend) throw new ObjectDisposedException(nameof(StubSession));
        SentBuffers.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (WasDisposed) return; // 이중 호출 방어
        WasDisposed = true;
        if (OnDisconnected != null)
            await OnDisconnected(); // 프로덕션 동작과 동일한 OnDisconnected 발화
    }
}
