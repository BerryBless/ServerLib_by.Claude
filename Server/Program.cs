using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const int Port = 9000;

var metrics = new ServerMetrics();
var listener = new SocketPipelineListener();
var test = 0;

listener.OnClientConnected = session =>
{
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  (total: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (total: {metrics.ConnectedCount})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics.OnPacketReceived();

    if (packetId == IncrementPacket.Id)
        Interlocked.Increment(ref test);
    else if (packetId == DecrementPacket.Id)
        Interlocked.Decrement(ref test);

    var total = metrics.TotalPacketsReceived;
    if (total % 500 == 0)
        Console.WriteLine($"  packets={total}  test={Volatile.Read(ref test)}");

    return ValueTask.CompletedTask;
};

listener.Start(Port);
Console.WriteLine($"[Server] port {Port} — 증가(Id={IncrementPacket.Id}) / 감소(Id={DecrementPacket.Id}). Enter to stop.");
Console.ReadLine();

listener.Stop();
Console.WriteLine($"종료  total={metrics.TotalPacketsReceived}  final test={test}");
