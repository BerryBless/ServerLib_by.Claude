namespace AppConfig;

/// <summary>
/// 인증 서버(AuthServer) 실행 설정입니다. appsettings.json의 "AuthServer" 섹션에 바인딩됩니다.
/// </summary>
public sealed class AuthServerConfig
{
    /// <summary>
    /// 인증 서버 수신 포트입니다. 게임 서버(9000)·관리 포트(9100)와 분리된 전용 포트.
    /// 기본값 9200.
    /// </summary>
    public int Port { get; set; } = 9200;

    /// <summary>동시 수용 세션 상한입니다. 0이면 무제한.</summary>
    public int MaxConnections { get; set; } = 0;

    /// <summary>단일 IP의 동시 연결 상한입니다. 0이면 무제한.</summary>
    public int MaxConnectionsPerIp { get; set; } = 0;

    /// <summary>
    /// 로그인·인증 관련 설정입니다. <see cref="AuthConfig"/>를 재사용합니다.
    /// </summary>
    public AuthConfig Auth { get; set; } = new();
}
