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
    int clientCount    = cfg.Ticketing.ClientCount;
    int failingIdx     = cfg.Ticketing.FailingClientIndex;
    int headStartMs    = cfg.Ticketing.FailerHeadStartMs;
    int seatsPerClient = cfg.Ticketing.SeatsPerClient;  // 클라당 배치 예약 좌석 수
    // 0=Free 상수 — 좌석맵 States 배열에서 예약 가능한 좌석을 판별하는 매직 넘버를 명시
    const byte SeatFree = 0;

    Console.WriteLine($"\n[TICKET] 좌석지정 배치 티켓팅 데모 시작 — 클라이언트={clientCount}  seatsPerClient={seatsPerClient}  실패클라={failingIdx}  headStart={headStartMs}ms");
    Console.WriteLine($"         흐름: 로그인 → 좌석맵 조회 → {seatsPerClient}석 배치예약(SeatTaken 시 재선택) → 일괄결제");

    // confirmedSeats: 좌석 수 누계 (클라이언트 수가 아님)
    int confirmedSeats = 0, soldOut = 0, failed = 0;
    // serverTotalSeats: 최초 좌석맵 응답에서 파악한 서버 총 좌석 수(모니터링용)
    int serverTotalSeats = 0;

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

        // 좌석맵 조회 후 k개의 빈 좌석을 선택한다. preferSeatIds가 있으면 우선 시도.
        // 반환: (rows[], cols[], serverCols) 또는 null (빈 좌석 k개 미만)
        async ValueTask<(byte[] rows, byte[] cols, int serverCols)?> RequestSeatMapAndPickFreeSeatsAsync(
            int k, int[]? preferSeatIds = null)
        {
            await conn.SendAsync(new SeatMapRequestPacket(), ct);
            var mapRaw = await ReadNextAsync();
            if (!PacketPool.TryParseHeader(mapRaw, out ushort mapId, out _) || mapId != SeatMapResponsePacket.Id)
                return null;
            var mapResp = serializer.Deserialize<SeatMapResponsePacket>(mapRaw);
            if (mapResp.Rows == 0 || mapResp.Cols == 0 || mapResp.States.Length == 0) return null;

            // 처음 조회한 클라이언트가 서버 총 좌석 수를 기록 (불변식 출력용)
            Interlocked.CompareExchange(ref serverTotalSeats, mapResp.Rows * mapResp.Cols, 0);

            int total = mapResp.Rows * mapResp.Cols;
            int serverCols = mapResp.Cols;
            var rows  = new byte[k];
            var cols  = new byte[k];
            // used: k개 선택된 seatId 추적 (중복 방지)
            var used  = new HashSet<int>(k);
            int found = 0;

            // 선호 좌석 우선 선택
            if (preferSeatIds is not null)
            {
                foreach (int sid in preferSeatIds)
                {
                    if (found >= k) break;
                    if (sid >= 0 && sid < total && mapResp.States[sid] == SeatFree && !used.Contains(sid))
                    {
                        rows[found] = (byte)(sid / mapResp.Cols);
                        cols[found] = (byte)(sid % mapResp.Cols);
                        used.Add(sid);
                        found++;
                    }
                }
            }
            // 부족분을 앞에서부터 순서대로 채움
            for (int s = 0; s < total && found < k; s++)
            {
                if (mapResp.States[s] != SeatFree || used.Contains(s)) continue;
                rows[found] = (byte)(s / mapResp.Cols);
                cols[found] = (byte)(s % mapResp.Cols);
                used.Add(s);
                found++;
            }

            return found >= k ? (rows, cols, serverCols) : null; // k개 확보 못하면 null(빈 좌석 부족)
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

            // ② 좌석맵 조회 → seatsPerClient석 배치 예약 (SeatTaken 시 최대 5회 재시도)
            // 클라 i는 seatId=i, i+clientCount, i+2*clientCount, ... 를 선호 — 자연스러운 경합 발생
            int[] preferSeatIds = Enumerable.Range(0, seatsPerClient)
                .Select(k => i + k * clientCount).ToArray();

            TicketResultPacket resResult = default;
            int resServerCols = 3; // 기본값; 실제는 좌석맵 응답에서 갱신됨
            const int maxReserveTries = 5;
            bool reserved = false;

            for (int attempt = 0; attempt < maxReserveTries && !reserved; attempt++)
            {
                // 배치 좌석 선택: 첫 시도에는 선호 좌석, 이후에는 아무 빈 좌석
                var picked = await RequestSeatMapAndPickFreeSeatsAsync(seatsPerClient,
                    attempt == 0 ? preferSeatIds : null);
                if (picked is null)
                {
                    Console.WriteLine($"  [{i}] 좌석맵 조회 결과 빈 좌석 {seatsPerClient}개 부족(매진) — 종료");
                    Interlocked.Increment(ref soldOut);
                    return;
                }

                var (rowsBatch, colsBatch, sc) = picked.Value;
                resServerCols = sc;
                var seatLabelsReq = string.Join(",", rowsBatch.Zip(colsBatch)
                    .Select(rc => $"{(char)('A' + rc.First)}{rc.Second + 1}"));
                Console.WriteLine($"  [{i}] 좌석맵 조회 완료  목표좌석=[{seatLabelsReq}](attempt={attempt + 1})");

                // 배치 예약 요청: Count=seatsPerClient, Rows/Cols 배열
                var reserveReq = new TicketReserveRequestPacket
                {
                    Count = (byte)seatsPerClient,
                    Rows  = rowsBatch,
                    Cols  = colsBatch
                };
                await conn.SendAsync(reserveReq, ct);
                var resRaw = await ReadNextAsync();
                resResult  = serializer.Deserialize<TicketResultPacket>(resRaw);

                if (resResult.Status == TicketStatus.Reserved)
                {
                    reserved = true;
                    var seatIdList = string.Join(",", resResult.Slots
                        .Select(s => $"{(char)('A' + s / sc)}{s % sc + 1}"));
                    Console.WriteLine($"  [{i}] 예약 성공  seats=[{seatIdList}]  count={resResult.Count}  remaining={resResult.Remaining}");
                }
                else if (resResult.Status == TicketStatus.SeatTaken)
                {
                    // 배치 내 일부 좌석 점유됨 → 좌석맵 재조회 후 새 조합으로 재시도
                    Console.WriteLine($"  [{i}] 배치 점유됨(SeatTaken) — 재시도 {attempt + 1}/{maxReserveTries}");
                }
                else if (resResult.Status == TicketStatus.RateLimited)
                {
                    Console.WriteLine($"  [{i}] 속도 제한(RateLimited) — 예약 중단");
                    Interlocked.Increment(ref soldOut);
                    return;
                }
                else
                {
                    Console.WriteLine($"  [{i}] 예약 실패({resResult.Status}) — 종료");
                    Interlocked.Increment(ref soldOut);
                    return;
                }
            }

            if (!reserved)
            {
                Console.WriteLine($"  [{i}] 최대 재시도 횟수 초과 — 종료");
                Interlocked.Increment(ref soldOut);
                return;
            }

            // ③ 일괄 결제 (보유 전체 seatsPerClient석 확정)
            await conn.SendAsync(new TicketPayRequestPacket(), ct);
            var payRaw    = await ReadNextAsync();
            var payResult = serializer.Deserialize<TicketResultPacket>(payRaw);
            var paidLabels = string.Join(",", payResult.Slots
                .Select(s => $"{(char)('A' + s / resServerCols)}{s % resServerCols + 1}"));
            Console.WriteLine($"  [{i}] 결제={payResult.Status}  seats=[{paidLabels}]  count={payResult.Count}  remaining={payResult.Remaining}");

            if (payResult.Status == TicketStatus.Confirmed)
            {
                // Interlocked.Add: payResult.Count(확정 좌석 수)만큼 원자 가산
                Interlocked.Add(ref confirmedSeats, payResult.Count);
                return;
            }

            if (payResult.Status == TicketStatus.PaymentFailed && isFailer)
            {
                // ④ Failer 재예약: 좌석맵 재조회 → seatsPerClient석 재지정 → 재결제
                Console.WriteLine($"  [{i}] 결제 실패(의도적) — 좌석맵 재조회 후 {seatsPerClient}석 재예약 시도");

                for (int retryAttempt = 0; retryAttempt < maxReserveTries; retryAttempt++)
                {
                    var retryPicked = await RequestSeatMapAndPickFreeSeatsAsync(seatsPerClient);
                    if (retryPicked is null)
                    {
                        Console.WriteLine($"  [{i}] 재예약: 빈 좌석 {seatsPerClient}개 부족 — 종료");
                        Interlocked.Increment(ref soldOut);
                        return;
                    }

                    var (rRows, rCols, rsc) = retryPicked.Value;
                    var retryLabels = string.Join(",", rRows.Zip(rCols)
                        .Select(rc => $"{(char)('A' + rc.First)}{rc.Second + 1}"));
                    var retryReq = new TicketReserveRequestPacket
                    {
                        Count = (byte)seatsPerClient,
                        Rows  = rRows,
                        Cols  = rCols
                    };
                    await conn.SendAsync(retryReq, ct);
                    var retryResRaw = await ReadNextAsync();
                    var retryRes    = serializer.Deserialize<TicketResultPacket>(retryResRaw);
                    var retryIds = string.Join(",", retryRes.Slots
                        .Select(s => $"{(char)('A' + s / rsc)}{s % rsc + 1}"));
                    Console.WriteLine($"  [{i}] 재예약={retryRes.Status}  목표=[{retryLabels}]  seats=[{retryIds}]  remaining={retryRes.Remaining}");

                    if (retryRes.Status == TicketStatus.Reserved)
                    {
                        // ⑤ Failer 재결제
                        await conn.SendAsync(new TicketPayRequestPacket(), ct);
                        var retryPayRaw = await ReadNextAsync();
                        var retryPay    = serializer.Deserialize<TicketResultPacket>(retryPayRaw);
                        var retryPaidLabels = string.Join(",", retryPay.Slots
                            .Select(s => $"{(char)('A' + s / rsc)}{s % rsc + 1}"));
                        Console.WriteLine($"  [{i}] 재결제={retryPay.Status}  seats=[{retryPaidLabels}]  ← Failer 최종");
                        if (retryPay.Status == TicketStatus.Confirmed)
                            Interlocked.Add(ref confirmedSeats, retryPay.Count);
                        else
                            Interlocked.Increment(ref failed);
                        return;
                    }
                    else if (retryRes.Status == TicketStatus.SeatTaken)
                    {
                        Console.WriteLine($"  [{i}] 재예약 SeatTaken — 재시도 {retryAttempt + 1}/{maxReserveTries}");
                    }
                    else
                    {
                        Console.WriteLine($"  [{i}] 재예약 실패({retryRes.Status}) — 종료");
                        Interlocked.Increment(ref soldOut);
                        return;
                    }
                }
                Console.WriteLine($"  [{i}] Failer 재예약 최대 재시도 초과 — 종료");
                Interlocked.Increment(ref failed);
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

    int totalRequestedSeats = clientCount * seatsPerClient;
    int totalSeats          = Volatile.Read(ref serverTotalSeats);
    // 올바른 배치 상한: floor(TotalSeats / SeatsPerClient) 배치만 완전 성공 가능
    int maxBatches   = totalSeats > 0 ? totalSeats / seatsPerClient : clientCount;
    int expectedMax  = Math.Min(clientCount, maxBatches) * seatsPerClient;
    Console.WriteLine($"\n[TICKET] 최종 결과 — ConfirmedSeats={confirmedSeats}  SoldOut(클라)={soldOut}  Failed={failed}");
    Console.WriteLine($"[TICKET] 기대 상한: ConfirmedSeats({confirmedSeats}) ≤ ExpectedMax({expectedMax})");
    Console.WriteLine($"         where ExpectedMax = min(ClientCount({clientCount}), floor(TotalSeats({totalSeats})/SeatsPerClient({seatsPerClient})))*SeatsPerClient = {expectedMax}");
}
