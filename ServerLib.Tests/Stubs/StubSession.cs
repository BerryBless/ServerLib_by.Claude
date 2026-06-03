using System.Collections.Concurrent;
using System.Net;
using ServerLib.Interface;

namespace ServerLib.Tests.Stubs;

internal sealed class StubSession : ISession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint => null;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
    public bool ThrowOnSend { get; init; }
    public ConcurrentQueue<byte[]> SentBuffers { get; } = new();

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnSend) throw new ObjectDisposedException(nameof(StubSession));
        SentBuffers.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
