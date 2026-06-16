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
    /// <summary>로그인 자격증명입니다. Features.EnableLogin이 true일 때 사용됩니다.</summary>
    public LoginCredentials Login { get; set; } = new();
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
}

/// <summary>클라이언트 로그인 자격증명 설정입니다.</summary>
public sealed class LoginCredentials
{
    /// <summary>로그인 사용자 이름입니다.</summary>
    public string Username { get; set; } = "admin";
    /// <summary>로그인 비밀번호(평문)입니다.</summary>
    public string Password { get; set; } = "password123";
}
