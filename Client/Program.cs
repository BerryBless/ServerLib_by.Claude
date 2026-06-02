using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const string Host = "127.0.0.1";
const int Port = 9000;

var serializer = new BinaryPacketSerializer();
var receivedCount = 0;
await using var connection = new SocketPipelineClient();

connection.OnConnected = () =>
{
    Console.WriteLine($"[ServerLib] Connected to {Host}:{Port}");
    return ValueTask.CompletedTask;
};

connection.OnDisconnected = () =>
{
    Console.WriteLine($"Disconnected. Total received: {receivedCount}");
    return ValueTask.CompletedTask;
};

connection.OnReceived = data =>
{
    Interlocked.Increment(ref receivedCount);
    if (PacketPool.TryParseHeader(data.Span, out ushort packetId, out _) && packetId == EchoPacket.Id)
    {
        var packet = serializer.Deserialize<EchoPacket>(data.Span);
        Console.WriteLine($"[Echo #{receivedCount}] \"{packet.Message}\"");
    }
    return ValueTask.CompletedTask;
};

await connection.ConnectAsync(Host, Port);

// 자동 메시지 3개 전송
string[] autoMessages = ["Hello", "World", "Echo Test"];
foreach (var msg in autoMessages)
{
    await SendEchoAsync(msg);
    await Task.Delay(100);
}

// 이후 대화형 모드
Console.WriteLine("\nType messages (empty line to quit):");
while (connection.IsConnected)
{
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) break;
    await SendEchoAsync(input);
}

async Task SendEchoAsync(string message)
{
    var packet = new EchoPacket { Message = message };
    int totalSize = PacketPool.HeaderSize + packet.GetBodySize();
    var rented = ArrayPool<byte>.Shared.Rent(totalSize);
    try
    {
        int written = serializer.Serialize(packet, rented);
        await connection.SendAsync(rented.AsMemory(0, written));
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rented);
    }
}
