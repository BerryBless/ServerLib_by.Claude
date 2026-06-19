namespace AppConfig;

/// <summary>서버 실행 설정. appsettings.json의 "Server" 섹션에 바인딩됩니다.</summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 9000;

    /// <summary>
    /// 모니터 전용 관리 포트입니다. 게임 포트(Port)와 별도로 운영하여
    /// 모니터 접속이 게임 세션 카운트를 오염시키지 않습니다.
    /// </summary>
    public int AdminPort { get; set; } = 9100;

    public int MonitorIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// CPU 백그라운드 샘플러 주기(밀리초)입니다.
    /// CPU%는 TotalProcessorTime의 누적값이므로 단일 읽기로 계산할 수 없어
    /// 고정 주기 delta 방식이 필수입니다.
    /// </summary>
    public int MonitorSampleIntervalMs { get; set; } = 1000;

    public int IdleTimeoutSeconds { get; set; } = 30;

    /// <summary>동시 수용 세션 상한(B1). 0이면 무제한입니다. 연결 폭주에 의한 메모리 고갈을 막습니다.</summary>
    public int MaxConnections { get; set; } = 0;

    /// <summary>단일 IP의 동시 연결 상한(B2). 0이면 무제한입니다. 한 출발지의 연결 독점을 막습니다.</summary>
    public int MaxConnectionsPerIp { get; set; } = 0;

    public ServerFeatures Features { get; set; } = new();

    /// <summary>로그인·인증 관련 설정입니다. Features.EnableLogin이 true일 때 사용됩니다.</summary>
    public AuthConfig Auth { get; set; } = new();

    /// <summary>티켓팅 관련 설정입니다. Features.EnableTicketing이 true일 때 사용됩니다.</summary>
    public TicketConfig Ticket { get; set; } = new();
}

/// <summary>서버 기능 on/off 토글.</summary>
public sealed class ServerFeatures
{
    public bool EnableSessionRegistry { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableIdleTimeout { get; set; } = true;

    /// <summary>
    /// MySQL+Redis 기반 로그인 기능 활성화 여부입니다.
    /// false이면 LoginRequestPacket(Id=10)을 무시하고 기존 게임 흐름에 영향을 주지 않습니다.
    /// </summary>
    public bool EnableLogin { get; set; } = false;

    /// <summary>
    /// Redis 토큰 게이팅 활성화 여부입니다.
    /// true이면 DamagePacket 처리 전 <c>AuthContext</c> 유무를 확인해 미인증 세션을 즉시 드롭합니다.
    /// Redis 연결이 필요하므로 true 시 Auth.RedisConnectionString이 유효해야 합니다.
    /// false(기본)이면 기존 보스몹 데모가 인증 없이 그대로 동작합니다.
    /// </summary>
    public bool RequireAuth { get; set; } = false;

    /// <summary>
    /// 선착순 티켓팅 데모 활성화 여부입니다.
    /// true이면 <c>LoginRequestPacket</c>을 더미 로그인으로 처리하고 티켓 예약·결제 패킷을 활성화합니다.
    /// <c>EnableLogin=false</c> (기본값)인 상태에서만 의미가 있습니다 — 두 모드가 동시에 활성화되면
    /// 실제 LoginService가 우선하여 더미 로그인 분기가 실행되지 않습니다.
    /// </summary>
    public bool EnableTicketing { get; set; } = false;
}

/// <summary>
/// 티켓팅 관련 설정입니다. appsettings.json의 "Server.Ticket" 섹션에 바인딩됩니다.
/// <c>ServerFeatures.EnableTicketing</c>이 <see langword="true"/>일 때 사용됩니다.
/// </summary>
public sealed class TicketConfig
{
    /// <summary>
    /// 좌석 배치 행 수입니다. 기본 2행.
    /// 전체 좌석 수 = <see cref="Rows"/> × <see cref="Cols"/> 이며 255 이하여야 합니다.
    /// </summary>
    public int Rows { get; set; } = 2;

    /// <summary>
    /// 좌석 배치 열 수입니다. 기본 3열.
    /// 전체 좌석 수 = <see cref="Rows"/> × <see cref="Cols"/> 이며 255 이하여야 합니다.
    /// </summary>
    public int Cols { get; set; } = 3;

    /// <summary>결제 시뮬레이션 지연(밀리초)입니다. 기본 300ms.</summary>
    public int PaymentDelayMs { get; set; } = 300;

    /// <summary>더미 결제 주변 실패율(0.0~1.0)입니다. 기본 0(항상 성공).</summary>
    public double PaymentFailureRate { get; set; } = 0.0;

    /// <summary>예약 후 결제하지 않으면 자동 반납되는 TTL(초)입니다. 기본 30초.</summary>
    public int ReservationTtlSeconds { get; set; } = 30;
}

/// <summary>
/// 로그인·인증 관련 설정입니다. appsettings.json의 "Server.Auth" 섹션에 바인딩됩니다.
/// </summary>
public sealed class AuthConfig
{
    /// <summary>MySQL 연결 문자열입니다. schema.sql의 gamedb 데이터베이스를 가리켜야 합니다.</summary>
    public string MySqlConnectionString { get; set; } =
        "Server=127.0.0.1;Port=3306;Database=gamedb;User ID=root;Password=password;";

    /// <summary>StackExchange.Redis 연결 문자열입니다.</summary>
    public string RedisConnectionString { get; set; } = "127.0.0.1:6379";

    /// <summary>세션 토큰 유효 기간(초)입니다. 기본 1시간.</summary>
    public int TokenTtlSeconds { get; set; } = 3600;

    /// <summary>PBKDF2 반복 횟수입니다. 100,000 이상을 권장합니다.</summary>
    public int PbkdfIterations { get; set; } = 100_000;

    /// <summary>
    /// true이면 서버 시작 시 테스트 사용자(SeedUsername/SeedPassword)를 MySQL에 삽입합니다.
    /// 최초 1회 사용 후 반드시 false로 되돌리세요.
    /// </summary>
    public bool SeedTestUser { get; set; } = false;

    /// <summary>시드 사용자 이름입니다.</summary>
    public string SeedUsername { get; set; } = "admin";

    /// <summary>시드 사용자 비밀번호(평문)입니다. 서버가 PBKDF2 해시로 변환하여 저장합니다.</summary>
    public string SeedPassword { get; set; } = "password123";
}
