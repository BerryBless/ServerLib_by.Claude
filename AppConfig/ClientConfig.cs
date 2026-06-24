namespace AppConfig;

/// <summary>클라이언트 실행 설정. appsettings.json의 "Client" 섹션에 바인딩됩니다.</summary>
public sealed class ClientConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public int BatchSize { get; set; } = 1000;
    public int DefaultThreadCount { get; set; } = 4;
    public int PingIntervalSeconds { get; set; } = 1;
    public int RttDisplayIntervalSeconds { get; set; } = 2;
    /// <summary>송신 1건의 시한(초). 0이면 비활성(SendAsync 송신당 CTS 미할당). A/B 측정용 토글.</summary>
    public int SendTimeoutSeconds { get; set; } = 30;
    public ClientFeatures Features { get; set; } = new();
    /// <summary>로그인 자격증명입니다. Features.EnableLogin / Features.EnableAuthGating이 true일 때 사용됩니다.</summary>
    public LoginCredentials Login { get; set; } = new();

    /// <summary>티켓팅 데모 설정입니다. Features.EnableTicketing이 true일 때 사용됩니다.</summary>
    public TicketingDemoConfig Ticketing { get; set; } = new();

    /// <summary>
    /// 인증 서버(AuthServer) 수신 포트입니다.
    /// Features.EnableAuthGating이 true일 때 T0가 이 포트로 접속해 토큰을 발급받습니다.
    /// </summary>
    public int AuthPort { get; set; } = 9200;
}

/// <summary>클라이언트 기능 on/off 토글.</summary>
public sealed class ClientFeatures
{
    public bool EnableHeartbeat { get; set; } = true;
    public bool EnableRttDisplay { get; set; } = true;

    /// <summary>
    /// true이면 T0 스레드가 공격 루프 시작 전 LoginRequestPacket을 1회 전송하고 응답을 출력합니다.
    /// 서버의 EnableLogin과 독립적이며, 서버가 로그인을 지원하지 않으면 패킷이 무시됩니다.
    /// </summary>
    public bool EnableLogin { get; set; } = false;

    /// <summary>
    /// true이면 T0 스레드가 AuthServer(ClientConfig.AuthPort)에 먼저 로그인한 뒤
    /// 발급된 토큰을 AuthTokenPacket(Id=12)으로 게임 서버에 제시합니다.
    /// EnableLogin과 독립적이며, 기본 false이면 기존 attack-loop가 무변경으로 동작합니다.
    /// </summary>
    public bool EnableAuthGating { get; set; } = false;

    /// <summary>
    /// true이면 기존 공격 루프 대신 선착순 티켓팅 데모를 실행합니다.
    /// 서버의 <c>Features.EnableTicketing=true</c>와 함께 설정해야 합니다.
    /// </summary>
    public bool EnableTicketing { get; set; } = false;
}

/// <summary>
/// 티켓팅 데모 설정입니다. appsettings.json의 "Client.Ticketing" 섹션에 바인딩됩니다.
/// </summary>
public sealed class TicketingDemoConfig
{
    /// <summary>동시에 연결할 클라이언트 수입니다. 기본 7.</summary>
    public int ClientCount { get; set; } = 7;

    /// <summary>결제 실패를 시뮬레이션할 클라이언트 인덱스입니다. 기본 0 (가장 먼저 접속).</summary>
    public int FailingClientIndex { get; set; } = 0;

    /// <summary>
    /// 실패 클라이언트에게 다른 클라이언트보다 먼저 예약할 수 있도록 주는 선행 지연(밀리초)입니다.
    /// 이 시간 동안 실패 클라이언트가 접속·로그인·예약을 완료하면 슬롯이 보장됩니다.
    /// </summary>
    public int FailerHeadStartMs { get; set; } = 200;

    /// <summary>
    /// 클라이언트 1명이 한 배치로 예약·결제를 시도하는 좌석 수입니다. 기본 2석.
    /// 실제 예약 수는 <c>min(SeatsPerClient, MaxSeatsPerSession)</c>으로 상한이 적용됩니다(서버 설정 기준).
    /// 서버 총 좌석 수가 <c>ClientCount * SeatsPerClient</c>보다 적으면 일부 클라이언트는 <c>SoldOut</c>을 수신합니다.
    /// </summary>
    public int SeatsPerClient { get; set; } = 2;
}

/// <summary>클라이언트 로그인 자격증명 설정입니다.</summary>
public sealed class LoginCredentials
{
    /// <summary>로그인 사용자 이름입니다.</summary>
    public string Username { get; set; } = "admin";
    /// <summary>로그인 비밀번호(평문)입니다.</summary>
    public string Password { get; set; } = "password123";
}
