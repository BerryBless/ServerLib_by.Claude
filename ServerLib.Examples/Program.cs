using ServerLib.Examples.Examples;

// ServerLib.Examples — ServerLib 전체 public API 예제 모음
// 각 예제는 127.0.0.1 루프백으로 서버+클라를 한 프로세스에서 구동하고 [OK]를 출력합니다.
//
// 실행 방법:
//   dotnet run                    → 메뉴 표시
//   dotnet run -- all             → 전체 순차 실행 (CI 스모크 테스트)
//   dotnet run -- 01              → 번호로 단일 예제 실행
//   dotnet run -- echo            → 이름 키워드로 단일 예제 실행

// 예제 목록: (번호, 키워드, 설명, 실행 메서드)
// static 배열이라 런타임 힙 할당 없이 프로그램 데이터 세그먼트에 존재
(string num, string key, string desc, Func<Task> run)[] examples =
[
    ("01", "echo",         "Echo 기초: 서버↔클라 패킷 왕복",                   EchoBasics.RunAsync),
    ("02", "session",      "세션 수명주기: 상태 전이·컨텍스트·속성",            SessionLifecycle.RunAsync),
    ("03", "broadcast",    "브로드캐스트·레지스트리: 전체 클라 동시 전송",       BroadcastRegistry.RunAsync),
    ("04", "serialization","직렬화: SpanWriter/Reader·PacketPool 전 API",      Serialization.RunAsync),
    ("05", "rpc",          "RPC 디스패처: 패킷 ID 기반 핸들러 라우팅",          Rpc.RunAsync),
    ("06", "metrics",      "서버 메트릭스: 연결·패킷·바이트 카운터",            Metrics.RunAsync),
    ("07", "heartbeat",    "하트비트: PingInterval·Rtt·SendTimeout",          Heartbeat.RunAsync),
    ("08", "limits",       "연결 제한: MaxConnections·IdleTimeout·거부 카운터", ConnectionLimits.RunAsync),
    ("09", "rudp",         "RUDP: 신뢰 UDP 채널 + 빌딩블록 직접 시연",         Rudp.RunAsync),
    ("10", "holepunch",    "UDP 홀펀칭: UdpHolePuncher 루프백 시연",           UdpHolePunch.RunAsync),
    ("11", "packets",      "패킷 라운드트립: 11종 패킷 serialize→deserialize", Packets.RunAsync),
];

var arg = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

if (arg == "all")
{
    // CI 스모크 테스트: 전체 예제 순차 실행 — 예외 발생 시 즉시 크래시(실패를 크게 드러냄)
    Console.WriteLine("=== ServerLib.Examples 전체 실행 ===");
    foreach (var (num, key, desc, run) in examples)
    {
        Console.WriteLine($"\n[{num}] {desc}");
        await run();
    }
    Console.WriteLine("\n=== 모든 예제 완료 ===");
    return;
}

if (arg != string.Empty)
{
    // 번호("01") 또는 키워드("echo")로 단일 예제 실행
    var match = examples.FirstOrDefault(e => e.num == arg || e.key.Contains(arg));
    if (match.run is not null)
    {
        Console.WriteLine($"[{match.num}] {match.desc}");
        await match.run();
        return;
    }
    Console.WriteLine($"알 수 없는 예제: '{arg}'. 번호(01~11) 또는 키워드를 입력하세요.");
    return;
}

// 메뉴 표시
Console.WriteLine("=== ServerLib.Examples — 사용 가능한 예제 ===");
foreach (var (num, key, desc, _) in examples)
    Console.WriteLine($"  {num}  [{key,-12}]  {desc}");
Console.WriteLine();
Console.WriteLine("실행: dotnet run -- <번호|키워드|all>");
