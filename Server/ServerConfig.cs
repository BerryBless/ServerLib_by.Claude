/// <summary>서버 실행 설정. appsettings.json의 "Server" 섹션에 바인딩됩니다.</summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 9000;
    public int MonitorIntervalSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 30;
    public ServerFeatures Features { get; set; } = new();
}

/// <summary>서버 기능 on/off 토글.</summary>
public sealed class ServerFeatures
{
    public bool EnableSessionRegistry { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableIdleTimeout { get; set; } = true;
}
