using System.Buffers;
using Microsoft.Extensions.Configuration;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();
var cfg = config.GetSection("Client").Get<ClientConfig>() ?? new ClientConfig();

string Host = cfg.Host;
int Port = cfg.Port;
int BatchSize = cfg.BatchSize;  // 회당 전송 수 (진행 출력 단위)

if (args.Length > 0 && (!int.TryParse(args[0], out _) || int.Parse(args[0]) < 1))
{
    Console.Error.WriteLine("사용법: Client [스레드 수] [스레드당 전송 횟수]  (기본값: 스레드 4, 횟수 무한)");
    return;
}
if (args.Length > 1 && (!long.TryParse(args[1], out _) || long.Parse(args[1]) < 1))
{
    Console.Error.WriteLine("사용법: Client [스레드 수] [스레드당 전송 횟수]  (기본값: 스레드 4, 횟수 무한)");
    return;
}
int threadCount = args.Length > 0 ? int.Parse(args[0]) : cfg.DefaultThreadCount;
long? sendCount = args.Length > 1 ? long.Parse(args[1]) : null;

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
string modeDesc = sendCount is null
    ? "무한 루프 (Ctrl+C로 종료)"
    : $"스레드당 {sendCount:N0}회 전송 후 종료";
Console.WriteLine($"{threadCount}개 스레드 시작 — {modeDesc}");
Console.WriteLine($"  증가 스레드: {incThreads}개, 감소 스레드: {decThreads}개  (배치={BatchSize})");

var tasks = Enumerable.Range(0, threadCount).Select(async i =>
{
    bool isIncrement = i < incThreads;
    var label = isIncrement ? "증가" : "감소";
    var sendMem = isIncrement ? incMem : decMem;
    var ct = cts.Token;
    long total = 0;

    await using var conn = new SocketPipelineClient();
    if (cfg.Features.EnableHeartbeat)
        conn.PingInterval = TimeSpan.FromSeconds(cfg.PingIntervalSeconds); // 자동 PING → RTT 측정
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

    if (i == 0 && cfg.Features.EnableRttDisplay)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(cfg.RttDisplayIntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
                Console.WriteLine($"  [T0] RTT={conn.Rtt.TotalMilliseconds:F1}ms");
            }
        });
    }

    while (!ct.IsCancellationRequested && (sendCount is null || total < sendCount))
    {
        long batchEnd = sendCount is null
            ? total + BatchSize
            : Math.Min(total + BatchSize, sendCount.Value);
        for (; total < batchEnd && !ct.IsCancellationRequested; total++)
        {
            await conn.SendAsync(sendMem, ct);
        }
        Console.WriteLine($"  [T{i}] {label} {total:N0}회 전송");
    }
}).ToArray();

await Task.WhenAll(tasks);

ArrayPool<byte>.Shared.Return(incBuf);
ArrayPool<byte>.Shared.Return(decBuf);

Console.WriteLine("모든 스레드 종료.");
