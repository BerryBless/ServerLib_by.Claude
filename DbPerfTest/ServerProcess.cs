namespace DbPerfTest;

/// <summary>[STATS] 라인에서 파싱한 서버 스냅샷입니다.</summary>
public sealed record ServerStatsSnapshot(
    long Received  = 0,
    long Sessions  = 0,
    long HeapBytes = 0);

/// <summary>[DBSTATS] 라인에서 파싱한 DB 연산 평균 지연 스냅샷입니다.</summary>
public sealed record DbStatsSnapshot(
    long MysqlSelectAvgUs = 0,
    long RedisGetAvgUs    = 0,
    long RedisSetAvgUs    = 0,
    long MysqlCount       = 0,
    long RedisGetCount    = 0,
    long RedisSetCount    = 0);
