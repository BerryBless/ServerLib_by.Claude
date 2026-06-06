namespace StabilityTest;

/// <summary>버스트 안정성 테스트 실행 파라미터. 모두 args로 오버라이드 가능합니다.</summary>
public sealed class StabilityConfig
{
    public int Seed { get; set; } = 12345;            // 시드 RNG — 실패 폭주 재현용
    public int Port { get; set; } = 9100;             // 전용 테스트 포트(개발 서버 9000과 분리)
    public int BurstSeconds { get; set; } = 90;        // 폭주 구간 길이
    public int SettleSeconds { get; set; } = 20;       // drain/settle 구간 길이
    public int MaxReliableClients { get; set; } = 200; // 동시 신뢰 클라이언트 상한
    public int StormMinClients { get; set; } = 50;     // 연결 폭주 최소 클라이언트 수
    public int StormMaxClients { get; set; } = 500;    // 연결 폭주 최대 클라이언트 수
    public int SpikeMinPackets { get; set; } = 500;    // 트래픽 스파이크 최소 패킷 수
    public int SpikeMaxPackets { get; set; } = 5000;   // 트래픽 스파이크 최대 패킷 수
    public int GapMinMs { get; set; } = 200;           // 폭주 이벤트 간 최소 간격
    public int GapMaxMs { get; set; } = 2000;          // 폭주 이벤트 간 최대 간격
    public int CountStableSamples { get; set; } = 3;   // received 안정 판정 연속 표본 수
    public int HangFrozenSamples { get; set; } = 5;    // 부하 중 received 정지 행 판정 연속 표본 수
    public double HeapTolerance { get; set; } = 2.0;   // settle 후 heap ≤ baseline×tol (소프트)
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>`--key value` 형태 인자를 파싱합니다. 알 수 없는 키는 무시합니다.</summary>
    public static StabilityConfig Parse(string[] args)
    {
        var c = new StabilityConfig();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            var key = args[i].TrimStart('-').ToLowerInvariant();
            var val = args[i + 1];
            switch (key)
            {
                case "seed": c.Seed = int.Parse(val); break;
                case "port": c.Port = int.Parse(val); break;
                case "burst": c.BurstSeconds = int.Parse(val); break;
                case "settle": c.SettleSeconds = int.Parse(val); break;
                case "maxclients": c.MaxReliableClients = int.Parse(val); break;
                case "host": c.Host = val; break;
            }
        }
        return c;
    }
}
