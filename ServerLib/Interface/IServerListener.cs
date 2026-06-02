namespace ServerLib.Interface;

public interface IServerListener
{
    bool IsRunning { get; }
    Func<ISession, ValueTask>? OnClientConnected { get; set; }
    Func<ISession, ValueTask>? OnClientDisconnected { get; set; }
    Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    void Start(int port);
    void Stop();
}
