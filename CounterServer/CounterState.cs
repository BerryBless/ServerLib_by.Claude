namespace CounterServer;

/// <summary>
/// 여러 세션이 <b>동시에 갱신하는 단 하나의 공유 카운터</b>입니다. 이 예제의 경합(contention) 지점 그 자체입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> <b>Thread-safe.</b> 모든 갱신·조회가 <see cref="Interlocked"/> 원자 연산이므로
/// 서로 다른 세션의 IO 스레드에서 동시에 호출해도 갱신 유실(lost update)이 발생하지 않습니다.
/// 전통적 락(<c>lock</c>/<c>Monitor</c>)을 쓰지 않는 이유: 임계 구간이 "메모리 워드 1개 증감"뿐이라
/// 모니터 진입·이탈 비용과 경합 시 커널 대기 전환 비용이 연산 자체보다 훨씬 큽니다.
/// <c>Interlocked</c>는 CPU의 <c>lock xadd</c> 명령 하나로 캐시라인을 배타 소유(RFO)해 갱신하므로
/// 컨텍스트 스위치 없이 완결됩니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> Zero-allocation. 갱신·조회 경로에 힙 할당이 전혀 없습니다
/// (필드는 인스턴스 생성 시 1회 확보된 <see cref="long"/> 2개뿐).
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 모든 멤버가 즉시 반환합니다. 대기·큐잉·비동기 완료가 없습니다.
/// </description></item>
/// <item><description>
/// <b>두 값의 원자성(중요):</b> <see cref="Value"/>와 <see cref="AppliedOps"/> <b>각각</b>은 원자적으로 읽히지만
/// <b>둘을 한 번에 읽는 것은 원자적이지 않습니다</b>. 갱신이 진행 중이면 두 값이 서로 다른 시점의 스냅샷일 수 있습니다.
/// 따라서 <b>두 값을 함께 단언하는 것은 모든 증감이 끝난 정지 상태에서만</b> 유효합니다
/// (클라이언트의 "전 연결 확인 응답 수신 → 최종 조회" 배리어 이후).
/// </description></item>
/// <item><description>
/// <b>인스턴스 필드인 이유:</b> <c>static</c>으로 두면 같은 프로세스의 테스트들이 카운터를 공유해
/// 병렬 실행 시 서로 오염됩니다. 서버 1개 = 상태 1개로 두어 리스너 인스턴스 단위 격리를 보장합니다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class CounterState
{
    // long _value: Interlocked 대상 필드. 64비트 값이라 32비트 프로세스에서는 일반 읽기/쓰기가 원자적이지 않아
    // 상·하위 워드가 찢어진(torn) 값이 보일 수 있다. Interlocked.Increment/Decrement/Read는 플랫폼 폭과 무관하게
    // 단일 원자 연산(x86-64는 lock xadd / lock cmpxchg8b)으로 처리되므로 찢김과 갱신 유실을 동시에 막는다.
    // volatile을 쓰지 않는 이유: volatile은 가시성·재정렬만 다루고 read-modify-write의 원자성은 보장하지 않는다.
    private long _value;

    // long _appliedOps: 실제 적용된 증감 op 수. _value와 마찬가지로 Interlocked 전용 필드다.
    // _value와 별개의 필드로 두는 이유: 두 값을 하나의 long에 비트 패킹하면 단일 CAS로 원자적 쌍 읽기가 가능하지만,
    // 32비트씩으로 좁아져 학습 예제의 값 범위(long)를 잃는다. 이 예제는 정지 상태 단언만 요구하므로
    // 필드를 분리하고 "쌍의 비원자성"을 문서로 못 박는 쪽을 택했다.
    private long _appliedOps;

    /// <summary>현재 카운터 값을 원자적으로 읽어 반환합니다.</summary>
    /// <value>더하기 1회당 +1, 빼기 1회당 −1이 누적된 순(net) 값입니다.</value>
    /// <remarks>
    /// <b>[Thread Safety]</b> Thread-safe. <b>[Memory]</b> Zero-allocation. <b>[Blocking]</b> Non-blocking(즉시 반환).
    /// <br/>동시 갱신 중에 읽으면 "그 시점의 유효한 값"이지 "최종값"이 아닙니다.
    /// </remarks>
    // Interlocked.Read(ref long): 64비트 읽기 자체를 원자화한다. 단순 `_value` 읽기는 32비트 런타임에서
    // 두 번의 32비트 로드로 쪼개져 찢긴 값을 볼 수 있으므로 읽기 경로도 Interlocked로 통일한다.
    public long Value => Interlocked.Read(ref _value);

    /// <summary>지금까지 실제로 적용된 증감 연산의 총 횟수를 원자적으로 읽어 반환합니다.</summary>
    /// <value>더하기 횟수 + 빼기 횟수. 조회 요청·검증 실패로 드롭된 패킷은 포함하지 않습니다.</value>
    /// <remarks>
    /// <b>[Thread Safety]</b> Thread-safe. <b>[Memory]</b> Zero-allocation. <b>[Blocking]</b> Non-blocking(즉시 반환).
    /// <br/><see cref="Value"/>가 0일 때 "상쇄된 것"인지 "아무것도 처리되지 않은 것"인지 구분해 줍니다.
    /// </remarks>
    public long AppliedOps => Interlocked.Read(ref _appliedOps);

    /// <summary>카운터를 원자적으로 1 증가시키고, 적용 연산 수를 1 증가시킵니다.</summary>
    /// <returns>증가 <b>직후</b>의 카운터 값입니다. 다른 세션이 곧바로 갱신할 수 있으므로 "현재 값"의 보증은 아닙니다.</returns>
    /// <remarks>
    /// <b>[Thread Safety]</b> Thread-safe. 여러 IO 스레드가 동시에 호출해도 증가분이 유실되지 않습니다.
    /// <br/><b>[Memory Allocation]</b> Zero-allocation.
    /// <br/><b>[Blocking]</b> Non-blocking. 즉시 반환합니다(락 대기 없음).
    /// <br/><b>[비원자 쌍]</b> <c>_value</c>와 <c>_appliedOps</c>는 <b>서로 다른 두 원자 연산</b>으로 갱신되므로
    /// 그 사이에 다른 스레드가 조회하면 "값은 반영됐지만 연산 수는 아직"인 중간 상태를 볼 수 있습니다.
    /// 최종 검증은 정지 상태에서만 수행하십시오.
    /// </remarks>
    public long Increment()
    {
        // Interlocked.Increment: lock xadd 1회. 읽기→더하기→쓰기 사이에 다른 코어가 끼어들 수 없어
        // `_value++`(비원자 3단계)에서 발생하는 갱신 유실이 원천 차단된다. 이 예제가 결정적 기대값을 내는 근거.
        long updated = Interlocked.Increment(ref _value);
        // 검증을 통과해 실제 적용된 op만 계수한다. 드롭된 패킷은 이 줄에 도달하지 않는다.
        Interlocked.Increment(ref _appliedOps);
        return updated;
    }

    /// <summary>카운터를 원자적으로 1 감소시키고, 적용 연산 수를 1 증가시킵니다.</summary>
    /// <returns>감소 <b>직후</b>의 카운터 값입니다. 다른 세션이 곧바로 갱신할 수 있으므로 "현재 값"의 보증은 아닙니다.</returns>
    /// <remarks>
    /// <b>[Thread Safety]</b> Thread-safe. <b>[Memory Allocation]</b> Zero-allocation. <b>[Blocking]</b> Non-blocking.
    /// <br/><b>[주의]</b> 감소여도 <see cref="AppliedOps"/>는 <b>증가</b>합니다(수행한 연산의 개수를 세는 값이므로).
    /// </remarks>
    public long Decrement()
    {
        // Interlocked.Decrement: lock xadd(-1) 1회. Increment와 동일한 원자성 보증.
        long updated = Interlocked.Decrement(ref _value);
        Interlocked.Increment(ref _appliedOps);
        return updated;
    }
}
