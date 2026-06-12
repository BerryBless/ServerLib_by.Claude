using System.Buffers;
using AppConfig;
using Microsoft.Extensions.Configuration;
using ServerLib;                              // ServerNet 팩토리: 구현체(internal) 대신 인터페이스로 리스너·레지스트리 생성
using ServerLib.Core;                         // ServerMetrics, GetContext<T>() 확장(public 빌딩블록)
using ServerLib.Core.Memory;                  // PacketPool: 헤더 파싱 유틸(public 빌딩블록)
using ServerLib.Core.Serialization;           // BinaryPacketSerializer / PacketSendExtensions
using ServerLib.Core.Serialization.Packets;   // DamagePacket / MobHpPacket / MobDeathPacket
using ServerLib.Interface;                     // IServerListener / ISession / ISessionRegistry

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    // AddCommandLine: appsettings.json 위에 args 오버라이드 계층 → 하네스가 포트·주기를 인자로 제어
    .AddCommandLine(args)
    .Build();
var cfg = config.GetSection("Server").Get<ServerConfig>() ?? new ServerConfig();

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

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics?.OnPacketReceived();
    Interlocked.Increment(ref totalReceived);
    Interlocked.Increment(ref windowPackets);

    if (packetId == DamagePacket.Id)
    {
        // Deserialize<T>: 헤더 포함 전체 프레임(data.Span)을 받아 내부에서 헤더 4B를 슬라이스로 건너뜀
        var pkt = serializer.Deserialize<DamagePacket>(data.Span);
        var label = session.GetContext<GameContext>()?.Nickname ?? session.RemoteEndPoint?.ToString() ?? "?";
        mob.ApplyDamage(session.SessionId, label, pkt.Amount);
    }

    return ValueTask.CompletedTask;
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

listener.Start(cfg.Port);
Console.WriteLine($"[Server] port {cfg.Port} — 보스HP={MobMaxHp:N0}  데미지패킷Id={DamagePacket.Id}  브로드캐스트주기=200ms");
Console.WriteLine($"  Features: metrics={cfg.Features.EnableMetrics} idleTimeout={cfg.Features.EnableIdleTimeout}");
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

var (finalHp, _, finalGen) = mob.Snapshot();
Console.WriteLine($"종료  total={metrics?.TotalPacketsReceived ?? 0}  final hp={finalHp}  gen={finalGen}");
Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} hp={finalHp} gen={finalGen} " +
                  $"sessions={listener.ActiveSessionCount} heapBytes={GC.GetTotalMemory(false)} " +
                  $"allocBytes={GC.GetTotalAllocatedBytes()} gen0={GC.CollectionCount(0)} gen2={GC.CollectionCount(2)}");

// 세션에 부착할 커스텀 컨텍스트 — GameContext(PlayerId, Nickname) 예제
record GameContext(int PlayerId = 0, string Nickname = "Guest");
