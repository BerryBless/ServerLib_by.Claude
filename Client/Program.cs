using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const string Host = "127.0.0.1";
const int Port = 9000;
const int SendCount = 1000;

var serializer = new BinaryPacketSerializer();

// 패킷 미리 직렬화 (4바이트 헤더만, 본문 없음)
var incPacket = new IncrementPacket();
var decPacket = new DecrementPacket();
var incBuf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize);
var decBuf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize);
serializer.Serialize(incPacket, incBuf);
serializer.Serialize(decPacket, decBuf);
var incMem = incBuf.AsMemory(0, PacketPool.HeaderSize);
var decMem = decBuf.AsMemory(0, PacketPool.HeaderSize);

Console.WriteLine($"4개 스레드 시작: 스레드 0·1 → 증가×{SendCount}, 스레드 2·3 → 감소×{SendCount}");

// 4개 스레드: 0·1은 증가, 2·3은 감소
var tasks = Enumerable.Range(0, 4).Select(async i =>
{
    bool isIncrement = i < 2;
    var label = isIncrement ? "증가" : "감소";
    var sendMem = isIncrement ? incMem : decMem;

    await using var conn = new SocketPipelineClient();
    conn.OnConnected = () =>
    {
        Console.WriteLine($"  [Thread {i}] connected ({label})");
        return ValueTask.CompletedTask;
    };
    conn.OnDisconnected = () =>
    {
        Console.WriteLine($"  [Thread {i}] disconnected");
        return ValueTask.CompletedTask;
    };

    await conn.ConnectAsync(Host, Port);

    for (int j = 0; j < SendCount; j++)
        await conn.SendAsync(sendMem);

    Console.WriteLine($"  [Thread {i}] {label} {SendCount}회 완료");
}).ToArray();

await Task.WhenAll(tasks);

ArrayPool<byte>.Shared.Return(incBuf);
ArrayPool<byte>.Shared.Return(decBuf);

Console.WriteLine($"완료  전송={SendCount * 4}  (증가={SendCount * 2} + 감소={SendCount * 2})  예상 final test=0");
