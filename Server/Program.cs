using System.Buffers;
using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;
using ServerLib.Interface;

const int Port = 9000;

var metrics = new ServerMetrics();
var serializer = new BinaryPacketSerializer();
var listener = new SocketPipelineListener();

listener.OnClientConnected = session =>
{
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  (total: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (total: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnReceived = async (session, data) =>
{
    metrics.OnPacketReceived();
    metrics.OnBytesReceived(data.Length);

    // 헤더에서 PacketId 읽어 라우팅
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return;

    if (packetId == EchoPacket.Id)
    {
        var packet = serializer.Deserialize<EchoPacket>(data.Span);
        Console.WriteLine($"[{session.RemoteEndPoint}] Echo: \"{packet.Message}\"");

        // 동일 패킷을 직렬화하여 에코 응답
        int totalSize = PacketPool.HeaderSize + packet.GetBodySize();
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            int written = serializer.Serialize(packet, rented);
            await session.SendAsync(rented.AsMemory(0, written));
            metrics.OnBytesSent(written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
    else if (packetId == ChatPacket.Id)
    {
        var packet = serializer.Deserialize<ChatPacket>(data.Span);
        Console.WriteLine($"[{session.RemoteEndPoint}] Chat [{packet.Sender}]: {packet.Content}");
    }
};

listener.Start(Port);
Console.WriteLine($"[ServerLib] Echo server on port {Port}. Press Enter to stop.");
Console.ReadLine();

listener.Stop();
Console.WriteLine($"Server stopped. Total packets: {metrics.TotalPacketsReceived}");
