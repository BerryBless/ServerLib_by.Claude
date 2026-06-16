namespace Server.Auth;

/// <summary>
/// 토큰 저장소 조회 결과입니다. 토큰→userId·username 두 필드를 한 번의 Redis GET으로 반환합니다.
/// </summary>
// readonly record struct: 조회 결과를 힙 할당 없이 반환 — long+string 두 필드로 박싱 없음.
// AuthContext(Token 필드 포함, 세션 컨텍스트 타입) 재사용은 레이어 스멜 → 전용 레코드 사용.
public readonly record struct TokenInfo(long UserId, string Username);

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
    /// <param name="username">토큰을 소유하는 사용자 이름입니다. 게이팅 경로에서 AuthContext 복원에 사용됩니다.</param>
    /// <param name="ttl">토큰 유효 기간입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    Task StoreAsync(string token, long userId, string username, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// 토큰으로 사용자 정보를 조회합니다. 토큰이 유효하지 않거나 만료된 경우 <c>null</c>을 반환합니다.
    /// </summary>
    /// <param name="token">검증할 토큰(base64url 인코딩)입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    /// <returns>유효한 토큰이면 <see cref="TokenInfo"/>(UserId·Username), 만료·미존재이면 <c>null</c>입니다.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. ConnectionMultiplexer 멀티플렉싱으로 동시 호출 안전.</description></item>
    /// <item><description><b>Memory Allocation:</b> 키 string 1회 할당. 토큰 부재 시 null 반환(무할당).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. Redis GET 1 RTT — awaitable Task로 반환.</description></item>
    /// </list>
    /// </remarks>
    Task<TokenInfo?> TryResolveAsync(string token, CancellationToken ct = default);
}
