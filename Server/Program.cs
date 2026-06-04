using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;
using ServerLib.Interface;

const int Port = 9000;

var registry = new SessionRegistry();
var metrics = new ServerMetrics();
var listener = new SocketPipelineListener(registry);

var test = 0;
long windowPackets = 0;
using var cts = new CancellationTokenSource();

listener.OnClientConnected = session =>
{
    session.Context = new GameContext(PlayerId: 1001, Nickname: "홍길동");
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  state={session.State}  (sessions: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (sessions: {metrics.ConnectedCount})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics.OnPacketReceived();
    Interlocked.Increment(ref windowPackets);

    if (packetId == IncrementPacket.Id)
        Interlocked.Increment(ref test);
    else if (packetId == DecrementPacket.Id)
        Interlocked.Decrement(ref test);

    return ValueTask.CompletedTask;
};

listener.IdleTimeout = TimeSpan.FromSeconds(30);
listener.OnIdleTimeout = session =>
{
    Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
    return ValueTask.CompletedTask;
};

listener.Start(Port);
Console.WriteLine($"[Server] port {Port} — 증가(Id={IncrementPacket.Id}) / 감소(Id={DecrementPacket.Id}).");
Console.WriteLine($"  Enter: 현재 세션 목록 출력 | 'q'+Enter: 서버 종료");

_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(10000, cts.Token); }
        catch (OperationCanceledException) { break; }

        long count = Interlocked.Exchange(ref windowPackets, 0);
        Console.WriteLine($"[Monitor] sessions={metrics.ConnectedCount}  packets/10s={count:N0}  test={Volatile.Read(ref test)}  registry={registry.Count}");
    }
});

while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) break;

    var sessions = registry.GetAll();
    Console.WriteLine($"[Sessions] count={sessions.Count}");
    foreach (var s in sessions)
        Console.WriteLine($"  {s.SessionId:N}  {s.RemoteEndPoint}  connected={s.ConnectedAt:HH:mm:ss}");
}

cts.Cancel();
listener.Stop();
Console.WriteLine($"종료  total={metrics.TotalPacketsReceived}  final test={test}");

// 세션에 부착할 커스텀 컨텍스트 예제
record GameContext(int PlayerId = 0, string Nickname = "Guest");
