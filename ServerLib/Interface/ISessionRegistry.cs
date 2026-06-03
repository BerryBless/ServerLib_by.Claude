namespace ServerLib.Interface;

/// <summary>서버에 연결된 활성 세션 전체를 추적하는 레지스트리입니다.</summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 모든 멤버는 동시 호출 안전합니다.</description></item>
/// <item><description>세션 등록·해제는 <see cref="ISessionRegistrar"/>를 통해 수행됩니다.</description></item>
/// </list>
/// </remarks>
public interface ISessionRegistry
{
    /// <summary>현재 연결된 세션 수입니다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    int Count { get; }

    /// <summary>SessionId로 특정 세션을 조회합니다.</summary>
    /// <param name="sessionId">조회할 세션의 고유 식별자</param>
    /// <param name="session">조회된 세션. 존재하지 않으면 <see langword="null"/>.</param>
    /// <returns>세션이 존재하면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    bool TryGet(Guid sessionId, out ISession? session);

    /// <summary>현재 활성 세션 전체의 스냅샷을 반환합니다.</summary>
    /// <returns>호출 시점의 활성 세션 배열 (이후 변경이 반영되지 않는 스냅샷)</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 반환 후 세션이 추가·제거되어도 반환된 컬렉션에는 반영되지 않습니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 호출마다 새 배열 스냅샷을 할당합니다. Hot path에서 반복 호출을 피하십시오.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    IReadOnlyCollection<ISession> GetAll();

    /// <summary>현재 활성 세션 전체에 동일 메시지를 병렬로 전송합니다.</summary>
    /// <param name="data">전송할 데이터 버퍼. 메서드가 완료될 때까지 유효해야 합니다.</param>
    /// <param name="cancellationToken">전송 취소 토큰</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 호출마다 세션 스냅샷 배열과 Task 배열을 할당합니다.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 모든 병렬 전송이 완료될 때까지 비동기 대기합니다.</description></item>
    /// <item><description><b>Error Handling:</b> 개별 세션 전송 실패(<see cref="ObjectDisposedException"/>,
    /// <see cref="System.Net.Sockets.SocketException"/>)는 무시됩니다.
    /// 한 세션 실패가 나머지 브로드캐스트를 중단시키지 않습니다.
    /// <see cref="OperationCanceledException"/>은 전파됩니다 — 취소는 개별 세션 실패가 아닌 호출자의 명시적 요청으로 처리합니다.</description></item>
    /// </list>
    /// </remarks>
    ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

}
