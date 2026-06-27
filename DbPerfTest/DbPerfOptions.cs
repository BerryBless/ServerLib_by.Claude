namespace DbPerfTest;

/// <summary>DbPerfTest CLI 파싱 결과입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only). Thread-safe.<br/>
/// <b>[Blocking:]</b> Parse()는 Non-blocking. 배열 순회만 수행.
/// </remarks>
public sealed class DbPerfOptions
{
    /// <summary>동시 클라이언트 수입니다.</summary>
    public int    Clients           { get; init; } = 20;

    /// <summary>게임 서버 포트입니다.</summary>
    public int    Port              { get; init; } = 9100;

    /// <summary>관리 포트입니다.</summary>
    public int    AdminPort         { get; init; } = 9101;

    /// <summary>측정 시간(초). warmup 이후부터 카운팅됩니다.</summary>
    public int    DurationSeconds   { get; init; } = 30;

    /// <summary>warmup 폐기 시간(초). cold JIT·cold DB 풀 오염 방지.</summary>
    public int    WarmupSeconds     { get; init; } = 5;

    /// <summary>read:write 비율의 read 부분입니다. IsReadOp()에서 사용됩니다.</summary>
    public int    ReadParts         { get; init; } = 80;

    /// <summary>read:write 비율의 write 부분입니다.</summary>
    public int    WriteParts        { get; init; } = 20;

    /// <summary>외부 서버 부착 모드입니다. true이면 Server.exe를 자식으로 구동하지 않습니다.</summary>
    public bool   Attach            { get; init; } = false;

    /// <summary>Hard FAIL 임계: 총 throughput(req/s) 하한. null이면 무제한.</summary>
    public long?  TargetThroughput  { get; init; } = null;

    /// <summary>Hard FAIL 임계: write 또는 read p99 상한(밀리초). null이면 무제한.</summary>
    public long?  TargetP99Ms       { get; init; } = null;

    /// <summary>Redis 연결 문자열 오버라이드. null이면 서버 기본값 사용.</summary>
    public string? RedisConn        { get; init; } = null;

    /// <summary>MySQL 연결 문자열 오버라이드. null이면 서버 기본값 사용.</summary>
    public string? MySqlConn        { get; init; } = null;

    /// <summary>PBKDF2 반복 횟수 오버라이드. null이면 서버 기본값(100,000) 사용.</summary>
    public int?   PbkdfIterations   { get; init; } = null;

    /// <summary>로그인에 사용할 사용자 이름입니다. SeedTestUser와 일치해야 합니다.</summary>
    public string Username          { get; init; } = "admin";

    /// <summary>로그인에 사용할 비밀번호입니다.</summary>
    public string Password          { get; init; } = "password123";

    /// <summary>counter번째 요청이 read여야 하는지 반환합니다.</summary>
    /// <param name="counter">요청 순번(0-based). 스레드별 로컬 카운터를 전달합니다.</param>
    /// <returns>해당 순번이 read 범위에 해당하면 true, write 범위이면 false.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. ReadParts·WriteParts는 불변이며 나머지 연산만 수행합니다.<br/>
    /// <b>[Memory Allocation:]</b> Zero-allocation guaranteed.
    /// </remarks>
    public bool IsReadOp(int counter) =>
        (counter % (ReadParts + WriteParts)) < ReadParts;

    /// <summary>CLI 인자 배열을 파싱하여 DbPerfOptions 인스턴스를 반환합니다.</summary>
    /// <param name="args">main 메서드의 args 배열 또는 직접 구성된 문자열 배열.</param>
    /// <returns>파싱 결과로 채워진 불변 DbPerfOptions 인스턴스.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Non-blocking. 배열 순회만 수행하며 공유 상태를 변경하지 않습니다.<br/>
    /// <b>[Blocking:]</b> --help/-h 플래그는 Console.WriteLine 후 Environment.Exit(0)을 호출합니다. 그 외 Non-blocking.
    /// </remarks>
    public static DbPerfOptions Parse(string[] args)
    {
        int   clients = 20, port = 9100, adminPort = 9101;
        int   duration = 30, warmup = 5;
        int   readParts = 80, writeParts = 20;
        bool  attach = false;
        long? targetTput = null, targetP99 = null;
        string? redisConn = null, mysqlConn = null;
        int?  pbkdf = null;
        string username = "admin", password = "password123";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--clients"           when i+1<args.Length: clients    = int.Parse(args[++i]); break;
                case "--port"              when i+1<args.Length: port       = int.Parse(args[++i]); break;
                case "--admin-port"        when i+1<args.Length: adminPort  = int.Parse(args[++i]); break;
                case "--duration"          when i+1<args.Length: duration   = int.Parse(args[++i]); break;
                case "--warmup-seconds"    when i+1<args.Length: warmup     = int.Parse(args[++i]); break;
                case "--target-throughput" when i+1<args.Length: targetTput = long.Parse(args[++i]); break;
                case "--target-p99-ms"     when i+1<args.Length: targetP99  = long.Parse(args[++i]); break;
                case "--redis-conn"        when i+1<args.Length: redisConn  = args[++i]; break;
                case "--mysql-conn"        when i+1<args.Length: mysqlConn  = args[++i]; break;
                case "--pbkdf-iterations"  when i+1<args.Length: pbkdf      = int.Parse(args[++i]); break;
                case "--username"          when i+1<args.Length: username   = args[++i]; break;
                case "--password"          when i+1<args.Length: password   = args[++i]; break;
                case "--attach": attach = true; break;

                case "--read-write-ratio" when i+1<args.Length:
                    var parts = args[++i].Split(':');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out int r)
                        && int.TryParse(parts[1], out int w)
                        && r >= 0 && w >= 0 && r + w > 0)
                    {
                        readParts = r; writeParts = w;
                    }
                    break;

                case "--preset" when i+1<args.Length:
                    switch (args[++i])
                    {
                        case "read-heavy": readParts = 95; writeParts = 5;  break;
                        case "balanced":   readParts = 50; writeParts = 50; break;
                    }
                    break;

                case "--help": case "-h":
                    PrintHelp(); Environment.Exit(0); break;
            }
        }

        return new DbPerfOptions
        {
            Clients          = Math.Max(1, clients),
            Port             = port,
            AdminPort        = adminPort,
            DurationSeconds  = Math.Max(1, duration),
            WarmupSeconds    = Math.Max(0, warmup),
            ReadParts        = Math.Max(0, readParts),
            WriteParts       = Math.Max(0, writeParts),
            Attach           = attach,
            TargetThroughput = targetTput,
            TargetP99Ms      = targetP99,
            RedisConn        = redisConn,
            MySqlConn        = mysqlConn,
            PbkdfIterations  = pbkdf,
            Username         = username,
            Password         = password,
        };
    }

    private static void PrintHelp() =>
        Console.WriteLine("""
            DbPerfTest — DB 포함 성능 테스트 하네스

            사용법:
              dotnet run -c Release --project DbPerfTest -- [options]

            공통:
              --clients N               동시 클라이언트 수                  (기본: 20)
              --port N                  게임 서버 포트                      (기본: 9100)
              --admin-port N            관리 포트                           (기본: 9101)
              --duration N              측정 시간(초)                       (기본: 30)
              --warmup-seconds N        warmup 폐기 시간(초)                (기본: 5)
              --read-write-ratio R:W    read:write 비율 e.g. 80:20          (기본: 80:20)
              --preset read-heavy       --read-write-ratio 95:5
              --preset balanced         --read-write-ratio 50:50
              --attach                  외부 서버 부착 모드

            판정 임계:
              --target-throughput N     총 req/s 하한 (미설정 시 무제한)
              --target-p99-ms N         p99 상한(ms) (미설정 시 무제한)

            DB 오버라이드:
              --redis-conn STR          Redis 연결 문자열
              --mysql-conn STR          MySQL 연결 문자열
              --pbkdf-iterations N      PBKDF2 반복 횟수
              --username STR            로그인 사용자 이름                  (기본: admin)
              --password STR            로그인 비밀번호                     (기본: password123)

            예시:
              dotnet run -c Release --project DbPerfTest -- --clients 20 --duration 30
              dotnet run -c Release --project DbPerfTest -- --preset read-heavy --clients 50
              dotnet run -c Release --project DbPerfTest -- --target-p99-ms 1 (의도적 FAIL)

            종료코드: 0=PASS, 1=FAIL, 2=하네스 초기화 실패
            """);
}
