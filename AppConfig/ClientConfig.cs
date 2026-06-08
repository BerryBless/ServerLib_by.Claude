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
}

/// <summary>클라이언트 기능 on/off 토글.</summary>
public sealed class ClientFeatures
{
    public bool EnableHeartbeat { get; set; } = true;
    public bool EnableRttDisplay { get; set; } = true;
}
