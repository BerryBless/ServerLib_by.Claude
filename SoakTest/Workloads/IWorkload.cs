using ServerLib.Interface;

namespace SoakTest.Workloads;

/// <summary>
/// 소크 테스트 워크로드 전략 추상화입니다.
/// connect / churn / graceful-FIN 연결 수명은 <see cref="SoakClient"/>가 소유하고,
/// "연결 위에서 무엇을 보낼 것인가"는 이 인터페이스 구현이 소유합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe.
/// 각 <see cref="SoakClient"/> 인스턴스는 고유한 워크로드 인스턴스를 소유합니다.
/// 서로 다른 Task에서 서로 다른 인스턴스를 사용하므로 크로스 인스턴스 공유는 없습니다.
/// <b>[Memory:]</b> 구현체는 생성자에서 공통 버퍼를 1회만 직렬화하고 모든 사이클에서 재사용합니다.
/// <b>[Blocking:]</b> <see cref="RunCycleAsync"/>는 Non-blocking.
/// fire-and-pace 방식으로 응답을 파싱하지 않습니다 — 누수 판정은 서버측 [TICKET] KPI로 수행합니다.
/// </remarks>
public interface IWorkload
{
    /// <summary>
    /// 이미 연결된 <paramref name="conn"/> 위에서 한 사이클 분량의 트래픽을 발사합니다.
    /// 응답 파싱 없음(fire-and-pace).
    /// SeatTaken·RateLimited 등 서버 응답은 서버측 [TICKET] KPI 카운터로 관찰합니다.
    /// </summary>
    /// <param name="conn">열린 클라이언트 연결입니다.</param>
    /// <param name="cycleIndex">이 클라이언트의 누적 사이클 번호입니다(좌석 순환에 사용).</param>
    /// <param name="ct">취소 신호입니다. 지연 대기 중 취소되면 <see cref="OperationCanceledException"/>이 발생합니다.</param>
    Task RunCycleAsync(IClientConnection conn, int cycleIndex, CancellationToken ct);
}
