using System.Buffers;
using System.Diagnostics; // Stopwatch.GetTimestamp: 측정 윈도의 경과 시간(처리량 산출용) — DateTime보다 저오버헤드 고해상도 타임스탬프
using System.Threading.Channels; // Channel<T>: 락-프리 MPSC 큐로 OnReceived → 데모 루프 비동기 전달
using AppConfig;
using Microsoft.Extensions.Configuration;
using ServerLib;                              // ServerNet 팩토리: 구현체(internal) 대신 IClientConnection 생성
using ServerLib.Core.Memory;                  // PacketPool: 헤더 크기·파싱 유틸(public 빌딩블록)
using ServerLib.Core.Serialization;           // BinaryPacketSerializer / IPacketSerializer(public)
using ServerLib.Core.Serialization.Packets;   // DamagePacket / MobHpPacket / MobDeathPacket / Ticket*
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

// 티켓팅 데모 모드: 공격 루프 대신 선착순 예약·결제 흐름을 시연하고 즉시 반환
if (cfg.Features.EnableTicketing)
{
    await RunTicketingDemoAsync(cfg, serializer, cts.Token);
    return;
}

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

        // OnReceived: 서버→클라 방향 MobHpPacket / MobDeathPacket / LoginResponsePacket 처리.
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
            else if (pktId == LoginResponsePacket.Id && i == 0)
            {
                // T0만 로그인 결과 출력 — 스탠드얼론 설계: 결과와 무관하게 공격 루프 계속 진행
                var resp = serializer.Deserialize<LoginResponsePacket>(data.Span);
                var mark = resp.Success ? "✓" : "✗";
                var tokenSnip = resp.Success && resp.Token.Length >= 8 ? resp.Token[..8] + "..." : "(없음)";
                Console.WriteLine($"  [LOGIN] T0 {mark}  token={tokenSnip}");
            }

            return ValueTask.CompletedTask;
        };

        await conn.ConnectAsync(Host, Port, ct);

        // 로그인 prelude: T0가 공격 루프 시작 전 LoginRequestPacket 1회 전송(스탠드얼론 — 결과와 무관하게 공격 계속)
        if (i == 0 && cfg.Features.EnableLogin)
        {
            var loginPkt = new LoginRequestPacket { Username = cfg.Login.Username, Password = cfg.Login.Password };
            await conn.SendAsync(loginPkt, ct);
            Console.WriteLine($"  [LOGIN] T0 로그인 요청 전송  user={cfg.Login.Username}");
        }

        // AuthGating prelude: T0가 AuthServer(cfg.AuthPort)에 먼저 로그인한 뒤 토큰을 게임 서버에 제시.
        // EnableAuthGating=false(기본)이면 이 블록이 스킵 → 기존 attack-loop 무변경 동작.
        if (i == 0 && cfg.Features.EnableAuthGating)
        {
            // TaskCompletionSource: authConn.OnReceived 콜백에서 토큰을 캡처해 await 지점으로 전달
            // RunContinuationsAsynchronously: TCS 완료 시 콜백 스레드를 즉시 해제 — I/O 스레드 블로킹 방지
            var tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // await using: AuthServer 전용 연결 — 토큰 수신 후 자동 Dispose(TCP 연결 해제)
            await using (var authConn = ServerNet.CreateClient())
            {
                authConn.OnReceived = authData =>
                {
                    if (PacketPool.TryParseHeader(authData.Span, out ushort pid, out _)
                        && pid == LoginResponsePacket.Id)
                    {
                        var resp = serializer.Deserialize<LoginResponsePacket>(authData.Span);
                        // TrySetResult: 1회만 완료 — 재전송 보호
                        tokenTcs.TrySetResult(resp.Success ? resp.Token : string.Empty);
                    }
                    return ValueTask.CompletedTask;
                };

                await authConn.ConnectAsync(cfg.Host, cfg.AuthPort, ct);
                await authConn.SendAsync(
                    new LoginRequestPacket { Username = cfg.Login.Username, Password = cfg.Login.Password }, ct);
                Console.WriteLine($"  [AUTHGATE] T0 AuthServer(:{cfg.AuthPort})에 로그인 요청  user={cfg.Login.Username}");

                // WaitAsync: 10초 타임아웃 — AuthServer 미응답 시 공격 루프로 계속 진행(스탠드얼론 설계)
                string token;
                try { token = await tokenTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
                catch (TimeoutException) { token = string.Empty; Console.WriteLine("  [AUTHGATE] T0 토큰 수신 타임아웃 — 인증 없이 진행"); }
                // authConn: await using 블록 종료 시 DisposeAsync() → AuthServer TCP 연결 해제

                if (!string.IsNullOrEmpty(token))
                {
                    var snip = token.Length >= 8 ? token[..8] + "..." : token;
                    Console.WriteLine($"  [AUTHGATE] T0 토큰 수신 성공: {snip}");
                    // AuthTokenPacket(Id=12): 게임 서버에 토큰 제시 → 서버가 Redis 검증 후 LoginResponsePacket으로 ack
                    await conn.SendAsync(new AuthTokenPacket { Token = token }, ct);
                    Console.WriteLine($"  [AUTHGATE] T0 게임 서버에 AuthTokenPacket 제시");
                }
            }
        }

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

// ──────────── 티켓팅 데모 로컬 함수 ────────────
static async Task RunTicketingDemoAsync(ClientConfig cfg, BinaryPacketSerializer serializer, CancellationToken ct)
{
    int clientCount = cfg.Ticketing.ClientCount;
    int failingIdx  = cfg.Ticketing.FailingClientIndex;
    int headStartMs = cfg.Ticketing.FailerHeadStartMs;

    Console.WriteLine($"\n[TICKET] 티켓팅 데모 시작 — 클라이언트={clientCount}  실패클라={failingIdx}  headStart={headStartMs}ms");

    int confirmed = 0, soldOut = 0, failed = 0;

    async Task RunClient(int i)
    {
        bool isFailer = (i == failingIdx);
        string username = $"user{i}";

        // Channel<byte[]>: 락-프리 큐로 OnReceived 콜백에서 복사한 패킷을 순서대로 버퍼링.
        // UnboundedChannel: 서버 응답 누적이 ~10패킷 이하이므로 백프레셔 불필요
        var inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        await using var conn = ServerNet.CreateClient();
        if (cfg.SendTimeoutSeconds > 0)
            conn.SendTimeout = TimeSpan.FromSeconds(cfg.SendTimeoutSeconds);

        conn.OnReceived = data =>
        {
            // data.Span은 콜백 반환 후 Pipe 내부 버퍼가 재사용되면 무효 → 즉시 byte[]로 복사 후 채널에 쓰기
            inbox.Writer.TryWrite(data.Span.ToArray());
            return ValueTask.CompletedTask;
        };
        conn.OnDisconnected = () =>
        {
            // TryComplete → 대기 중인 ReadAsync가 ChannelClosedException(InvalidOperationException 파생)을 throw한다.
            // OCE가 아님 — ReadNextAsync의 catch(Exception)에서 처리됨.
            inbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        };

        // 10초 타임아웃으로 다음 패킷 읽기 — 서버 무응답 시 데모가 영구 대기하는 것을 방지
        async ValueTask<byte[]> ReadNextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await inbox.Reader.ReadAsync(linked.Token);
        }

        try
        {
            await conn.ConnectAsync(cfg.Host, cfg.Port, ct);
            Console.WriteLine($"  [{i}] 접속{(isFailer ? " (failer)" : "")}  user={username}");

            // ① 더미 로그인: 비번 없이 아이디만 전송
            await conn.SendAsync(new LoginRequestPacket { Username = username, Password = string.Empty }, ct);
            var loginRaw  = await ReadNextAsync();
            if (!PacketPool.TryParseHeader(loginRaw, out ushort loginPktId, out _) || loginPktId != LoginResponsePacket.Id)
            {
                Console.WriteLine($"  [{i}] 예상치 못한 로그인 응답 — 종료");
                return;
            }
            var loginResp = serializer.Deserialize<LoginResponsePacket>(loginRaw);
            if (!loginResp.Success) { Console.WriteLine($"  [{i}] 로그인 실패"); return; }
            Console.WriteLine($"  [{i}] 로그인 성공");

            // ② 예약 요청
            await conn.SendAsync(new TicketReserveRequestPacket(), ct);
            var resRaw    = await ReadNextAsync();
            var resResult = serializer.Deserialize<TicketResultPacket>(resRaw);
            Console.WriteLine($"  [{i}] 예약={resResult.Status}  slot={resResult.Slot}  remaining={resResult.Remaining}");

            if (resResult.Status != TicketStatus.Reserved)
            {
                Interlocked.Increment(ref soldOut);
                return;
            }

            // ③ 결제 요청 (failer는 의도적 실패)
            await conn.SendAsync(new TicketPayRequestPacket { SimulateFailure = isFailer }, ct);
            var payRaw    = await ReadNextAsync();
            var payResult = serializer.Deserialize<TicketResultPacket>(payRaw);
            Console.WriteLine($"  [{i}] 결제={payResult.Status}  slot={payResult.Slot}  remaining={payResult.Remaining}");

            if (payResult.Status == TicketStatus.Confirmed)
            {
                Interlocked.Increment(ref confirmed);
                return;
            }

            if (payResult.Status == TicketStatus.PaymentFailed && isFailer)
            {
                // ④ Failer 재예약: 슬롯이 방금 반납됐으므로 즉시 재시도
                Console.WriteLine($"  [{i}] 결제 실패(의도적) — 슬롯 반납됨, 재예약 시도");
                await conn.SendAsync(new TicketReserveRequestPacket(), ct);
                var retryResRaw = await ReadNextAsync();
                var retryRes    = serializer.Deserialize<TicketResultPacket>(retryResRaw);
                Console.WriteLine($"  [{i}] 재예약={retryRes.Status}  slot={retryRes.Slot}  remaining={retryRes.Remaining}");

                if (retryRes.Status == TicketStatus.Reserved)
                {
                    // ⑤ Failer 재결제
                    await conn.SendAsync(new TicketPayRequestPacket { SimulateFailure = false }, ct);
                    var retryPayRaw = await ReadNextAsync();
                    var retryPay    = serializer.Deserialize<TicketResultPacket>(retryPayRaw);
                    Console.WriteLine($"  [{i}] 재결제={retryPay.Status}  slot={retryPay.Slot}  ← Failer 최종");
                    if (retryPay.Status == TicketStatus.Confirmed)
                        Interlocked.Increment(ref confirmed);
                    else
                        Interlocked.Increment(ref failed);
                }
                else
                {
                    Console.WriteLine($"  [{i}] 재예약 실패({retryRes.Status}) — 슬롯 이미 소진");
                    Interlocked.Increment(ref soldOut);
                }
            }
            else
            {
                Interlocked.Increment(ref failed);
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"  [{i}] 타임아웃 또는 취소"); }
        catch (Exception ex) { Console.WriteLine($"  [{i}] 오류: {ex.GetType().Name}: {ex.Message}"); }
    }

    // Failer에게 headStartMs만큼 선행 접속·예약 기회를 준다 → 슬롯 획득 보장
    var failerTask = RunClient(failingIdx);
    try { await Task.Delay(headStartMs, ct); } catch (OperationCanceledException) { }

    // 나머지 클라이언트 동시 실행
    var otherTasks = Enumerable.Range(0, clientCount)
        .Where(i => i != failingIdx)
        .Select(i => RunClient(i))
        .ToArray();

    await Task.WhenAll(otherTasks.Append(failerTask));

    Console.WriteLine($"\n[TICKET] 최종 결과 — Confirmed={confirmed}  SoldOut={soldOut}  Failed={failed}");
    Console.WriteLine($"[TICKET] 불변식: Confirmed({confirmed}) == min(ClientCount({clientCount}), TotalTickets)");
}
