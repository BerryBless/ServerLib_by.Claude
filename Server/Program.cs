using System.Buffers;
using AppConfig;
using Microsoft.Extensions.Configuration;
using Server.Auth;                             // LoginService, MySqlUserStore, RedisTokenStore, AuthContext
using ServerLib;                              // ServerNet 팩토리: 구현체(internal) 대신 인터페이스로 리스너·레지스트리 생성
using ServerLib.Core;                         // ServerMetrics, GetContext<T>() 확장(public 빌딩블록)
using ServerLib.Core.Memory;                  // PacketPool: 헤더 파싱 유틸(public 빌딩블록)
using ServerLib.Core.Serialization;           // BinaryPacketSerializer / PacketSendExtensions
using ServerLib.Core.Serialization.Packets;   // DamagePacket / MobHpPacket / MobDeathPacket / LoginRequestPacket / LoginResponsePacket / Ticket*
using ServerLib.Interface;                     // IServerListener / ISession / ISessionRegistry / SessionState
using StackExchange.Redis;                     // ConnectionMultiplexer
using System.Diagnostics;                      // Process / ProcessThread / PerformanceCounter (Windows 전용)
using System.Text.Json;                        // JsonSerializer — volatile JSON 스냅샷 직렬화
using Ticketing;                               // TicketInventory, DummyPaymentGateway, TicketContext

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    // AddCommandLine: appsettings.json 위에 args 오버라이드 계층 → 하네스가 포트·주기를 인자로 제어
    .AddCommandLine(args)
    .Build();
var cfg = config.GetSection("Server").Get<ServerConfig>() ?? new ServerConfig();

// ConnectionMultiplexer: StackExchange.Redis 내부에서 소수의 물리 TCP 소켓에 모든 Redis 명령을 멀티플렉싱.
// 생성 비용이 크고(DNS 해석 + TCP 연결 협상) 장수명 객체이므로 프로세스당 싱글톤 1개만 유지한다.
// 프로세스 종료 시 Dispose()로 명시적 해제 필수.
ConnectionMultiplexer? redis = null;
// RedisTokenStore: EnableLogin(토큰 저장)·RequireAuth(토큰 검증) 양쪽에서 공유 — 인스턴스 분리 금지.
RedisTokenStore? tokenStore = null;
LoginService? loginService = null;
// EnableLogin: 게임 서버 자체 로그인 기능. RequireAuth: Redis 토큰 게이팅(AuthServer 연계).
// 두 기능 중 하나라도 활성화이면 Redis 연결 필요.
// EnableTicketing: 더미 로그인+선착순 티켓 — loginService·tokenStore와 무관하게 별도 초기화.
TicketInventory? ticketInventory = null;
IDummyPaymentGateway? paymentGateway = null;
if (cfg.Features.EnableTicketing)
{
    ticketInventory = new TicketInventory(cfg.Ticket.Rows, cfg.Ticket.Cols, TimeSpan.FromSeconds(cfg.Ticket.ReservationTtlSeconds));
    paymentGateway  = new DummyPaymentGateway(cfg.Ticket.PaymentDelayMs, cfg.Ticket.PaymentFailureRate);
    Console.WriteLine($"[Ticket] 티켓팅 모듈 초기화  grid={cfg.Ticket.Rows}×{cfg.Ticket.Cols}(총{cfg.Ticket.Rows * cfg.Ticket.Cols}석)  " +
                      $"ttl={cfg.Ticket.ReservationTtlSeconds}s  payDelay={cfg.Ticket.PaymentDelayMs}ms  " +
                      $"failRate={cfg.Ticket.PaymentFailureRate:P0}");
}

// [SEC-04] EnableTicketing은 더미 로그인 전용 — 실제 LoginService와 동시 활성화 시 티켓 핸들러가 무음 비활성화됨
if (cfg.Features.EnableTicketing && cfg.Features.EnableLogin)
    throw new InvalidOperationException(
        "EnableTicketing과 EnableLogin은 동시에 활성화할 수 없습니다. " +
        "티켓팅 모드는 더미 로그인 전용입니다. appsettings.json을 확인하세요.");

if (cfg.Features.EnableLogin || cfg.Features.RequireAuth)
{
    redis = ConnectionMultiplexer.Connect(cfg.Auth.RedisConnectionString);
    // RedisTokenStore: 게임서버 토큰 검증(RequireAuth)·LoginService 토큰 저장(EnableLogin) 공유.
    // ConnectionMultiplexer는 멀티플렉싱으로 동시 호출 안전 — 단일 인스턴스 공유 가능.
    tokenStore = new RedisTokenStore(redis);

    if (cfg.Features.EnableLogin)
    {
        if (cfg.Auth.SeedTestUser)
        {
            await MySqlUserStore.EnsureSchemaAsync(cfg.Auth.MySqlConnectionString);
            // cfg.Auth.PbkdfIterations를 함께 전달 — 시드 해시와 검증 해시의 반복수가 달라지면 로그인이 영구 실패함
            await MySqlUserStore.SeedAsync(cfg.Auth.MySqlConnectionString, cfg.Auth.SeedUsername, cfg.Auth.SeedPassword, cfg.Auth.PbkdfIterations);
        }
        loginService = new LoginService(
            new MySqlUserStore(cfg.Auth.MySqlConnectionString),
            tokenStore,
            TimeSpan.FromSeconds(cfg.Auth.TokenTtlSeconds),
            cfg.Auth.PbkdfIterations);
        Console.WriteLine($"[Login] 인증 모듈 초기화 완료 (MySQL+Redis)  tokenTtl={cfg.Auth.TokenTtlSeconds}s  pbkdf={cfg.Auth.PbkdfIterations}iter");
    }
    else
    {
        Console.WriteLine($"[Login] Redis 토큰 검증 초기화 완료 (RequireAuth 전용 — 로그인은 AuthServer 전담)");
    }
}

// ISessionRegistry: 브로드캐스트(BroadcastAsync)는 레지스트리 없이 불가 → 게임 컨텐츠에서 필수.
// cfg.Features.EnableSessionRegistry 토글과 무관하게 항상 생성한다.
ISessionRegistry registry = ServerNet.CreateSessionRegistry();
var metrics = cfg.Features.EnableMetrics ? new ServerMetrics() : null;
IServerListener listener = ServerNet.CreateListener(registry);
// 송신 타임아웃: 수신을 멈춘(죽은) 피어가 송신 게이트를 영구 점유해 BroadcastAsync 전체를 정지시키는 것을 방지.
listener.SessionSendTimeout = TimeSpan.FromSeconds(30);
listener.MaxConnections = cfg.MaxConnections > 0 ? cfg.MaxConnections : null;
listener.MaxConnectionsPerIp = cfg.MaxConnectionsPerIp > 0 ? cfg.MaxConnectionsPerIp : null;

const long MobMaxHp = 100_000; // 보스 몹 기본 HP

// BinaryPacketSerializer: 내부 상태 없음(Thread-safe) — OnReceived(다중 I/O 스레드)에서 공유 안전
var serializer = new BinaryPacketSerializer();

// 권위 수신 카운트: EnableMetrics 토글과 무관하게 항상 증가 — 하네스의 데이터유실 검증 기준값
long totalReceived = 0;
long windowPackets = 0;
using var cts = new CancellationTokenSource();

// StatsHolder: CPU 샘플러 Task가 volatile 필드에 쓰고, 관리 핸들러가 락 없이 읽는 스냅샷 홀더
// volatile string: 불변(immutable) 참조 교체가 64비트에서 원자적 + 컴파일러·CPU 재정렬 차단
var statsHolder = new StatsHolder();

// PerformanceCounter[]: Windows 커널 GetSystemTimes 래퍼 — per-core CPU% 측정에 필요
// Process.Threads.TotalProcessorTime은 누적값이라 단일 읽기로 %를 얻을 수 없어
// PerformanceCounter(GetSystemTimes delta) 방식이 정확한 순간 CPU% 계산의 유일한 방법
// CA1416 억제: OperatingSystem.IsWindows() 삼항 true 분기에서만 PerformanceCounter를 생성하므로 안전
#pragma warning disable CA1416
PerformanceCounter[]? perfCounters = OperatingSystem.IsWindows()
    ? Enumerable.Range(0, Environment.ProcessorCount)
          .Select(i => new PerformanceCounter("Processor", "% Processor Time", i.ToString()))
          .ToArray()
    : null;
#pragma warning restore CA1416
// 워밍업: NextValue() 첫 호출은 항상 0 반환 → 유효한 delta 값을 위해 1틱 선행 호출이 필수
// CA1416 억제: OperatingSystem.IsWindows() 가드로 이미 Windows 환경임을 보증
#pragma warning disable CA1416
if (perfCounters != null)
    foreach (var c in perfCounters) _ = c.NextValue();
#pragma warning restore CA1416

// 보스 몹: 사망 시 MobDeathPacket 브로드캐스트 후 자동 리스폰
var mob = new MobManager(maxHp: MobMaxHp, onDeath: deathPkt =>
{
    // 사망은 희소 이벤트(수십~수백 회/분) — Task.Run으로 I/O 스레드를 블로킹하지 않고 비동기 브로드캐스트
    _ = Task.Run(async () =>
    {
        int sz = PacketPool.HeaderSize + deathPkt.GetBodySize();
        // ArrayPool<byte>.Shared: 사망 패킷 브로드캐스트 버퍼 대여 — 희소 이벤트이나 new byte[] 할당을 피한다
        var buf = ArrayPool<byte>.Shared.Rent(sz);
        try
        {
            serializer.Serialize(deathPkt, buf);
            await registry.BroadcastAsync(buf.AsMemory(0, sz));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        Console.WriteLine($"[KILL] gen={deathPkt.Generation}  mvp={deathPkt.MvpName}  topDmg={deathPkt.TopDamage:N0}");
    });
});

listener.OnClientConnected = async session =>
{
    // 닉네임: SessionId 앞 4자리로 고유 식별 — GameContext 부착 예제(세션별 커스텀 컨텍스트 패턴)
    session.Context = new GameContext(PlayerId: session.GetHashCode(), Nickname: $"전사-{session.SessionId.ToString("N")[..4]}");
    metrics?.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  (sessions: {metrics?.ConnectedCount ?? 0})");
    // 접속 즉시 현재 HP 1회 전송 — 첫 200ms 브로드캐스트 전까지 클라이언트가 HP 모름 상태를 방지
    var (hp, maxHp, gen) = mob.Snapshot();
    await session.SendAsync(new MobHpPacket { Hp = hp, MaxHp = maxHp, Generation = gen });
};

listener.OnClientDisconnected = session =>
{
    metrics?.OnClientDisconnected();

    // Ticket: 미결제 예약이 있으면 슬롯 반납 — Interlocked.Exchange 경합으로 결제 완료(Confirm)와 안전하게 처리
    if (ticketInventory is not null)
    {
        var tctx = session.GetContext<TicketContext>();
        if (tctx is not null)
        {
            ticketInventory.ReleaseByContext(tctx);
            Console.WriteLine($"[-] {session.RemoteEndPoint}  user={tctx.Username}  " +
                              $"(sessions: {metrics?.ConnectedCount ?? 0}  free={ticketInventory.FreeCount})");
            return ValueTask.CompletedTask;
        }
    }

    // E2: 부착해 둔 컨텍스트를 캐스팅 없이 타입 안전하게 되읽는다
    var nick = session.GetContext<GameContext>()?.Nickname ?? "?";
    Console.WriteLine($"[-] {session.RemoteEndPoint}  nick={nick}  (sessions: {metrics?.ConnectedCount ?? 0})");
    return ValueTask.CompletedTask;
};

// OnClientError: 손상/악성 패킷 디코드 실패나 OnReceived 핸들러 예외로 세션이 강제 종료될 때 통지
listener.OnClientError = (session, ex) =>
{
    Console.WriteLine($"[!] {session.RemoteEndPoint}  수신 오류 → 세션 종료: {ex.GetType().Name}: {ex.Message}");
    return ValueTask.CompletedTask;
};

// async 람다: LoginRequestPacket 처리(DB/Redis await)를 위해 async로 선언.
// DamagePacket(핫패스)은 동기 처리 → 비동기 오버헤드 없음. await가 없는 분기는 즉시 완료 ValueTask 반환.
listener.OnReceived = async (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return;

    metrics?.OnPacketReceived();
    Interlocked.Increment(ref totalReceived);
    Interlocked.Increment(ref windowPackets);

    if (packetId == DamagePacket.Id)
    {
        // RequireAuth 게이팅: 미인증 세션의 DamagePacket을 핫패스에서 즉시 드롭(무할당·락 없음).
        // GetContext<AuthContext>(): session.Context를 as-캐스팅 — 힙 접근 1회, 분기 1회로 최소화.
        // RequireAuth=false(기본)이면 이 가드가 false 분기로 스킵 → 기존 보스몹 데모 무변경 동작.
        if (cfg.Features.RequireAuth && session.GetContext<AuthContext>() is null) return;
        // Deserialize<T>: 헤더 포함 전체 프레임(data.Span)을 받아 내부에서 헤더 4B를 슬라이스로 건너뜀
        var pkt = serializer.Deserialize<DamagePacket>(data.Span);
        var label = session.GetContext<GameContext>()?.Nickname ?? session.RemoteEndPoint?.ToString() ?? "?";
        mob.ApplyDamage(session.SessionId, label, pkt.Amount);
    }
    else if (packetId == LoginRequestPacket.Id && loginService is not null)
    {
        // LoginRequestPacket 처리: 저빈도(세션당 1회) — DB+Redis I/O + Task.Run(PBKDF2) 포함
        // Task.Run 내부에서 CPU 집약 해시 검증을 스레드풀에 위임하므로 이 I/O 스레드는 블로킹되지 않음
        var req = serializer.Deserialize<LoginRequestPacket>(data.Span);
        var result = await loginService.LoginAsync(req.Username, req.Password);

        var resp = new LoginResponsePacket { Success = result.Success, Token = result.Token };
        await session.SendAsync(resp);

        if (result.Success)
        {
            // SessionState.Authenticated: 소비자 설정 가능한 상태(값=2) — 로그인 성공 후 전이
            session.TransitionTo(SessionState.Authenticated);
            // AuthContext: 이후 GetContext<AuthContext>()로 인증 정보 조회 가능
            session.Context = new AuthContext(result.UserId, result.Username, result.Token);
            Console.WriteLine($"[AUTH+] {session.RemoteEndPoint}  user={result.Username}  token={result.Token[..Math.Min(8, result.Token.Length)]}...");
        }
        else
        {
            Console.WriteLine($"[AUTH-] {session.RemoteEndPoint}  user={req.Username}  로그인 실패");
        }
    }
    else if (packetId == AuthTokenPacket.Id && tokenStore is not null)
    {
        // AuthTokenPacket(Id=12): 클라이언트가 AuthServer에서 발급받은 토큰을 게임 서버에 제시.
        // Redis에서 토큰 존재·유효성을 검증(1 RTT) — 유효하면 세션을 Authenticated 상태로 전이.
        var tok = serializer.Deserialize<AuthTokenPacket>(data.Span);
        // TryResolveAsync: Redis GET 1 RTT — userId·username 동시 복원(Non-blocking, StackExchange.Redis 파이프라이닝)
        var info = await tokenStore.TryResolveAsync(tok.Token);
        bool ok = info is not null;
        // LoginResponsePacket 재사용: 별도 ack 패킷 불필요 — 클라이언트 OnReceived의 기존 분기 재사용
        await session.SendAsync(new LoginResponsePacket { Success = ok, Token = ok ? tok.Token : string.Empty });
        if (ok)
        {
            session.TransitionTo(SessionState.Authenticated);
            // AuthContext: TryResolveAsync가 userId·username 모두 복원 → Username 빈 문자열 없음
            session.Context = new AuthContext(info!.Value.UserId, info.Value.Username, tok.Token);
            Console.WriteLine($"[GATE+] {session.RemoteEndPoint}  토큰 검증 성공  user={info.Value.Username}  userId={info.Value.UserId}  token={tok.Token[..Math.Min(8, tok.Token.Length)]}...");
        }
        else
        {
            Console.WriteLine($"[GATE-] {session.RemoteEndPoint}  토큰 검증 실패(만료·미존재)  token={tok.Token[..Math.Min(8, tok.Token.Length)]}...");
        }
    }
    // ──────────── 티켓팅 분기 (EnableTicketing=true 전용) ────────────
    else if (packetId == LoginRequestPacket.Id && loginService is null && ticketInventory is not null)
    {
        // 더미 로그인: 비번 검증 없이 아이디만 수락 — MySQL/Redis/PBKDF2 불필요.
        // loginService is null 가드: 실제 LoginService가 있으면 위 분기가 우선하므로 충돌 없음.
        var req = serializer.Deserialize<LoginRequestPacket>(data.Span);
        // [SEC-02] Username 길이 미검증 시 64KB 힙 할당 공격 가능(ushort 상한) — 32자 제한으로 차단
        const int MaxUsernameLength = 32;
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > MaxUsernameLength)
        {
            await session.SendAsync(new LoginResponsePacket { Success = false, Token = string.Empty });
            return;
        }
        var tctx = new TicketContext(req.Username);
        session.Context = tctx;
        session.TransitionTo(SessionState.Authenticated);
        await session.SendAsync(new LoginResponsePacket { Success = true, Token = string.Empty });
        Console.WriteLine($"[TICKET+] {session.RemoteEndPoint}  더미 로그인  user={req.Username}");
    }
    else if (packetId == SeatMapRequestPacket.Id && ticketInventory is not null)
    {
        // 좌석맵 조회: Non-blocking. stackalloc으로 스택 버퍼 확보 → SnapshotStates로 zero-alloc 기록.
        // states.ToArray()는 Rows*Cols 소규모 할당(≤255B)이며 저빈도 경로이므로 허용.
        var tctx = session.GetContext<TicketContext>();
        if (tctx is null) return; // 더미 로그인 없이 조회 시도 — 무시

        int total = ticketInventory.TotalTickets;
        // stackalloc: 최대 255석 — SnapshotStates 호출 동안 스택 프레임 내 유효, 동기 완료 보장
        Span<byte> states = stackalloc byte[total];
        ticketInventory.SnapshotStates(states);
        var mapPkt = new SeatMapResponsePacket
        {
            Rows   = (byte)ticketInventory.Rows,
            Cols   = (byte)ticketInventory.Cols,
            States = states.ToArray() // SnapshotStates 결과를 패킷 필드로 복사(소규모 1회 할당)
        };
        await session.SendAsync(mapPkt);
    }
    else if (packetId == TicketReserveRequestPacket.Id && ticketInventory is not null)
    {
        // 좌석지정 예약 요청: 클라이언트가 Row/Col을 지정 → seatId로 평면화 → lock-free CAS.
        // await 없음, 즉시 반환(Non-blocking). SeatTaken 시 클라가 좌석맵 재조회 후 재시도.
        var tctx = session.GetContext<TicketContext>();
        if (tctx is null) return; // 더미 로그인 없이 예약 시도 — 무시

        var req = serializer.Deserialize<TicketReserveRequestPacket>(data.Span);
        // seatId = row * Cols + col: 2D 좌석 주소를 내부 평면 인덱스로 변환
        int seatId = req.Row * ticketInventory.Cols + req.Col;
        var (status, slot) = ticketInventory.TryReserve(tctx, seatId);
        int freeAfterReserve = ticketInventory.FreeCount; // [PERF-01] O(n) 스캔 1회만 호출
        var pkt = new TicketResultPacket
        {
            Status    = status,
            Slot      = slot >= 0 ? (byte)slot : TicketResultPacket.NoSlot,
            Remaining = (byte)Math.Min(freeAfterReserve, byte.MaxValue)
        };
        await session.SendAsync(pkt);
        // 좌석을 문자+숫자 형식(예: A1)으로 출력 — Row=0→'A', Col=0→1
        char rowChar = (char)('A' + req.Row);
        Console.WriteLine($"[TICKET] {session.RemoteEndPoint}  user={tctx.Username}  reserve={status}  seat={rowChar}{req.Col + 1}(seatId={slot})  free={freeAfterReserve}");
    }
    else if (packetId == TicketPayRequestPacket.Id && ticketInventory is not null)
    {
        var tctx = session.GetContext<TicketContext>();
        if (tctx is null) return;

        // [SEC-01] 예약 없이 결제하거나 이중 결제 경로 사전 차단 — 직렬 디스패치 보장으로 check-then-act 안전
        if (Volatile.Read(ref tctx.SlotIndex) < 0)
        {
            int freeNoSlot = ticketInventory.FreeCount;
            await session.SendAsync(new TicketResultPacket
            {
                Status    = TicketStatus.NotReserved,
                Slot      = TicketResultPacket.NoSlot,
                Remaining = (byte)Math.Min(freeNoSlot, byte.MaxValue)
            });
            return;
        }

        // async 메모리 안전: data.Span은 await Task.Delay 이후 Pipe 내부 버퍼가 재사용되면 무효.
        // Deserialize와 필드 복사를 반드시 첫 번째 await 이전에 완료해야 한다.
        var pay = serializer.Deserialize<TicketPayRequestPacket>(data.Span);
        bool simulateFail = pay.SimulateFailure; // await 전에 스택 변수로 복사

        // 더미 결제 시뮬레이션: await Task.Delay(PaymentDelayMs) — Thread.Sleep 금지(I/O 스레드 블로킹)
        bool charged;
        try
        {
            charged = await paymentGateway!.ChargeAsync(tctx.Username, simulateFail, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // [DL-T01] 서버 종료 시 OCE: 슬롯 반납 후 응답 생략(세션이 이미 종료 중)
            ticketInventory.ReleaseByContext(tctx);
            return;
        }

        TicketResultPacket result;
        if (charged)
        {
            var (status, slot) = ticketInventory.Confirm(tctx);
            if (status == TicketStatus.NotReserved)
            {
                // [LF-가설1] 결제 성공 후 슬롯 상실(TTL 만료 경합): 실 PG 연동 시 이 경로에서 RefundAsync 필요
                Console.WriteLine($"[TICKET-WARN] {session.RemoteEndPoint}  user={tctx.Username}  결제 성공 후 슬롯 상실 (TTL 만료 경합)");
                int freeLost = ticketInventory.FreeCount;
                result = new TicketResultPacket
                {
                    Status    = TicketStatus.PaymentFailed,
                    Slot      = TicketResultPacket.NoSlot,
                    Remaining = (byte)Math.Min(freeLost, byte.MaxValue)
                };
            }
            else
            {
                int freeOk = ticketInventory.FreeCount; // [PERF-01] O(n) 스캔 1회만 호출
                result = new TicketResultPacket
                {
                    Status    = status,
                    Slot      = slot >= 0 ? (byte)slot : TicketResultPacket.NoSlot,
                    Remaining = (byte)Math.Min(freeOk, byte.MaxValue)
                };
                Console.WriteLine($"[TICKET] {session.RemoteEndPoint}  user={tctx.Username}  pay=OK  status={status}  slot={slot}  free={freeOk}");
            }
        }
        else
        {
            var (_, slot) = ticketInventory.Release(tctx);
            int freeFail = ticketInventory.FreeCount; // [PERF-01] O(n) 스캔 1회만 호출
            result = new TicketResultPacket
            {
                Status    = TicketStatus.PaymentFailed,
                Slot      = slot >= 0 ? (byte)slot : TicketResultPacket.NoSlot,
                Remaining = (byte)Math.Min(freeFail, byte.MaxValue)
            };
            Console.WriteLine($"[TICKET] {session.RemoteEndPoint}  user={tctx.Username}  pay=FAIL  slot반납={slot}  free={freeFail}");
        }
        await session.SendAsync(result);
    }
};

if (cfg.Features.EnableIdleTimeout)
{
    listener.IdleTimeout = TimeSpan.FromSeconds(cfg.IdleTimeoutSeconds);
    listener.OnIdleTimeout = session =>
    {
        Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
        return ValueTask.CompletedTask;
    };
}

// ServerNet.CreateListener(null): 레지스트리 없이 생성 → 관리 연결이 게임 ActiveSessionCount를 건드리지 않음
// SocketPipelineListener(registrar=null): Register/Unregister를 건너뛰므로 게임 세션 카운트가 순수하게 유지됨
IServerListener adminListener = ServerNet.CreateListener();
adminListener.OnReceived = async (session, data) =>
{
    if (PacketPool.TryParseHeader(data.Span, out ushort adminPktId, out _)
        && adminPktId == StatsRequestPacket.Id)
    {
        // statsHolder.Json: volatile 읽기 → 샘플러 Task가 직전에 기록한 최신 스냅샷 반환(락 없이 안전)
        await session.SendAsync(new StatsResponsePacket { Json = statsHolder.Json });
    }
};
// 관리 리스너에는 IdleTimeout 미설정 — 모니터가 5초 주기 폴링 시 게임 30s idle-timeout에 끊기지 않게 함

listener.Start(cfg.Port);
adminListener.Start(cfg.AdminPort);
Console.WriteLine($"[Server] port {cfg.Port} — 보스HP={MobMaxHp:N0}  데미지패킷Id={DamagePacket.Id}  브로드캐스트주기=200ms");
Console.WriteLine($"[Admin]  관리포트 {cfg.AdminPort} — StatsRequest(Id={StatsRequestPacket.Id})/StatsResponse(Id={StatsResponsePacket.Id})");
Console.WriteLine($"  Features: metrics={cfg.Features.EnableMetrics} idleTimeout={cfg.Features.EnableIdleTimeout} " +
                  $"login={cfg.Features.EnableLogin} requireAuth={cfg.Features.RequireAuth} ticketing={cfg.Features.EnableTicketing}");
if (cfg.Features.EnableTicketing)
    Console.WriteLine($"  Ticketing: SeatMapId={SeatMapRequestPacket.Id}/{SeatMapResponsePacket.Id}  ReserveId={TicketReserveRequestPacket.Id}  PayId={TicketPayRequestPacket.Id}  ResultId={TicketResultPacket.Id}  grid={cfg.Ticket.Rows}×{cfg.Ticket.Cols}");
Console.WriteLine($"  Enter: 현재 세션 목록 출력 | 'q'+Enter: 서버 종료");

// 주기 HP 브로드캐스트 Task: 200ms마다 전체 클라에 현재 HP 전송.
// per-hit 브로드캐스트(N_clients × 총타격수 = 2차 증폭) 대신 주기 방식으로 브로드캐스트율을 초당 5회로 고정.
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(200, cts.Token); }
        catch (OperationCanceledException) { break; }

        var (hp, maxHp, gen) = mob.Snapshot();
        var hpPkt = new MobHpPacket { Hp = hp, MaxHp = maxHp, Generation = gen };
        int sz = PacketPool.HeaderSize + hpPkt.GetBodySize();
        // ArrayPool<byte>.Shared: 고정 크기 버킷 풀에서 대여 — 200ms 주기 브로드캐스트에서 new byte[] 힙 할당 없이 재사용
        var buf = ArrayPool<byte>.Shared.Rent(sz);
        try
        {
            serializer.Serialize(hpPkt, buf);
            await registry.BroadcastAsync(buf.AsMemory(0, sz), cts.Token);
        }
        catch (OperationCanceledException) { break; }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
});

// TTL 스위퍼: EnableTicketing일 때만 활성. 1초 주기로 만료 예약을 자동 반납한다.
// Task.Run: 스위퍼 루프가 I/O 스레드를 점유하지 않도록 분리(SweepExpired 자체는 non-blocking이나 주기 루프를 격리)
if (ticketInventory is not null)
{
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try { await Task.Delay(1000, cts.Token); }
            catch (OperationCanceledException) { break; }
            int released = ticketInventory.SweepExpired();
            if (released > 0)
                Console.WriteLine($"[TTL] {released}개 만료 예약 반납  free={ticketInventory.FreeCount}");
        }
    });
}

// 모니터 루프
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.MonitorIntervalSeconds), cts.Token); }
        catch (OperationCanceledException) { break; }

        long count = Interlocked.Exchange(ref windowPackets, 0);
        var (hp, _, gen) = mob.Snapshot();
        Console.WriteLine($"[Monitor] sessions={metrics?.ConnectedCount ?? 0}  packets/{cfg.MonitorIntervalSeconds}s={count:N0}  hp={hp:N0}  gen={gen}  registry={registry.Count}");
        // [STATS]: 하네스가 머신 파싱하는 권위 신호(ASCII·고정 key=value). 토글 독립 소스만 사용.
        // test= 토큰이 hp=/gen=으로 교체됨 — StabilityTest 코드 프로젝트 부재 확인, in-repo 하네스 비파괴.
        Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} " +
                          $"hp={hp} " +
                          $"gen={gen} " +
                          $"sessions={listener.ActiveSessionCount} " +
                          $"heapBytes={GC.GetTotalMemory(false)} " +
                          $"allocBytes={GC.GetTotalAllocatedBytes()} " +
                          $"gen0={GC.CollectionCount(0)} " +
                          $"gen2={GC.CollectionCount(2)}");
    }
});

// CPU 샘플러 Task: PerformanceCounter + Process.Threads delta로 스레드별·호스트 per-core CPU% 계산.
// 요청당 계산 금지: 다중 모니터 동시 접속 시 각자의 "직전 스냅샷"이 서로를 오염시킴.
// 고정 주기 delta 방식으로 모든 요청이 동일한 샘플러 결과를 공유한다.
_ = Task.Run(async () =>
{
    // Process: 현재 프로세스의 스레드 목록·메모리 정보 — Refresh()로 OS에서 재조회
    var proc = Process.GetCurrentProcess();
    // prevThreadTimes: threadId → 직전 TotalProcessorTime(누적) — delta 계산용
    var prevThreadTimes = new Dictionary<int, TimeSpan>();
    var prevWall = DateTime.UtcNow;

    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(cfg.MonitorSampleIntervalMs, cts.Token); }
        catch (OperationCanceledException) { break; }

        var now = DateTime.UtcNow;
        double wallMs = Math.Max((now - prevWall).TotalMilliseconds, 1.0); // 0 나눗셈 방지
        prevWall = now;

        // 스레드별 CPU% = (현재 TotalProcessorTime - 직전) / wallMs * 100
        proc.Refresh(); // OS에서 최신 스레드 목록·WorkingSet 재조회
        var threadEntries = new List<object>();
        bool truncated = false;
        const int MaxThreads = 128; // 64KB BodyLength 상한 대응 — 128×~35B ≈ 4500B, 여유 충분

        foreach (ProcessThread t in proc.Threads)
        {
            if (threadEntries.Count >= MaxThreads) { truncated = true; break; }
            double cpuMs = 0;
            try
            {
                var cur = t.TotalProcessorTime;
                if (prevThreadTimes.TryGetValue(t.Id, out var prev))
                    cpuMs = (cur - prev).TotalMilliseconds;
                prevThreadTimes[t.Id] = cur;
            }
            catch { /* 스레드 종료 직후 접근 불가 — 무시하고 다음 틱에서 자동 정리 */ }
            threadEntries.Add(new { id = t.Id, cpuPercent = Math.Round(cpuMs / wallMs * 100.0, 2) });
        }

        // 호스트 per-core CPU (Windows 전용)
        // PerformanceCounter.NextValue(): GetSystemTimes 기반 — 각 코어의 Idle/Kernel/User 시간 delta를 반환
        // CA1416 억제: perfCounters != null はWindowsでのみ生成される(OperatingSystem.IsWindows() 가드 위쪽)
#pragma warning disable CA1416
        double[]? roundedCorePercents = perfCounters?
            .Select(c => Math.Round(c.NextValue(), 2))
            .ToArray();
#pragma warning restore CA1416
        double? cpuTotalPct = roundedCorePercents != null && roundedCorePercents.Length > 0
            ? Math.Round(roundedCorePercents.Average(), 2) : null;

        // GC.GetGCMemoryInfo(): 마지막 GC 수집 시점의 호스트 메모리 통계 — 크로스플랫폼, 약간 stale 허용
        var gcInfo = GC.GetGCMemoryInfo();
        var (hp, maxHp, gen) = mob.Snapshot();

        var snapshot = new
        {
            timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            sessions = listener.ActiveSessionCount,
            mob = new { hp, maxHp, gen },
            process = new
            {
                workingSetBytes = proc.WorkingSet64,
                gcHeapBytes     = GC.GetTotalMemory(false),
                threadCount     = proc.Threads.Count,
                threadsTruncated = truncated,
                threads         = threadEntries
            },
            host = new
            {
                logicalCores           = Environment.ProcessorCount,
                cpuPerCorePercent      = roundedCorePercents,  // double[]? — null on non-Windows
                cpuTotalPercent        = cpuTotalPct,          // double?   — null on non-Windows
                memoryLoadBytes        = gcInfo.MemoryLoadBytes,
                totalAvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes
            }
        };

        // JsonSerializer.Serialize: 리플렉션 기반 — 저빈도 관리 경로이므로 할당 허용
        statsHolder.Json = JsonSerializer.Serialize(snapshot);
    }

    // 정리: PerformanceCounter는 Win32 리소스를 보유하므로 명시적 해제
    if (perfCounters != null)
        foreach (var c in perfCounters) c.Dispose();
});

while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) break;

    var sessions = registry.GetAll();
    Console.WriteLine($"[Sessions] count={sessions.Count}");
    foreach (var s in sessions)
        Console.WriteLine($"  {s.SessionId:N}  {s.RemoteEndPoint}  connected={s.ConnectedAt:HH:mm:ss}  nick={s.GetContext<GameContext>()?.Nickname ?? "?"}");
}

cts.Cancel();
listener.Stop();
adminListener.Stop();
// ConnectionMultiplexer: 소수 TCP 소켓의 명시적 해제 — Dispose 없으면 GC가 처리하지만 지연될 수 있음
redis?.Dispose();

var (finalHp, _, finalGen) = mob.Snapshot();
Console.WriteLine($"종료  total={metrics?.TotalPacketsReceived ?? 0}  final hp={finalHp}  gen={finalGen}");
Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} hp={finalHp} gen={finalGen} " +
                  $"sessions={listener.ActiveSessionCount} heapBytes={GC.GetTotalMemory(false)} " +
                  $"allocBytes={GC.GetTotalAllocatedBytes()} gen0={GC.CollectionCount(0)} gen2={GC.CollectionCount(2)}");

// 세션에 부착할 커스텀 컨텍스트 — GameContext(PlayerId, Nickname) 예제
record GameContext(int PlayerId = 0, string Nickname = "Guest");

/// <summary>
/// CPU 샘플러 Task가 쓰고 관리 핸들러가 읽는 volatile JSON 스냅샷 홀더.
/// volatile 필드는 class 인스턴스에만 선언 가능하므로 별도 클래스로 분리합니다.
/// </summary>
// sealed: 상속 없는 단일 내부 타입 — JIT devirtualization 허용.
// volatile string: 컴파일러·CPU 재정렬 차단 + 각 코어의 캐시 무효화 보장.
//   string은 불변(immutable) 참조 타입이므로 64비트에서 참조 교체가 원자적.
//   읽기측은 항상 완전한 이전 값 또는 완전한 새 값 중 하나만 봄(torn-read 없음).
sealed class StatsHolder
{
    private volatile string _json = "{}";
    public string Json { get => _json; set => _json = value; }
}
