namespace Ticketing;

/// <summary>
/// 실제 PG 연동 없이 지연과 실패율로 결제를 시뮬레이션하는 더미 게이트웨이입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Thread-safe. 내부 상태가 없으며 <c>Random.Shared</c>(ThreadLocal 기반 lock-free)를 사용합니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. <c>Task.Delay</c>로 비동기 지연 — <c>Thread.Sleep</c> 사용 금지.
/// I/O 스레드에서 직접 <see langword="await"/> 가능합니다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class DummyPaymentGateway : IDummyPaymentGateway
{
    /// <summary>결제 시뮬레이션 지연(밀리초)입니다. 0이면 즉시 반환합니다.</summary>
    public int PaymentDelayMs { get; }

    /// <summary>주변 실패율(0.0~1.0)입니다. 0이면 항상 성공합니다.</summary>
    public double FailureRate { get; }

    /// <param name="delayMs">결제 지연 시간(밀리초). 기본 300ms.</param>
    /// <param name="failureRate">주변 실패율(0.0~1.0). 기본 0(항상 성공).</param>
    public DummyPaymentGateway(int delayMs = 300, double failureRate = 0.0)
    {
        PaymentDelayMs = delayMs;
        FailureRate = failureRate;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> ChargeAsync(string username, bool simulateFailure, CancellationToken ct = default)
    {
        // 결제 지연 시뮬레이션: Thread.Sleep 대신 Task.Delay — I/O 스레드를 블로킹하지 않는다.
        // await 중에는 I/O 스레드가 다른 세션을 처리할 수 있어 서버 처리량에 영향이 없다.
        if (PaymentDelayMs > 0)
            await Task.Delay(PaymentDelayMs, ct);

        // SimulateFailure=true이면 무조건 실패 — 데모에서 결제 실패→반납→재예약 흐름을 결정론적으로 시연
        if (simulateFailure)
            return false;

        // Random.Shared: ThreadLocal 슬롯 기반 lock-free PRNG — 동시 다수 I/O 스레드에서 락 없이 안전
        return Random.Shared.NextDouble() >= FailureRate;
    }
}
