namespace Ticketing;

/// <summary>
/// 더미 결제 게이트웨이 인터페이스입니다.
/// 실제 PG 연동 없이 지연(<see cref="DummyPaymentGateway.PaymentDelayMs"/>)과
/// 실패율(<see cref="DummyPaymentGateway.FailureRate"/>)만으로 결제를 시뮬레이션합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> Thread-safe. 내부 상태가 없으며 <c>Random.Shared</c>는 lock-free입니다.
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 내부적으로 <c>Task.Delay</c>를 사용하며 절대 <c>Thread.Sleep</c>을
/// 호출하지 않습니다. I/O 스레드에서 안전하게 호출할 수 있습니다.
/// </description></item>
/// <item><description>
/// <b>Memory:</b> <see langword="ValueTask"/> 반환 — 동기 완료 경로에서 힙 할당이 없습니다.
/// 지연(<c>Task.Delay</c>)이 있는 경우에만 상태머신 박싱 1회 발생합니다(저빈도 허용).
/// </description></item>
/// </list>
/// </remarks>
public interface IDummyPaymentGateway
{
    /// <summary>지정된 사용자에 대한 더미 결제를 시뮬레이션합니다.</summary>
    /// <param name="username">결제 사용자 이름입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    /// <returns>결제 성공 여부입니다.</returns>
    ValueTask<bool> ChargeAsync(string username, CancellationToken ct = default);
}
