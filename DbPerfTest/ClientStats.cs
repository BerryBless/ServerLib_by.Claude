using System.Threading;

namespace DbPerfTest;

/// <summary>다수 DbPerfClient Task에서 공유하는 lock-free 집계 카운터입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. Interlocked 연산만 사용.
/// <b>[Memory:]</b> Zero-allocation.
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class ClientStats
{
    // Interlocked: 다수 클라이언트 Task에서 경쟁 없이 원자적 증가 — lock보다 컨텍스트 스위치 비용 없음
    private long _errors;
    private long _connects;

    /// <summary>클라이언트 오류 수를 1 증가시킵니다.</summary>
    public void IncError()   => Interlocked.Increment(ref _errors);
    /// <summary>연결 성공 수를 1 증가시킵니다.</summary>
    public void IncConnect() => Interlocked.Increment(ref _connects);

    /// <summary>총 오류 수입니다.</summary>
    public long Errors   => Interlocked.Read(ref _errors);
    /// <summary>총 연결 성공 수입니다.</summary>
    public long Connects => Interlocked.Read(ref _connects);
}
