using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

const string Host = "127.0.0.1";
const int Port = 9000;
const int BatchSize = 1000;  // 회당 전송 수 (진행 출력 단위)

if (args.Length > 0 && (!int.TryParse(args[0], out _) || int.Parse(args[0]) < 1))
{
    Console.Error.WriteLine("사용법: Client [스레드 수]  (기본값: 4)");
    return;
}
int threadCount = args.Length > 0 ? int.Parse(args[0]) : 4;

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

int incThreads = threadCount / 2;
int decThreads = threadCount - incThreads;
Console.WriteLine($"{threadCount}개 스레드 무한 루프 시작 (Ctrl+C로 종료)");
Console.WriteLine($"  증가 스레드: {incThreads}개, 감소 스레드: {decThreads}개  (배치={BatchSize})");

var tasks = Enumerable.Range(0, threadCount).Select(async i =>
{
    bool isIncrement = i < incThreads;
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

    while (!ct.IsCancellationRequested)
    {
        for (int j = 0; j < BatchSize && !ct.IsCancellationRequested; j++)
        {
            await conn.SendAsync(sendMem, ct);
            total++;
        }
        Console.WriteLine($"  [T{i}] {label} {total:N0}회 전송");
    }
}).ToArray();

await Task.WhenAll(tasks);

ArrayPool<byte>.Shared.Return(incBuf);
ArrayPool<byte>.Shared.Return(decBuf);

Console.WriteLine("모든 스레드 종료.");
