namespace Server.Auth;

/// <summary>
/// 세션 토큰 저장소 추상화입니다. 발급된 토큰을 TTL과 함께 저장합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 다중 I/O 스레드에서 동시 호출이 허용됩니다.
/// Redis 구현체는 ConnectionMultiplexer의 내부 멀티플렉싱으로 경합을 제거합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 토큰 키 문자열 1회 힙 할당이 발생합니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking (async). Redis I/O는 awaitable Task로 반환됩니다.</description></item>
/// <item><description><b>TTL:</b> 저장된 토큰은 <paramref name="ttl"/> 경과 후 Redis에서 자동 만료됩니다.
/// 이 범위에서는 토큰을 검증하는 미들웨어가 없습니다(스탠드얼론 설계 — 향후 확장 포인트).</description></item>
/// </list>
/// </remarks>
internal interface ITokenStore
{
    /// <summary>세션 토큰을 저장소에 기록합니다.</summary>
    /// <param name="token">발급된 토큰(base64url 인코딩)입니다.</param>
    /// <param name="userId">토큰을 소유하는 사용자 ID입니다.</param>
    /// <param name="ttl">토큰 유효 기간입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    Task StoreAsync(string token, long userId, TimeSpan ttl, CancellationToken ct = default);
}
