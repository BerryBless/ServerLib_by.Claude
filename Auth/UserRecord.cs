namespace Server.Auth;

/// <summary>MySQL users 테이블 1행을 나타내는 불변 데이터 레코드입니다.</summary>
/// <param name="Id">데이터베이스 자동 증가 기본 키입니다.</param>
/// <param name="Username">사용자 이름(고유)입니다.</param>
/// <param name="PasswordHash">PBKDF2-SHA256 해시 32바이트입니다.</param>
/// <param name="Salt">패스워드 해시에 사용된 랜덤 salt 16바이트입니다.</param>
// sealed record: 불변 값 컨테이너 — IUserStore가 반환하는 스냅샷이므로 가변성 불필요.
// byte[] 필드는 힙 할당을 수반하지만 로그인은 저빈도 경로 — 허용.
public sealed record UserRecord(long Id, string Username, byte[] PasswordHash, byte[] Salt);
