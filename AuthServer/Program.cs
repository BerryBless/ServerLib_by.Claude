using AppConfig;
using Microsoft.Extensions.Configuration;
using Server.Auth;                             // LoginService, MySqlUserStore, RedisTokenStore, AuthContext
using ServerLib;                              // ServerNet 팩토리: CreateListener() → IServerListener
using ServerLib.Core.Memory;                  // PacketPool: 헤더 파싱 유틸
using ServerLib.Core.Serialization;           // BinaryPacketSerializer
using ServerLib.Core.Serialization.Packets;   // LoginRequestPacket / LoginResponsePacket
using ServerLib.Interface;                     // IServerListener / ISession / SessionState
using StackExchange.Redis;                     // ConnectionMultiplexer

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    // AddCommandLine: args로 포트·설정을 오버라이드(예: dotnet run -- AuthServer:Port=9201)
    .AddCommandLine(args)
    .Build();
var cfg = config.GetSection("AuthServer").Get<AuthServerConfig>() ?? new AuthServerConfig();

// ConnectionMultiplexer: 소수의 물리 TCP 소켓에 Redis 명령을 멀티플렉싱 — 생성 비용이 크므로 프로세스당 싱글톤 유지.
// using var: 종료 시 자동 Dispose로 TCP 소켓 반환 보장.
using var redis = ConnectionMultiplexer.Connect(cfg.Auth.RedisConnectionString);

if (cfg.Auth.SeedTestUser)
{
    await MySqlUserStore.EnsureSchemaAsync(cfg.Auth.MySqlConnectionString);
    // cfg.Auth.PbkdfIterations를 함께 전달 — 시드 해시와 검증 해시의 반복수가 달라지면 로그인이 영구 실패함
    await MySqlUserStore.SeedAsync(
        cfg.Auth.MySqlConnectionString,
        cfg.Auth.SeedUsername,
        cfg.Auth.SeedPassword,
        cfg.Auth.PbkdfIterations);
}

// RedisTokenStore: IConnectionMultiplexer를 주입받아 StoreAsync로 토큰 저장
var loginService = new LoginService(
    new MySqlUserStore(cfg.Auth.MySqlConnectionString),
    new RedisTokenStore(redis),
    TimeSpan.FromSeconds(cfg.Auth.TokenTtlSeconds),
    cfg.Auth.PbkdfIterations);
Console.WriteLine($"[AuthServer] 인증 서비스 초기화 완료  tokenTtl={cfg.Auth.TokenTtlSeconds}s  pbkdf={cfg.Auth.PbkdfIterations}iter");

// BinaryPacketSerializer: 내부 상태 없음(Thread-safe) — OnReceived(다중 I/O 스레드)에서 공유 안전
var serializer = new BinaryPacketSerializer();

// ServerNet.CreateListener(registry=null): registry 생략 — admin-listener 패턴.
// AuthServer 세션이 게임 서버의 ISessionRegistry.Count를 오염시키지 않는다.
IServerListener listener = ServerNet.CreateListener();
// SendTimeout: 죽은 피어가 송신 게이트를 영구 점유하는 상황 방지
listener.SessionSendTimeout = TimeSpan.FromSeconds(30);
if (cfg.MaxConnections > 0)    listener.MaxConnections    = cfg.MaxConnections;
if (cfg.MaxConnectionsPerIp > 0) listener.MaxConnectionsPerIp = cfg.MaxConnectionsPerIp;

listener.OnClientConnected = session =>
{
    Console.WriteLine($"[AuthServer+] {session.RemoteEndPoint}");
    return ValueTask.CompletedTask;
};
listener.OnClientDisconnected = session =>
{
    Console.WriteLine($"[AuthServer-] {session.RemoteEndPoint}");
    return ValueTask.CompletedTask;
};
listener.OnClientError = (session, ex) =>
{
    Console.WriteLine($"[AuthServer!] {session.RemoteEndPoint}  오류: {ex.GetType().Name}: {ex.Message}");
    return ValueTask.CompletedTask;
};

// async 람다: LoginAsync(DB+Redis await)를 위해 async 선언. LoginRequestPacket(Id=10)만 처리 — 단일 목적 리스너.
listener.OnReceived = async (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return;

    // 인식 불가 패킷 즉시 무시 — 단일 목적 인증 리스너
    if (packetId != LoginRequestPacket.Id)
        return;

    // LoginRequestPacket: DB+Redis I/O + Task.Run(PBKDF2 CPU). I/O 스레드 블로킹 없음.
    var req = serializer.Deserialize<LoginRequestPacket>(data.Span);
    var result = await loginService.LoginAsync(req.Username, req.Password);

    var resp = new LoginResponsePacket { Success = result.Success, Token = result.Token };
    await session.SendAsync(resp);

    if (result.Success)
    {
        // SessionState.Authenticated(값=2): 로그인 성공 후 세션 상태 전이
        session.TransitionTo(SessionState.Authenticated);
        // AuthContext: 이후 GetContext<AuthContext>()로 인증 정보 조회 가능
        session.Context = new AuthContext(result.UserId, result.Username, result.Token);
        Console.WriteLine($"[AUTH+] {session.RemoteEndPoint}  user={result.Username}  token={result.Token[..Math.Min(8, result.Token.Length)]}...");
    }
    else
    {
        Console.WriteLine($"[AUTH-] {session.RemoteEndPoint}  user={req.Username}  로그인 실패");
    }
};

listener.Start(cfg.Port);
Console.WriteLine($"[AuthServer] port {cfg.Port} — LoginRequestPacket(Id={LoginRequestPacket.Id}) 처리 중");
Console.WriteLine($"  Enter: 활성 세션 수 출력 | 'q'+Enter: 서버 종료");

while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) break;
    Console.WriteLine($"[AuthServer] 활성 세션: {listener.ActiveSessionCount}");
}

listener.Stop();
// using var redis: 블록 종료 시 자동 Dispose — TCP 소켓 즉시 반환
Console.WriteLine("[AuthServer] 종료 완료");
