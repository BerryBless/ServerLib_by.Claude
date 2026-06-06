namespace StabilityTest;

public enum BurstEventType { ConnectionStorm, TrafficSpike }

/// <summary>폭주 타임라인의 단일 이벤트. <paramref name="TimeOffsetMs"/>는 폭주 구간 시작 기준 오프셋입니다.</summary>
public readonly record struct BurstEvent(int TimeOffsetMs, BurstEventType Type, int Magnitude);
