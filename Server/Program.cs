using AppConfig;
using Microsoft.Extensions.Configuration;
using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;
using ServerLib.Interface;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    // AddCommandLine: appsettings.json 위에 args 오버라이드 계층 → 하네스가 포트·주기를 인자로 제어.
    // 예: Server.exe --Server:Port=9100 --Server:MonitorIntervalSeconds=1
    .AddCommandLine(args)
    .Build();
var cfg = config.GetSection("Server").Get<ServerConfig>() ?? new ServerConfig();

// 토글: 레지스트리/메트릭은 비활성 시 생성 자체를 생략(null)
var registry = cfg.Features.EnableSessionRegistry ? new SessionRegistry() : null;
var metrics = cfg.Features.EnableMetrics ? new ServerMetrics() : null;
var listener = new SocketPipelineListener(registry);
// 송신 타임아웃: 수신을 멈춘(죽은) 피어가 송신 게이트를 영구 점유해 BroadcastAsync 전체를 정지시키는 것을 방지.
// 시한 초과 시 해당 세션 송신만 SocketException(TimedOut)으로 끊기고 나머지 브로드캐스트는 계속 진행된다.
listener.SessionSendTimeout = TimeSpan.FromSeconds(30);

var test = 0;
// 권위 수신 카운트: EnableMetrics 토글과 무관하게 항상 증가 — 하네스의 데이터유실 검증 기준값.
long totalReceived = 0;
long windowPackets = 0;
using var cts = new CancellationTokenSource();

listener.OnClientConnected = session =>
{
    session.Context = new GameContext(PlayerId: 1001, Nickname: "홍길동");
    metrics?.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  state={session.State}  (sessions: {metrics?.ConnectedCount ?? 0})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics?.OnClientDisconnected();
    // E2: 부착해 둔 컨텍스트를 캐스팅 없이 타입 안전하게 되읽는다.
    var nick = session.GetContext<GameContext>()?.Nickname ?? "?";
    Console.WriteLine($"[-] {session.RemoteEndPoint}  nick={nick}  (sessions: {metrics?.ConnectedCount ?? 0})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};

// OnClientError: 손상/악성 패킷 디코드 실패나 OnReceived 핸들러 예외로 세션이 강제 종료될 때 통지받는다.
// 이 통지가 없으면 에러 종료가 정상 종료·유휴 타임아웃과 구분되지 않아 핸들러 버그가 조용히 묻힌다.
listener.OnClientError = (session, ex) =>
{
    Console.WriteLine($"[!] {session.RemoteEndPoint}  수신 오류 → 세션 종료: {ex.GetType().Name}: {ex.Message}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics?.OnPacketReceived();
    Interlocked.Increment(ref totalReceived);
    Interlocked.Increment(ref windowPackets);

    if (packetId == IncrementPacket.Id)
        Interlocked.Increment(ref test);
    else if (packetId == DecrementPacket.Id)
        Interlocked.Decrement(ref test);

    return ValueTask.CompletedTask;
};

// 토글: 유휴 타임아웃은 활성 시에만 설정(미설정 시 ServerLib가 스윕 루프를 시작하지 않음)
if (cfg.Features.EnableIdleTimeout)
{
    listener.IdleTimeout = TimeSpan.FromSeconds(cfg.IdleTimeoutSeconds);
    listener.OnIdleTimeout = session =>
    {
        Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
        return ValueTask.CompletedTask;
    };
}

listener.Start(cfg.Port);
Console.WriteLine($"[Server] port {cfg.Port} — 증가(Id={IncrementPacket.Id}) / 감소(Id={DecrementPacket.Id}).");
Console.WriteLine($"  Features: registry={cfg.Features.EnableSessionRegistry} metrics={cfg.Features.EnableMetrics} idleTimeout={cfg.Features.EnableIdleTimeout}");
Console.WriteLine($"  Enter: 현재 세션 목록 출력 | 'q'+Enter: 서버 종료");

_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.MonitorIntervalSeconds), cts.Token); }
        catch (OperationCanceledException) { break; }

        long count = Interlocked.Exchange(ref windowPackets, 0);
        Console.WriteLine($"[Monitor] sessions={metrics?.ConnectedCount ?? 0}  packets/{cfg.MonitorIntervalSeconds}s={count:N0}  test={Volatile.Read(ref test)}  registry={registry?.Count ?? 0}");
        // [STATS]: 하네스가 머신 파싱하는 권위 신호(ASCII·고정 key=value). 토글 독립 소스만 사용.
        Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} " +
                          $"test={Volatile.Read(ref test)} " +
                          $"sessions={listener.ActiveSessionCount} " +     // 토글 독립
                          $"heapBytes={GC.GetTotalMemory(false)} " +        // 서버측 관리 힙(누수 보조 신호)
                          $"allocBytes={GC.GetTotalAllocatedBytes()} " +    // 누적 할당 바이트(수신·송신 경로 alloc률 보조 신호)
                          $"gen0={GC.CollectionCount(0)} " +
                          $"gen2={GC.CollectionCount(2)}");
    }
});

while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) break;

    if (registry is null)
    {
        Console.WriteLine("[Sessions] 세션 레지스트리 비활성화됨 (EnableSessionRegistry=false)");
        continue;
    }
    var sessions = registry.GetAll();
    Console.WriteLine($"[Sessions] count={sessions.Count}");
    foreach (var s in sessions)
        Console.WriteLine($"  {s.SessionId:N}  {s.RemoteEndPoint}  connected={s.ConnectedAt:HH:mm:ss}");
}

cts.Cancel();
listener.Stop();
Console.WriteLine($"종료  total={metrics?.TotalPacketsReceived ?? 0}  final test={test}");
Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} test={test} " +
                  $"sessions={listener.ActiveSessionCount} heapBytes={GC.GetTotalMemory(false)} " +
                  $"allocBytes={GC.GetTotalAllocatedBytes()} gen0={GC.CollectionCount(0)} gen2={GC.CollectionCount(2)}");

// 세션에 부착할 커스텀 컨텍스트 예제
record GameContext(int PlayerId = 0, string Nickname = "Guest");
