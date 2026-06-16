namespace Server.Auth;

/// <summary>
/// 로그인 성공 후 세션(<see cref="ServerLib.Interface.ISession.Context"/>)에 부착되는 인증 컨텍스트입니다.
/// </summary>
/// <param name="UserId">인증된 사용자의 데이터베이스 ID입니다.</param>
/// <param name="Username">인증된 사용자 이름입니다.</param>
/// <param name="Token">Redis에 저장된 세션 토큰입니다.</param>
/// <remarks>
/// <b>[사용 패턴]</b>
/// <code>
/// session.Context = new AuthContext(userId, username, token);
/// var ctx = session.GetContext&lt;AuthContext&gt;();  // null이면 미인증 세션
/// </code>
/// </remarks>
// sealed record: 로그인 시 1회 생성 후 불변 — race condition 없이 다른 스레드에서 읽기 안전.
internal sealed record AuthContext(long UserId, string Username, string Token);
