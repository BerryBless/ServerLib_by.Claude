using System.Threading;

namespace Server.Auth;

/// <summary>MySQL SELECT / Redis SET·GET 연산의 지연(마이크로초)을 lock-free로 누적 계측합니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 모든 카운터 필드는 <see cref="Interlocked"/>로 원자적으로 갱신되므로
/// 다수 IO 스레드가 동시에 호출해도 손실·경쟁 조건 없이 누적됩니다.
/// <b>[Memory:]</b> Zero-allocation. 힙 할당 없음. <see cref="GetSnapshot"/> 반환 값은
/// <c>readonly record struct</c>이므로 스택 값 복사로 처리됩니다.
/// <b>[Blocking:]</b> Non-blocking. 내부적으로 <see cref="Interlocked.Add"/>·<see cref="Interlocked.Read"/>만
/// 사용하며 커널 전환 없이 하드웨어 원자 명령으로 완료됩니다.
/// </remarks>
public sealed class DbMetrics
{
    // Interlocked.Add: CPU 원자 명령(LOCK XADD)으로 다수 IO 스레드의 동시 누적을 손실 없이 보장
    private long _mysqlSelectUs;
    private long _mysqlCount;

    // Interlocked.Add: LOCK XADD — 별도 락 없이 RedisSet 지연 합계를 스레드-안전하게 누적
    private long _redisSetUs;
    private long _redisSetCount;

    // Interlocked.Add: LOCK XADD — 별도 락 없이 RedisGet 지연 합계를 스레드-안전하게 누적
    private long _redisGetUs;
    private long _redisGetCount;

    /// <summary>MySQL SELECT 구간 지연(마이크로초)을 기록합니다.</summary>
    /// <param name="us">측정된 지연 시간(마이크로초, μs)</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe — <see cref="Interlocked.Add"/> + <see cref="Interlocked.Increment"/>.<br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public void RecordMysqlSelect(long us)
    {
        Interlocked.Add(ref _mysqlSelectUs, us);
        Interlocked.Increment(ref _mysqlCount);
    }

    /// <summary>Redis SET 구간 지연(마이크로초)을 기록합니다.</summary>
    /// <param name="us">측정된 지연 시간(마이크로초, μs)</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe — <see cref="Interlocked.Add"/> + <see cref="Interlocked.Increment"/>.<br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public void RecordRedisSet(long us)
    {
        Interlocked.Add(ref _redisSetUs, us);
        Interlocked.Increment(ref _redisSetCount);
    }

    /// <summary>Redis GET 구간 지연(마이크로초)을 기록합니다.</summary>
    /// <param name="us">측정된 지연 시간(마이크로초, μs)</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe — <see cref="Interlocked.Add"/> + <see cref="Interlocked.Increment"/>.<br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public void RecordRedisGet(long us)
    {
        Interlocked.Add(ref _redisGetUs, us);
        Interlocked.Increment(ref _redisGetCount);
    }

    /// <summary>현재까지 누적된 평균 지연 스냅샷을 반환합니다.</summary>
    /// <returns>
    /// 각 연산의 평균 지연(마이크로초)과 호출 횟수를 담은 <see cref="DbStatsSnapshot"/>.
    /// 횟수가 0이면 평균도 0을 반환합니다.
    /// </returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe — 모든 필드 읽기에 <see cref="Interlocked.Read"/> 사용.<br/>
    /// <b>[Memory:]</b> Zero-allocation. 반환 타입이 <c>readonly record struct</c>이므로 스택 복사.<br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public DbStatsSnapshot GetSnapshot()
    {
        long mc  = Interlocked.Read(ref _mysqlCount);
        long rsc = Interlocked.Read(ref _redisSetCount);
        long rgc = Interlocked.Read(ref _redisGetCount);
        return new DbStatsSnapshot(
            MysqlSelectAvgUs: mc  > 0 ? Interlocked.Read(ref _mysqlSelectUs) / mc  : 0L,
            RedisSetAvgUs:    rsc > 0 ? Interlocked.Read(ref _redisSetUs)    / rsc : 0L,
            RedisGetAvgUs:    rgc > 0 ? Interlocked.Read(ref _redisGetUs)    / rgc : 0L,
            MysqlCount:    mc,
            RedisSetCount: rsc,
            RedisGetCount: rgc);
    }
}

/// <summary>DB 연산별 평균 지연(마이크로초)과 호출 횟수의 불변 스냅샷입니다.</summary>
/// <param name="MysqlSelectAvgUs">MySQL SELECT 평균 지연(μs). 호출 횟수 0이면 0.</param>
/// <param name="RedisSetAvgUs">Redis SET 평균 지연(μs). 호출 횟수 0이면 0.</param>
/// <param name="RedisGetAvgUs">Redis GET 평균 지연(μs). 호출 횟수 0이면 0.</param>
/// <param name="MysqlCount">MySQL SELECT 누적 호출 횟수.</param>
/// <param name="RedisSetCount">Redis SET 누적 호출 횟수.</param>
/// <param name="RedisGetCount">Redis GET 누적 호출 횟수.</param>
/// <remarks>
/// <b>[Thread Safety:]</b> Immutable — 생성 후 필드 변경 불가.<br/>
/// <b>[Memory:]</b> <c>readonly record struct</c> — 스택 값 복사, 힙 할당 없음.
/// </remarks>
public readonly record struct DbStatsSnapshot(
    long MysqlSelectAvgUs,
    long RedisSetAvgUs,
    long RedisGetAvgUs,
    long MysqlCount,
    long RedisSetCount,
    long RedisGetCount);
