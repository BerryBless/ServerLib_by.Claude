using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const int Port = 9000;

var metrics = new ServerMetrics();
var listener = new SocketPipelineListener();
var test = 0;
long windowPackets = 0;
using var cts = new CancellationTokenSource();

listener.OnClientConnected = session =>
{
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  (sessions: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (sessions: {metrics.ConnectedCount})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics.OnPacketReceived();
    Interlocked.Increment(ref windowPackets);

    if (packetId == IncrementPacket.Id)
        Interlocked.Increment(ref test);
    else if (packetId == DecrementPacket.Id)
        Interlocked.Decrement(ref test);

    return ValueTask.CompletedTask;
};

listener.Start(Port);
Console.WriteLine($"[Server] port {Port} — 증가(Id={IncrementPacket.Id}) / 감소(Id={DecrementPacket.Id}). Enter to stop.");

_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(10000, cts.Token); }
        catch (OperationCanceledException) { break; }

        long count = Interlocked.Exchange(ref windowPackets, 0);
        Console.WriteLine($"[Monitor] sessions={metrics.ConnectedCount}  packets/10s={count:N0}  test={Volatile.Read(ref test)}");
    }
});

Console.ReadLine();
cts.Cancel();

listener.Stop();
Console.WriteLine($"종료  total={metrics.TotalPacketsReceived}  final test={test}");
