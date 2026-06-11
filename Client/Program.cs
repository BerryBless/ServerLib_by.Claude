using System.Buffers;
using System.Diagnostics; // Stopwatch.GetTimestamp: 측정 윈도의 경과 시간(처리량 산출용) — DateTime보다 저오버헤드 고해상도 타임스탬프
using AppConfig;
using Microsoft.Extensions.Configuration;
using ServerLib;                              // ServerNet 팩토리: 구현체(internal) 대신 IClientConnection 생성
using ServerLib.Core.Memory;                  // PacketPool: 헤더 크기·파싱 유틸(public 빌딩블록)
using ServerLib.Core.Serialization;           // BinaryPacketSerializer / IPacketSerializer(public)
using ServerLib.Core.Serialization.Packets;   // IncrementPacket/DecrementPacket: 예제 패킷 타입(public)
using ServerLib.Interface;                     // IClientConnection

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

// 참고(E1): 단발성 송신은 conn.SendAsync(packet) 편의 오버로드(PacketSendExtensions)로 직렬화·버퍼 관리를 캡슐화할 수 있다.
// 단, 아래 hot loop는 패킷을 1회만 직렬화해 buffer를 재사용하는 무할당 패턴이 더 유리하므로 그대로 둔다.
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

    await using IClientConnection conn = ServerNet.CreateClient();
    // SendTimeoutSeconds=0이면 비활성 → 송신당 CTS 미할당(A/B 측정 토글). >0이면 응답불능 서버 송신 무한 블록 방지.
    if (cfg.SendTimeoutSeconds > 0)
        conn.SendTimeout = TimeSpan.FromSeconds(cfg.SendTimeoutSeconds);
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
    return total; // [측정] 스레드별 실제 전송 패킷 수 — bytesPerPacket 분모 집계용
}).ToArray();

// [측정] 측정 윈도 시작점: 부하 직전 누적 할당 바이트·GC 카운트·고해상도 타임스탬프를 캡처한다.
// GC.GetTotalAllocatedBytes(true): 모든 스레드의 누적 할당을 정밀 집계(true=GC 강제로 보류분까지 반영) → 송신당 CTS 할당 같은 핫패스 alloc을 직접 본다.
long allocStart = GC.GetTotalAllocatedBytes(precise: true);
int gen0Start = GC.CollectionCount(0);
int gen1Start = GC.CollectionCount(1);
int gen2Start = GC.CollectionCount(2);
long tsStart = Stopwatch.GetTimestamp();

long[] perThread = await Task.WhenAll(tasks);

double elapsedMs = Stopwatch.GetElapsedTime(tsStart).TotalMilliseconds;
long allocDelta = GC.GetTotalAllocatedBytes(precise: true) - allocStart;
long grandTotal = 0;
foreach (var t in perThread) grandTotal += t;
double bytesPerPacket = grandTotal > 0 ? (double)allocDelta / grandTotal : 0;

ArrayPool<byte>.Shared.Return(incBuf);
ArrayPool<byte>.Shared.Return(decBuf);

Console.WriteLine("모든 스레드 종료.");
// [CLIENTSTATS]: 하네스가 머신 파싱하는 측정 신호(ASCII·고정 key=value). 송신 경로 할당률(bytesPerPacket)이 1순위 지표.
Console.WriteLine($"[CLIENTSTATS] sent={grandTotal} allocBytes={allocDelta} bytesPerPacket={bytesPerPacket:F2} " +
                  $"gen0={GC.CollectionCount(0) - gen0Start} gen1={GC.CollectionCount(1) - gen1Start} gen2={GC.CollectionCount(2) - gen2Start} " +
                  $"elapsedMs={elapsedMs:F0} pktPerSec={(elapsedMs > 0 ? grandTotal / (elapsedMs / 1000.0) : 0):F0}");
