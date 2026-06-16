namespace Server.Auth;

/// <summary>
/// 사용자 저장소 추상화입니다. 사용자 이름으로 사용자 레코드를 조회합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 다중 I/O 스레드에서 동시 호출이 허용됩니다.
/// 구현체는 내부적으로 ADO.NET 커넥션 풀(per-query connection)로 경합을 제거해야 합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 조회 성공 시 <see cref="UserRecord"/> 1개(힙 할당)를 반환합니다.
/// 존재하지 않는 사용자는 <c>null</c> 반환(무할당).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking (async). 실제 DB I/O는 awaitable Task로 반환됩니다.
/// 단, 네트워크 지연 또는 DB 과부하 시 완료가 지연될 수 있습니다.</description></item>
/// </list>
/// </remarks>
internal interface IUserStore
{
    /// <summary>사용자 이름으로 사용자 레코드를 조회합니다.</summary>
    /// <param name="username">조회할 사용자 이름입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    /// <returns>사용자가 존재하면 <see cref="UserRecord"/>, 없으면 <c>null</c>입니다.</returns>
    Task<UserRecord?> FindByUsernameAsync(string username, CancellationToken ct = default);
}
