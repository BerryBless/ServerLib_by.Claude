using System.Buffers;
using System.Diagnostics; // Stopwatch.GetTimestamp: 측정 윈도의 경과 시간(처리량 산출용) — DateTime보다 저오버헤드 고해상도 타임스탬프
using AppConfig;
using Microsoft.Extensions.Configuration;
using ServerLib;                              // ServerNet 팩토리: 구현체(internal) 대신 IClientConnection 생성
using ServerLib.Core.Memory;                  // PacketPool: 헤더 크기·파싱 유틸(public 빌딩블록)
using ServerLib.Core.Serialization;           // BinaryPacketSerializer / IPacketSerializer(public)
using ServerLib.Core.Serialization.Packets;   // DamagePacket / MobHpPacket / MobDeathPacket
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

// BinaryPacketSerializer: 내부 상태 없음(Thread-safe) — 모든 공격 스레드에서 공유 가능
var serializer = new BinaryPacketSerializer();

string modeDesc = sendCount is null
    ? "무한 루프 (Ctrl+C로 종료)"
    : $"스레드당 {sendCount:N0}회 전송 후 종료";
Console.WriteLine($"{threadCount}개 스레드 시작 — {modeDesc}");
Console.WriteLine($"  스레드별 공격력: 10~30 사이클 (T0=10, T1=15, T2=20, T3=25, T4=30, T5=10...)");

var tasks = Enumerable.Range(0, threadCount).Select(async i =>
{
    // 스레드마다 고정 공격력 부여: 딜이 달라야 MVP 집계가 의미를 가짐
    int damage = 10 + (i % 5) * 5; // T0=10, T1=15, T2=20, T3=25, T4=30
    var dmgPkt = new DamagePacket { Amount = damage };
    int pktSize = PacketPool.HeaderSize + dmgPkt.GetBodySize(); // 4(헤더) + 4(int Amount) = 8B

    // ArrayPool<byte>.Shared: TLS 슬롯 우선 확인 후 공유 버킷 대여 — hot loop에서 new byte[] 없이 O(1) 반환
    var dmgBuf = ArrayPool<byte>.Shared.Rent(pktSize);

    // 패킷을 1회만 직렬화해 버퍼 재사용 — 동일 공격력 패킷을 루프마다 다시 직렬화하지 않아 CPU·Alloc 절감
    serializer.Serialize(dmgPkt, dmgBuf);
    var dmgMem = dmgBuf.AsMemory(0, pktSize);
    var ct = cts.Token;
    long total = 0;

    try
    {
        await using IClientConnection conn = ServerNet.CreateClient();
        if (cfg.SendTimeoutSeconds > 0)
            conn.SendTimeout = TimeSpan.FromSeconds(cfg.SendTimeoutSeconds);
        if (cfg.Features.EnableHeartbeat)
            conn.PingInterval = TimeSpan.FromSeconds(cfg.PingIntervalSeconds);

        conn.OnConnected = () =>
        {
            Console.WriteLine($"  [T{i}] connected  damage={damage}");
            return ValueTask.CompletedTask;
        };
        conn.OnDisconnected = () =>
        {
            Console.WriteLine($"  [T{i}] disconnected  total={total:N0}");
            return ValueTask.CompletedTask;
        };

        // OnReceived: 서버→클라 방향 MobHpPacket / MobDeathPacket 처리.
        // T0만 HP 바를 출력해 콘솔 스팸 방지 — 사망 패킷은 모든 스레드가 출력(저빈도).
        conn.OnReceived = data =>
        {
            if (!PacketPool.TryParseHeader(data.Span, out ushort pktId, out _))
                return ValueTask.CompletedTask;

            if (pktId == MobHpPacket.Id && i == 0)
            {
                // T0만 HP 진행 바를 출력 — 200ms 주기 브로드캐스트에서 스레드마다 출력하면 콘솔 범람
                var hp = serializer.Deserialize<MobHpPacket>(data.Span);
                int barLen = hp.MaxHp > 0 ? (int)(hp.Hp * 30 / hp.MaxHp) : 0;
                barLen = Math.Clamp(barLen, 0, 30);
                string bar = new string('█', barLen) + new string('░', 30 - barLen);
                Console.WriteLine($"  [HP] [{bar}] {hp.Hp:N0}/{hp.MaxHp:N0}  gen={hp.Generation}");
            }
            else if (pktId == MobDeathPacket.Id)
            {
                // 사망은 저빈도 — 모든 스레드가 출력해 각 클라이언트 관점의 처치 통보를 시연
                var death = serializer.Deserialize<MobDeathPacket>(data.Span);
                Console.WriteLine($"  [처치] T{i}  gen={death.Generation}  MVP={death.MvpName}  topDmg={death.TopDamage:N0}");
            }

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
                await conn.SendAsync(dmgMem, ct);
            }
        }
    }
    finally
    {
        // 취소·예외 경로에서도 풀 버퍼를 반드시 반납
        ArrayPool<byte>.Shared.Return(dmgBuf);
    }

    return total; // [측정] 스레드별 실제 전송 패킷 수 — bytesPerPacket 분모 집계용
}).ToArray();

// [측정] 측정 윈도 시작점: 태스크 생성 후·WhenAll 전 기준점 캡처.
// GC.GetTotalAllocatedBytes(true): 모든 스레드 누적 할당을 정밀 집계 — 송신 hot path alloc을 직접 측정
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

Console.WriteLine("모든 스레드 종료.");
// [CLIENTSTATS]: 하네스가 머신 파싱하는 측정 신호(ASCII·고정 key=value). 송신 경로 할당률(bytesPerPacket)이 1순위 지표.
Console.WriteLine($"[CLIENTSTATS] sent={grandTotal} allocBytes={allocDelta} bytesPerPacket={bytesPerPacket:F2} " +
                  $"gen0={GC.CollectionCount(0) - gen0Start} gen1={GC.CollectionCount(1) - gen1Start} gen2={GC.CollectionCount(2) - gen2Start} " +
                  $"elapsedMs={elapsedMs:F0} pktPerSec={(elapsedMs > 0 ? grandTotal / (elapsedMs / 1000.0) : 0):F0}");
