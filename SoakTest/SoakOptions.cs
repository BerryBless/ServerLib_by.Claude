namespace SoakTest;

/// <summary>소크 테스트 워크로드 종류입니다.</summary>
internal enum WorkloadType
{
    /// <summary>DamagePacket 반복 송신 (기본값, 현행 동작 보존).</summary>
    Damage,
    /// <summary>티켓팅 reserve → pay / abandon / ttl-expire churn.</summary>
    Ticketing,
}

/// <summary>
/// CLI 인자를 파싱한 소크 테스트 구성 옵션입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only) 레코드이므로 Thread-safe. 파싱 후 수정 불가.
/// </remarks>
public sealed class SoakOptions
{
    // ── 공통 옵션 ─────────────────────────────────────────────────────────────────
    /// <summary>동시 연결 클라이언트 수입니다.</summary>
    public int Clients { get; init; } = 20;

    /// <summary>게임 서버 포트입니다.</summary>
    public int Port { get; init; } = 9100;

    /// <summary>관리 포트입니다(서버를 직접 구동할 때 자식 프로세스에 전달).</summary>
    public int AdminPort { get; init; } = 9101;

    /// <summary>연결 사이클 사이의 지연(밀리초). 0이면 즉시 재연결합니다.</summary>
    public int ChurnDelayMs { get; init; } = 0;

    /// <summary>진행 상황 출력 주기(초)입니다.</summary>
    public int ReportIntervalSec { get; init; } = 3;

    /// <summary>외부 서버 부착 모드입니다. true이면 Server.exe를 자식 프로세스로 구동하지 않습니다.</summary>
    public bool Attach { get; init; } = false;

    // ── Damage 전용 옵션 ──────────────────────────────────────────────────────────
    /// <summary>[Damage] 연결당 DamagePacket 송신 횟수입니다.</summary>
    public int SendsPerConn { get; init; } = 5;

    /// <summary>[Damage] 연결 직후 서버 응답 수신 여유 시간(밀리초). 서버가 MobHpPacket을 송신할 시간입니다.</summary>
    public int ReceiveSettleMs { get; init; } = 50;

    // ── 워크로드 선택 ─────────────────────────────────────────────────────────────
    /// <summary>워크로드 종류입니다. 기본값: Damage (현행 동작 보존).</summary>
    internal WorkloadType Workload { get; init; } = WorkloadType.Damage;

    // ── 티켓팅 전용 옵션 ──────────────────────────────────────────────────────────
    /// <summary>[티켓팅] 경합 패턴입니다. 기본값: Spread.</summary>
    internal ContentionPattern Contention { get; init; } = ContentionPattern.Spread;

    /// <summary>[티켓팅] 좌석 그리드 행 수입니다. rows × cols ≤ 255.</summary>
    public int Rows { get; init; } = 5;

    /// <summary>[티켓팅] 좌석 그리드 열 수입니다. rows × cols ≤ 255.</summary>
    public int Cols { get; init; } = 8;

    /// <summary>[티켓팅] 세션당 예약 좌석 수(K). 서버 MaxSeatsPerSession 이하여야 합니다.</summary>
    public int SeatsPerSession { get; init; } = 2;

    /// <summary>
    /// [티켓팅] 서버 ReservationTtlSeconds. ttl-expire 사이클의 idle 보유 시간입니다.
    /// 서버 IdleTimeoutSeconds(기본 30s)보다 작아야 idle-kick이 발생하지 않습니다.
    /// </summary>
    public int TtlSeconds { get; init; } = 5;

    /// <summary>[티켓팅] 서버 PaymentDelayMs. pay 사이클에서 FIN 전 대기 시간 계산에 사용합니다.</summary>
    public int PaymentDelayMs { get; init; } = 50;

    /// <summary>
    /// [티켓팅] graceful-abandon 사이클 비율(0.0–1.0).
    /// pay 없이 즉시 FIN → ReleaseAllByContext 즉시 반납 경로를 스트레스합니다.
    /// </summary>
    public double AbandonRate { get; init; } = 0.15;

    /// <summary>
    /// [티켓팅] TTL 만료 사이클 비율(0.0–1.0).
    /// idle 보유 → SweepExpired 경로를 스트레스합니다.
    /// abandonRate + expireRate ≤ 1.0 이어야 합니다.
    /// </summary>
    public double ExpireRate { get; init; } = 0.15;

    /// <summary>
    /// [티켓팅] 로그인 패킷 전송 후 예약 전 대기(밀리초). 단일 연결 in-order 안전 마진입니다.
    /// </summary>
    public int LoginSettleMs { get; init; } = 10;

    /// <summary>
    /// CLI 인자 배열을 파싱해 <see cref="SoakOptions"/>를 반환합니다.
    /// </summary>
    /// <param name="args">커맨드라인 인자 배열입니다.</param>
    /// <returns>파싱된 옵션 인스턴스입니다.</returns>
    /// <remarks>
    /// <b>[Blocking:]</b> Non-blocking. 인자 배열 순회만 수행합니다.
    /// </remarks>
    public static SoakOptions Parse(string[] args)
    {
        int    clients = 20, port = 9100, adminPort = 9101;
        int    churnDelay = 0, sends = 5, settle = 50, report = 3;
        bool   attach = false;

        var workload    = WorkloadType.Damage;
        var contention  = ContentionPattern.Spread;
        int rows = 5, cols = 8, seatsPerSession = 2;
        int ttl = 5, payDelay = 50, loginSettle = 10;
        double abandonRate = 0.15, expireRate = 0.15;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // 공통
                case "--clients"      when i + 1 < args.Length: clients      = int.Parse(args[++i]); break;
                case "--port"         when i + 1 < args.Length: port         = int.Parse(args[++i]); break;
                case "--admin-port"   when i + 1 < args.Length: adminPort    = int.Parse(args[++i]); break;
                case "--churn-delay"  when i + 1 < args.Length: churnDelay   = int.Parse(args[++i]); break;
                case "--report"       when i + 1 < args.Length: report       = int.Parse(args[++i]); break;
                case "--attach": attach = true; break;

                // Damage 전용
                case "--sends"        when i + 1 < args.Length: sends        = int.Parse(args[++i]); break;
                case "--settle"       when i + 1 < args.Length: settle       = int.Parse(args[++i]); break;

                // 워크로드 선택
                case "--workload"     when i + 1 < args.Length:
                    workload = args[++i].ToLowerInvariant() switch
                    {
                        "ticketing" => WorkloadType.Ticketing,
                        _           => WorkloadType.Damage,
                    };
                    break;

                // 티켓팅 전용
                case "--contention"   when i + 1 < args.Length:
                    contention = args[++i].ToLowerInvariant() switch
                    {
                        "hotspot" => ContentionPattern.Hotspot,
                        "grind"   => ContentionPattern.Grind,
                        _         => ContentionPattern.Spread,
                    };
                    break;
                case "--rows"          when i + 1 < args.Length: rows          = int.Parse(args[++i]); break;
                case "--cols"          when i + 1 < args.Length: cols          = int.Parse(args[++i]); break;
                case "--seats-per-session" when i + 1 < args.Length: seatsPerSession = int.Parse(args[++i]); break;
                case "--ttl"           when i + 1 < args.Length: ttl           = int.Parse(args[++i]); break;
                case "--payment-delay" when i + 1 < args.Length: payDelay      = int.Parse(args[++i]); break;
                case "--abandon-rate"  when i + 1 < args.Length: abandonRate   = double.Parse(args[++i]); break;
                case "--expire-rate"   when i + 1 < args.Length: expireRate    = double.Parse(args[++i]); break;
                case "--login-settle"  when i + 1 < args.Length: loginSettle   = int.Parse(args[++i]); break;

                case "--help": case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        // rows × cols ≤ 255 강제 (1바이트 seatId 제약)
        if (rows * cols > 255)
        {
            Console.Error.WriteLine($"[SoakTest] 경고: rows({rows}) × cols({cols}) = {rows * cols} > 255. rows=5, cols=8로 재설정.");
            rows = 5; cols = 8;
        }

        // seatsPerSession ≤ rows × cols
        seatsPerSession = Math.Min(seatsPerSession, rows * cols);

        // 비율 합계 ≤ 1.0
        if (abandonRate + expireRate > 1.0)
        {
            Console.Error.WriteLine($"[SoakTest] 경고: abandonRate({abandonRate}) + expireRate({expireRate}) > 1.0. abandonRate=0.15, expireRate=0.15으로 재설정.");
            abandonRate = 0.15; expireRate = 0.15;
        }

        return new SoakOptions
        {
            Clients          = Math.Max(1, clients),
            Port             = port,
            AdminPort        = adminPort,
            ChurnDelayMs     = Math.Max(0, churnDelay),
            SendsPerConn     = Math.Max(1, sends),
            ReceiveSettleMs  = Math.Max(0, settle),
            ReportIntervalSec= Math.Max(1, report),
            Attach           = attach,

            Workload         = workload,
            Contention       = contention,
            Rows             = Math.Max(1, rows),
            Cols             = Math.Max(1, cols),
            SeatsPerSession  = Math.Max(1, seatsPerSession),
            TtlSeconds       = Math.Max(1, ttl),
            PaymentDelayMs   = Math.Max(0, payDelay),
            AbandonRate      = Math.Clamp(abandonRate, 0.0, 1.0),
            ExpireRate       = Math.Clamp(expireRate, 0.0, 1.0),
            LoginSettleMs    = Math.Max(0, loginSettle),
        };
    }

    /// <summary>사용법을 콘솔에 출력합니다.</summary>
    public static void PrintHelp() =>
        Console.WriteLine("""
            SoakTest — 무한반복 소크 테스트 하네스

            사용법:
              dotnet run -c Release --project SoakTest -- [options]

            공통 옵션:
              --clients  N         동시 클라이언트 수                    (기본: 20)
              --port     N         게임 서버 포트                        (기본: 9100)
              --admin-port N       관리 포트(child 모드 전용)             (기본: 9101)
              --churn-delay N      사이클 간 지연(ms)                    (기본: 0 = 즉시)
              --report   N         진행 출력 주기(초)                    (기본: 3)
              --attach             외부 서버 부착(Server.exe 미구동)
              --workload TYPE      워크로드 종류: damage|ticketing        (기본: damage)

            [Damage] 전용:
              --sends    N         연결당 DamagePacket 수                (기본: 5)
              --settle   N         응답 수신 여유시간(ms)                 (기본: 50)

            [Ticketing] 전용:
              --contention TYPE    경합 패턴: hotspot|spread|grind       (기본: spread)
              --rows     N         그리드 행 수 (rows × cols ≤ 255)      (기본: 5)
              --cols     N         그리드 열 수                          (기본: 8)
              --seats-per-session N 세션당 예약 좌석 수                   (기본: 2)
              --ttl      N         ReservationTtlSeconds [주의: < IdleTimeout(30s)] (기본: 5)
              --payment-delay N    PaymentDelayMs                        (기본: 50)
              --abandon-rate F     graceful-abandon 비율 (0.0–1.0)       (기본: 0.15)
              --expire-rate  F     TTL-expire 비율 (0.0–1.0)            (기본: 0.15)
              --login-settle N     로그인→예약 안전 마진(ms)               (기본: 10)

            예시:
              dotnet run -c Release --project SoakTest -- --clients 20
              dotnet run -c Release --project SoakTest -- --workload ticketing --clients 30 --rows 5 --cols 8
              dotnet run -c Release --project SoakTest -- --workload ticketing --contention hotspot --clients 50
              dotnet run -c Release --project SoakTest -- --attach --clients 50 --port 9000

            종료: Ctrl+C 또는 콘솔에 'q'+Enter 입력
            종료코드: 0=PASS, 1=FAIL, 2=하네스 초기화 실패
            """);
}
