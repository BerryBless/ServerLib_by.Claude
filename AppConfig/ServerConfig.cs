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
}

/// <summary>서버 기능 on/off 토글.</summary>
public sealed class ServerFeatures
{
    public bool EnableSessionRegistry { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableIdleTimeout { get; set; } = true;
}
