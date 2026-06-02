namespace ServerLib.Interface;

public interface IClientConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Func<ValueTask>? OnConnected { get; set; }
    Func<ValueTask>? OnDisconnected { get; set; }
    Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void Disconnect();
}
