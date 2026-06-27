using System.Threading;

namespace DbPerfTest;

/// <summary>write/read 지연(마이크로초)을 독립적으로 기록하고 백분위를 계산합니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> RecordWrite/RecordRead는 Thread-safe(lock). GetXxxPercentiles는
/// 측정 종료 후 단일 스레드에서 호출하는 것을 전제합니다.
/// <b>[Memory:]</b> 측정값당 8B(long). 30s * 100클라 * ~20req/s = ~24,000 항목 ≈ 192KB.
/// <b>[Blocking:]</b> RecordXxx는 very-short lock. GetXxxPercentiles는 Array.Sort O(n log n).
/// </remarks>
public sealed class LatencyRecorder
{
    // List<long>: 동적 크기 배열 — 측정 전 건수 미지, Capacity 자동 증가로 사전 크기 지정 불필요
    private readonly List<long> _writeUs = new();
    private readonly List<long> _readUs  = new();

    // 독립 락: write/read 교차 경합 최소화 — 각 경로가 독립 임계 구간 진입해 서로 블로킹하지 않음
    private readonly object _wLock = new();
    private readonly object _rLock = new();

    // Interlocked 카운터: 진행 중 조회(progress reporter)가 List.Count 대신 사용 — 락 없이 Thread-safe 원자 읽기
    private long _writeCount;
    private long _readCount;

    /// <summary>write 요청 지연(마이크로초)을 기록합니다.</summary>
    /// <param name="microseconds">측정된 write 지연값(마이크로초)</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 내부적으로 short lock으로 List 동시 쓰기를 보호합니다.<br/>
    /// <b>[Memory:]</b> 항목당 8B 힙 할당(List&lt;long&gt; 내부 배열).<br/>
    /// <b>[Blocking:]</b> very-short lock 후 즉시 반환.
    /// </remarks>
    public void RecordWrite(long microseconds)
    {
        lock (_wLock) _writeUs.Add(microseconds);
        Interlocked.Increment(ref _writeCount);
    }

    /// <summary>read 요청 지연(마이크로초)을 기록합니다.</summary>
    /// <param name="microseconds">측정된 read 지연값(마이크로초)</param>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 내부적으로 short lock으로 List 동시 쓰기를 보호합니다.<br/>
    /// <b>[Memory:]</b> 항목당 8B 힙 할당(List&lt;long&gt; 내부 배열).<br/>
    /// <b>[Blocking:]</b> very-short lock 후 즉시 반환.
    /// </remarks>
    public void RecordRead(long microseconds)
    {
        lock (_rLock) _readUs.Add(microseconds);
        Interlocked.Increment(ref _readCount);
    }

    /// <summary>기록된 write 건수입니다. Thread-safe.</summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. Interlocked.Read로 메모리 장벽 보장.<br/>
    /// <b>[Blocking:]</b> 즉시 반환(Non-blocking).
    /// </remarks>
    public long WriteCount => Interlocked.Read(ref _writeCount);

    /// <summary>기록된 read 건수입니다. Thread-safe.</summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. Interlocked.Read로 메모리 장벽 보장.<br/>
    /// <b>[Blocking:]</b> 즉시 반환(Non-blocking).
    /// </remarks>
    public long ReadCount => Interlocked.Read(ref _readCount);

    /// <summary>write 지연 백분위를 계산합니다. 측정 종료 후 단일 스레드에서 호출하세요.</summary>
    /// <returns>p50/p95/p99/max를 담은 <see cref="PercentileResult"/>. 데이터 없으면 default(모두 0).</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 측정 완료 후 단일 스레드 호출 전제. 진행 중 호출 시 스냅샷 기반 계산.<br/>
    /// <b>[Memory:]</b> 내부적으로 List를 복사해 Array.Sort — O(n) 임시 힙 할당.<br/>
    /// <b>[Blocking:]</b> Array.Sort O(n log n) 동기 블로킹.
    /// </remarks>
    public PercentileResult GetWritePercentiles() => CalcPercentiles(_writeUs);

    /// <summary>read 지연 백분위를 계산합니다. 측정 종료 후 단일 스레드에서 호출하세요.</summary>
    /// <returns>p50/p95/p99/max를 담은 <see cref="PercentileResult"/>. 데이터 없으면 default(모두 0).</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> 측정 완료 후 단일 스레드 호출 전제. 진행 중 호출 시 스냅샷 기반 계산.<br/>
    /// <b>[Memory:]</b> 내부적으로 List를 복사해 Array.Sort — O(n) 임시 힙 할당.<br/>
    /// <b>[Blocking:]</b> Array.Sort O(n log n) 동기 블로킹.
    /// </remarks>
    public PercentileResult GetReadPercentiles() => CalcPercentiles(_readUs);

    private static PercentileResult CalcPercentiles(List<long> data)
    {
        if (data.Count == 0) return default;
        var sorted = data.ToArray();
        Array.Sort(sorted);
        return new PercentileResult(
            Count: sorted.LongLength,
            P50:   Ptile(sorted, 0.50),
            P95:   Ptile(sorted, 0.95),
            P99:   Ptile(sorted, 0.99),
            Max:   sorted[^1]);
    }

    private static long Ptile(long[] sorted, double p)
    {
        int idx = Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1);
        return sorted[Math.Min(idx, sorted.Length - 1)];
    }
}

/// <summary>백분위 계산 결과입니다. 단위: 마이크로초.</summary>
/// <param name="Count">측정 건수</param>
/// <param name="P50">50번째 백분위(중앙값)</param>
/// <param name="P95">95번째 백분위</param>
/// <param name="P99">99번째 백분위</param>
/// <param name="Max">최대값</param>
public readonly record struct PercentileResult(
    long Count, long P50, long P95, long P99, long Max);
