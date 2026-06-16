namespace Server.Auth;

/// <summary>
/// 세션 토큰 저장소 추상화입니다. 발급된 토큰을 TTL과 함께 저장하고 조회합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 다중 I/O 스레드에서 동시 호출이 허용됩니다.
/// Redis 구현체는 ConnectionMultiplexer의 내부 멀티플렉싱으로 경합을 제거합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 토큰 키 문자열 1회 힙 할당이 발생합니다.
/// 토큰 부재 시 <c>null</c> 반환은 무할당입니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking (async). Redis I/O는 awaitable Task로 반환됩니다.</description></item>
/// <item><description><b>TTL:</b> 저장된 토큰은 <see cref="StoreAsync"/> 호출 시 지정된 TTL 경과 후 Redis에서 자동 만료됩니다.</description></item>
/// </list>
/// </remarks>
public interface ITokenStore
{
    /// <summary>세션 토큰을 저장소에 기록합니다.</summary>
    /// <param name="token">발급된 토큰(base64url 인코딩)입니다.</param>
    /// <param name="userId">토큰을 소유하는 사용자 ID입니다.</param>
    /// <param name="ttl">토큰 유효 기간입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    Task StoreAsync(string token, long userId, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// 토큰으로 사용자 ID를 조회합니다. 토큰이 유효하지 않거나 만료된 경우 <c>null</c>을 반환합니다.
    /// </summary>
    /// <param name="token">검증할 토큰(base64url 인코딩)입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    /// <returns>유효한 토큰이면 사용자 ID, 만료·미존재이면 <c>null</c>입니다.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. ConnectionMultiplexer 멀티플렉싱으로 동시 호출 안전.</description></item>
    /// <item><description><b>Memory Allocation:</b> 키 string 1회 할당. 토큰 부재 시 null 반환(무할당).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. Redis GET 1 RTT — awaitable Task로 반환.</description></item>
    /// </list>
    /// </remarks>
    Task<long?> TryGetUserIdAsync(string token, CancellationToken ct = default);
}
