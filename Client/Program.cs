using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const string Host = "127.0.0.1";
const int Port = 9000;
const int BatchSize = 1000;  // 회당 전송 수 (진행 출력 단위)

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n[Ctrl+C] 종료 신호 수신 — 스레드 정리 중...");
};

var serializer = new BinaryPacketSerializer();

var incPacket = new IncrementPacket();
var decPacket = new DecrementPacket();
var incBuf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize);
var decBuf = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize);
serializer.Serialize(incPacket, incBuf);
serializer.Serialize(decPacket, decBuf);
var incMem = incBuf.AsMemory(0, PacketPool.HeaderSize);
var decMem = decBuf.AsMemory(0, PacketPool.HeaderSize);

Console.WriteLine($"4개 스레드 무한 루프 시작 (Ctrl+C로 종료)");
Console.WriteLine($"  스레드 0·1 → 증가, 스레드 2·3 → 감소  (배치={BatchSize})");

var tasks = Enumerable.Range(0, 4).Select(async i =>
{
    bool isIncrement = i < 2;
    var label = isIncrement ? "증가" : "감소";
    var sendMem = isIncrement ? incMem : decMem;
    var ct = cts.Token;
    long total = 0;

    await using var conn = new SocketPipelineClient();
    conn.OnConnected = () =>
    {
        Console.WriteLine($"  [T{i}] connected");
        return ValueTask.CompletedTask;
    };
    conn.OnDisconnected = () =>
    {
        Console.WriteLine($"  [T{i}] disconnected  total={total:N0}");
        return ValueTask.CompletedTask;
    };

    await conn.ConnectAsync(Host, Port, ct);

    for (int j = 0; j < SendCount; j++)
        await conn.SendAsync(sendMem);

    Console.WriteLine($"  [Thread {i}] {label} {SendCount}회 완료");
}).ToArray();

await Task.WhenAll(tasks);

ArrayPool<byte>.Shared.Return(incBuf);
ArrayPool<byte>.Shared.Return(decBuf);

Console.WriteLine("모든 스레드 종료.");
