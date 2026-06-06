namespace StabilityTest;

public enum BurstEventType { ConnectionStorm, TrafficSpike }

/// <summary>폭주 타임라인의 단일 이벤트. <paramref name="TimeOffsetMs"/>는 폭주 구간 시작 기준 오프셋입니다.</summary>
public readonly record struct BurstEvent(int TimeOffsetMs, BurstEventType Type, int Magnitude);

/// <summary>시드 고정 RNG로 무작위 폭주 타임라인을 결정적으로 생성합니다.</summary>
/// <remarks>동일 시드 → 동일 타임라인. 실패한 폭주를 재현하기 위한 핵심 장치입니다.</remarks>
public sealed class BurstScheduler
{
    private readonly StabilityConfig _config;

    public BurstScheduler(StabilityConfig config) => _config = config;

    /// <summary>폭주 구간(0..BurstSeconds*1000ms)에 걸친 이벤트 목록을 비감소 오프셋 순으로 반환합니다.</summary>
    public IReadOnlyList<BurstEvent> BuildTimeline()
    {
        // new Random(seed): 결정적 의사난수열 — 같은 시드는 같은 수열 → 타임라인 재현 가능
        var rng = new Random(_config.Seed);
        var events = new List<BurstEvent>();
        int windowMs = _config.BurstSeconds * 1000;
        int t = 0;

        while (true)
        {
            t += rng.Next(_config.GapMinMs, _config.GapMaxMs + 1);
            if (t >= windowMs) break;

            var type = rng.Next(2) == 0 ? BurstEventType.ConnectionStorm : BurstEventType.TrafficSpike;
            int magnitude = type == BurstEventType.ConnectionStorm
                ? rng.Next(_config.StormMinClients, _config.StormMaxClients + 1)
                : rng.Next(_config.SpikeMinPackets, _config.SpikeMaxPackets + 1);

            events.Add(new BurstEvent(t, type, magnitude));
        }

        return events;
    }
}
