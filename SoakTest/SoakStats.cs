namespace SoakTest;

/// <summary>
/// 소크 테스트 진행 중 클라이언트 측 lock-free 집계 카운터입니다.
/// N개 클라이언트 Task에서 동시에 증가하고 reporter Task가 스냅샷을 읽습니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 모든 쓰기는 <c>Interlocked.Increment</c>로 원자적 증가.
/// 읽기는 <c>Volatile.Read</c>로 메모리 재정렬 없이 최신 값을 관찰합니다.
/// <b>[Memory:]</b> Zero-allocation. 모든 카운터는 힙 고정 long 필드로 박싱 없음.
/// <b>[Blocking:]</b> Non-blocking. 모든 연산이 즉시 반환합니다.
/// </remarks>
public sealed class SoakStats
{
    // long: Interlocked.Increment는 CPU LOCK XADD 원자 명령 → N개 Task 동시 증가 시 경쟁 없이 정확
    // Volatile.Read: 다음 읽기 명령이 위로 재정렬되는 것을 막음 → reporter가 최신 값 관찰 보장
    private long _connects;

    // _sent: DamagePacket 송신 성공 카운트 — 서버 DataLoss 판정의 기준값
    private long _sent;

    // _received: OnReceived 콜백(IO 스레드)에서 증가 — MobHpPacket·MobDeathPacket 등 포함
    private long _received;

    // _errors: 연결·송신 예외 카운트 — ClientErrorRate Hard 판정에 사용
    private long _errors;

    // _cycles: 완료 churn 사이클 수 — 처리량 대략적 지표
    private long _cycles;

    /// <summary>누적 연결 성공 횟수입니다.</summary>
    public long Connects  => Volatile.Read(ref _connects);
    /// <summary>누적 DamagePacket 송신 성공 횟수입니다.</summary>
    public long Sent      => Volatile.Read(ref _sent);
    /// <summary>누적 서버 응답 수신 횟수입니다.</summary>
    public long Received  => Volatile.Read(ref _received);
    /// <summary>누적 연결·송신 예외 횟수입니다.</summary>
    public long Errors    => Volatile.Read(ref _errors);
    /// <summary>누적 완료 churn 사이클 수입니다.</summary>
    public long Cycles    => Volatile.Read(ref _cycles);

    /// <summary>연결 성공 카운터를 1 증가합니다.</summary>
    public void IncConnect()  => Interlocked.Increment(ref _connects);
    /// <summary>송신 성공 카운터를 1 증가합니다.</summary>
    public void IncSent()     => Interlocked.Increment(ref _sent);
    /// <summary>수신 카운터를 1 증가합니다.</summary>
    public void IncReceived() => Interlocked.Increment(ref _received);
    /// <summary>오류 카운터를 1 증가합니다.</summary>
    public void IncError()    => Interlocked.Increment(ref _errors);
    /// <summary>완료 사이클 카운터를 1 증가합니다.</summary>
    public void IncCycle()    => Interlocked.Increment(ref _cycles);
}
