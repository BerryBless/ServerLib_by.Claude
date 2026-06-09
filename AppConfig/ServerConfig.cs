namespace AppConfig;

/// <summary>서버 실행 설정. appsettings.json의 "Server" 섹션에 바인딩됩니다.</summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 9000;
    public int MonitorIntervalSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 30;

    /// <summary>동시 수용 세션 상한(B1). 0이면 무제한입니다. 연결 폭주에 의한 메모리 고갈을 막습니다.</summary>
    public int MaxConnections { get; set; } = 0;

    /// <summary>단일 IP의 동시 연결 상한(B2). 0이면 무제한입니다. 한 출발지의 연결 독점을 막습니다.</summary>
    public int MaxConnectionsPerIp { get; set; } = 0;

    public ServerFeatures Features { get; set; } = new();
}

/// <summary>서버 기능 on/off 토글.</summary>
public sealed class ServerFeatures
{
    public bool EnableSessionRegistry { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableIdleTimeout { get; set; } = true;
}
