namespace StabilityTest;

/// <summary>서버 [STATS] 라인 1개의 스냅샷. 모든 값은 누적/순간값입니다.</summary>
public readonly record struct StatsSnapshot(long Received, long Test, int Sessions, long HeapBytes, int Gen2);
