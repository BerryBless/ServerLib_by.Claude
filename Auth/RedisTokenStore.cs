using StackExchange.Redis;

namespace Server.Auth;

/// <summary>
/// Redis 기반 세션 토큰 저장소 구현입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. IConnectionMultiplexer는 멀티플렉싱으로
/// 다중 동시 호출을 처리합니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. StringSetAsync/StringGetAsync는 awaitable Task를 반환합니다.</description></item>
/// <item><description><b>TTL:</b> 토큰은 <see cref="ITokenStore.StoreAsync"/> 호출 시 지정된 TTL 후 자동 만료됩니다.
/// Redis의 TTL 만료는 키 단위 원자적 삭제이므로 별도 정리 작업이 불필요합니다.</description></item>
/// </list>
/// </remarks>
public sealed class RedisTokenStore : ITokenStore
{
    // IConnectionMultiplexer: StackExchange.Redis의 멀티플렉싱 커넥션 인터페이스.
    // 내부적으로 소수의 물리 TCP 소켓에 모든 Redis 명령을 파이프라이닝 → 고동시 환경에서 커넥션 과부하 방지.
    // ConnectionMultiplexer(구현체)는 프로세스당 1개 생성 후 프로그램 수명 동안 공유한다(Program.cs에서 싱글톤).
    private readonly IConnectionMultiplexer _multiplexer;

    // 토큰 키 접두어: namespace 분리로 다른 Redis 데이터와 충돌 방지
    private const string KeyPrefix = "auth:session:";

    /// <param name="multiplexer">프로세스 싱글톤 ConnectionMultiplexer입니다.</param>
    public RedisTokenStore(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    /// <inheritdoc/>
    public async Task StoreAsync(string token, long userId, TimeSpan ttl, CancellationToken ct = default)
    {
        // IDatabase: 논리 DB 뷰(GetDatabase()는 O(1) — 물리 연결을 새로 맺지 않음)
        var db = _multiplexer.GetDatabase();
        // StringSetAsync: key="auth:session:{token}", value=userId, expiry=TTL
        // Redis 내부에서 TTL은 원자적으로 설정되며 만료 시 키가 자동 삭제됨(별도 정리 불필요)
        await db.StringSetAsync($"{KeyPrefix}{token}", userId.ToString(), ttl);
    }

    /// <inheritdoc/>
    public async Task<long?> TryGetUserIdAsync(string token, CancellationToken ct = default)
    {
        // IDatabase: O(1) 논리 뷰 획득 — GetDatabase()는 새 TCP 연결을 맺지 않음
        var db = _multiplexer.GetDatabase();
        // StringGetAsync: Redis GET 1 RTT — 키 부재·만료 시 RedisValue.IsNullOrEmpty = true
        RedisValue val = await db.StringGetAsync($"{KeyPrefix}{token}");
        if (val.IsNullOrEmpty) return null;  // 만료 또는 미존재 → 토큰 무효
        // StoreAsync에서 userId.ToString()으로 저장 → (string?)val로 명시 캐스팅해 TryParse 오버로드 모호성 제거
        return long.TryParse((string?)val, out var userId) ? userId : null;
    }
}
