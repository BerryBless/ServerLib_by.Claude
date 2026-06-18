namespace SoakTest;

/// <summary>
/// CLI 인자를 파싱한 소크 테스트 구성 옵션입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only) 레코드이므로 Thread-safe. 파싱 후 수정 불가.
/// </remarks>
public sealed class SoakOptions
{
    /// <summary>동시 연결 클라이언트 수입니다.</summary>
    public int Clients { get; init; } = 20;

    /// <summary>게임 서버 포트입니다.</summary>
    public int Port { get; init; } = 9100;

    /// <summary>관리 포트입니다(서버를 직접 구동할 때 자식 프로세스에 전달).</summary>
    public int AdminPort { get; init; } = 9101;

    /// <summary>연결 사이클 사이의 지연(밀리초). 0이면 즉시 재연결합니다.</summary>
    public int ChurnDelayMs { get; init; } = 0;

    /// <summary>한 연결당 DamagePacket 송신 횟수입니다.</summary>
    public int SendsPerConn { get; init; } = 5;

    /// <summary>진행 상황 출력 주기(초)입니다.</summary>
    public int ReportIntervalSec { get; init; } = 3;

    /// <summary>연결 직후 서버 응답 수신 여유 시간(밀리초). 서버가 MobHpPacket을 송신할 시간입니다.</summary>
    public int ReceiveSettleMs { get; init; } = 50;

    /// <summary>외부 서버 부착 모드입니다. true이면 Server.exe를 자식 프로세스로 구동하지 않습니다.</summary>
    public bool Attach { get; init; } = false;

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
        int clients = 20, port = 9100, adminPort = 9101;
        int churnDelay = 0, sends = 5, report = 3, settle = 50;
        bool attach = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--clients"     when i + 1 < args.Length: clients   = int.Parse(args[++i]); break;
                case "--port"        when i + 1 < args.Length: port      = int.Parse(args[++i]); break;
                case "--admin-port"  when i + 1 < args.Length: adminPort = int.Parse(args[++i]); break;
                case "--churn-delay" when i + 1 < args.Length: churnDelay= int.Parse(args[++i]); break;
                case "--sends"       when i + 1 < args.Length: sends     = int.Parse(args[++i]); break;
                case "--report"      when i + 1 < args.Length: report    = int.Parse(args[++i]); break;
                case "--settle"      when i + 1 < args.Length: settle    = int.Parse(args[++i]); break;
                case "--attach": attach = true; break;
                case "--help": case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return new SoakOptions
        {
            Clients          = Math.Max(1, clients),
            Port             = port,
            AdminPort        = adminPort,
            ChurnDelayMs     = Math.Max(0, churnDelay),
            SendsPerConn     = Math.Max(1, sends),
            ReportIntervalSec= Math.Max(1, report),
            ReceiveSettleMs  = Math.Max(0, settle),
            Attach           = attach,
        };
    }

    /// <summary>사용법을 콘솔에 출력합니다.</summary>
    public static void PrintHelp() =>
        Console.WriteLine("""
            SoakTest — 무한반복 소크 테스트 하네스

            사용법:
              dotnet run -c Release --project SoakTest -- [options]

            옵션:
              --clients  N      동시 클라이언트 수          (기본: 20)
              --port     N      게임 서버 포트              (기본: 9100)
              --admin-port N    관리 포트(child 모드 전용)  (기본: 9101)
              --sends    N      연결당 DamagePacket 수      (기본: 5)
              --churn-delay N   사이클 간 지연(ms)          (기본: 0 = 즉시)
              --settle   N      응답 수신 여유시간(ms)       (기본: 50)
              --report   N      진행 출력 주기(초)           (기본: 3)
              --attach          외부 서버 부착(Server.exe 미구동)

            예시:
              dotnet run -c Release --project SoakTest -- --clients 50 --port 9100
              dotnet run -c Release --project SoakTest -- --attach --clients 50 --port 9000

            종료: Ctrl+C 또는 콘솔에 'q'+Enter 입력
            종료코드: 0=PASS, 1=FAIL, 2=하네스 초기화 실패
            """);
}
