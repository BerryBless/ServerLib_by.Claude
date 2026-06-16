using System.Security.Cryptography;

namespace Server.Auth;

/// <summary>
/// 로그인 흐름 오케스트레이터입니다.
/// MySQL 사용자 조회 → PBKDF2 검증 → Redis 토큰 발급·저장 순서로 처리합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 내부 상태가 없으며 주입된 의존성도 Thread-safe입니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 I/O 작업은 async/await로 반환됩니다.
/// PBKDF2 검증(CPU 집약)은 <c>Task.Run</c>으로 스레드풀에 위임합니다.</description></item>
/// <item><description><b>PBKDF2 CPU 비용:</b> <see cref="LoginAsync"/>가 내부에서 <c>await Task.Run(Verify)</c>를
/// 호출하므로 호출자(I/O 스레드)는 블로킹되지 않습니다. 로그인은 세션당 1회 저빈도 작업이므로 Task.Run
/// 오버헤드가 허용됩니다. 게임 핫패스(DamagePacket 처리)에서는 절대 사용하지 마세요.</description></item>
/// </list>
/// </remarks>
internal sealed class LoginService
{
    private readonly IUserStore _userStore;
    private readonly ITokenStore _tokenStore;
    private readonly TimeSpan _tokenTtl;
    private readonly int _pbkdfIterations;

    // CWE-208 타이밍 열거 방어: 존재하지 않는 사용자에 대해서도 동일한 PBKDF2 비용을 소비해
    // "사용자 존재" vs "비밀번호 불일치"를 응답 지연으로 구분할 수 없게 한다.
    // 제로-초기화된 더미 salt/hash — Verify는 항상 false를 반환하지만 동일한 CPU 시간이 소요된다.
    private static readonly byte[] DummySalt = new byte[PasswordHasher.SaltSize];
    private static readonly byte[] DummyHash = new byte[PasswordHasher.HashSize];

    internal LoginService(IUserStore userStore, ITokenStore tokenStore, TimeSpan tokenTtl, int pbkdfIterations)
    {
        _userStore       = userStore;
        _tokenStore      = tokenStore;
        _tokenTtl        = tokenTtl;
        _pbkdfIterations = pbkdfIterations;
    }

    /// <summary>사용자 이름과 비밀번호로 로그인합니다.</summary>
    /// <param name="username">로그인 사용자 이름입니다.</param>
    /// <param name="password">평문 비밀번호입니다.</param>
    /// <param name="ct">작업 취소 토큰입니다.</param>
    /// <returns>로그인 결과입니다. 실패 시 <see cref="LoginResult.Success"/>가 false이며 Token이 빈 문자열입니다.</returns>
    internal async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        // ① MySQL 사용자 조회
        var user = await _userStore.FindByUsernameAsync(username, ct);
        if (user is null)
        {
            // CWE-208 방어: 사용자가 없더라도 더미 PBKDF2를 동일 반복수로 수행해 응답 지연을 균일화.
            // 즉시 반환하면 "아이디 존재" 여부가 타이밍으로 노출된다 — 사용자 열거 공격에 악용됨.
            await Task.Run(() => PasswordHasher.Verify(password, DummySalt, DummyHash, _pbkdfIterations), ct);
            return new LoginResult(false);
        }

        // ② PBKDF2 검증: CPU 집약(~10–50ms) → Task.Run으로 스레드풀에 위임하여 I/O 스레드를 해제
        // 캡처: salt·storedHash·iterations만 캡처(string password는 이미 힙 참조)
        var salt        = user.Salt;
        var storedHash  = user.PasswordHash;
        var iterations  = _pbkdfIterations;
        bool valid = await Task.Run(() => PasswordHasher.Verify(password, salt, storedHash, iterations), ct);
        if (!valid)
            return new LoginResult(false);

        // ③ 세션 토큰 발급: CSPRNG 32바이트 → base64url(추측 불가, URL 안전)
        // RandomNumberGenerator.GetBytes: 암호학적으로 안전한 RNG — 예측 불가능한 토큰 생성
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")  // base64url: '+' → '-'
            .Replace("/", "_")  // base64url: '/' → '_'
            .TrimEnd('=');      // padding 제거

        // ④ Redis 토큰 저장(TTL 첨부)
        await _tokenStore.StoreAsync(token, user.Id, _tokenTtl, ct);

        return new LoginResult(true, user.Id, user.Username, token);
    }
}

/// <summary>
/// <see cref="LoginService.LoginAsync"/>의 결과입니다.
/// </summary>
// readonly record struct: 힙 할당 없이 로그인 결과를 반환 — string 필드는 어차피 힙이지만 struct wrapper는 스택.
internal readonly record struct LoginResult(
    bool   Success,
    long   UserId   = 0,
    string Username = "",
    string Token    = "");
