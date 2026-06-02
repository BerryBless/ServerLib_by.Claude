using System.Net;

namespace ServerLib.Interface;

public interface ISession : IAsyncDisposable
{
    Guid SessionId { get; }
    EndPoint? RemoteEndPoint { get; }
    DateTimeOffset ConnectedAt { get; }

    Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    Func<ValueTask>? OnDisconnected { get; set; }

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
