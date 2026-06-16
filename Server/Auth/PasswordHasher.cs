using System.Security.Cryptography;

namespace Server.Auth;

/// <summary>
/// PBKDF2-SHA256 기반 비밀번호 해시 및 검증 유틸리티입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 내부 상태가 없는 static 메서드입니다.</description></item>
/// <item><description><b>CPU Blocking:</b> <see cref="Verify"/>는 PBKDF2 해시 연산(~10–50ms)을 동기적으로 수행합니다.
/// I/O 스레드에서 직접 호출하지 말고, <c>await Task.Run(...)</c>으로 스레드풀에 위임하세요.
/// (LoginService가 이 패턴을 적용합니다.)</description></item>
/// <item><description><b>Memory Allocation:</b> 각 호출마다 해시 결과 byte[32]와 salt byte[16]을 힙에 생성합니다.</description></item>
/// </list>
/// </remarks>
internal static class PasswordHasher
{
    // PBKDF2 출력 길이(바이트): SHA-256 다이제스트 크기와 일치
    // internal: LoginService의 더미 hash/salt 배열 크기 선언에 재사용 (매직 리터럴 방지)
    internal const int HashSize = 32;
    // Salt 길이(바이트): NIST SP 800-132 권고 최소 16B
    // internal: LoginService의 더미 salt 배열 크기 선언에 재사용
    internal const int SaltSize = 16;
    // 기본 반복 횟수: OWASP 2024 권고(SHA-256 기준 600,000회)에서 데모 목적으로 낮춤.
    // 운영 환경에서는 cfg.Auth.PbkdfIterations로 100,000 이상을 사용하세요.
    internal const int DefaultIterations = 100_000;

    /// <summary>비밀번호를 PBKDF2-SHA256으로 해시합니다.</summary>
    /// <param name="password">해시할 비밀번호(평문)입니다.</param>
    /// <param name="iterations">PBKDF2 반복 횟수입니다. 기본값 100,000 이상을 권장합니다.</param>
    /// <returns>(Salt 16B, Hash 32B) 튜플입니다.</returns>
    internal static (byte[] Salt, byte[] Hash) Hash(string password, int iterations = DefaultIterations)
    {
        // RandomNumberGenerator.GetBytes: CSPRNG(암호학적으로 안전한 RNG) — 예측 불가능한 salt 생성.
        // per-user 랜덤 salt로 사전 공격(Rainbow Table)을 차단한다.
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        // Rfc2898DeriveBytes.Pbkdf2: BCL 내장 PBKDF2 구현 — 별도 패키지 불필요.
        // iterations 비용으로 브루트포스 속도를 의도적으로 낮춘다.
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return (salt, hash);
    }

    /// <summary>비밀번호가 저장된 해시와 일치하는지 검증합니다.</summary>
    /// <param name="password">검증할 비밀번호(평문)입니다.</param>
    /// <param name="salt">저장된 salt입니다.</param>
    /// <param name="storedHash">저장된 해시입니다.</param>
    /// <param name="iterations">PBKDF2 반복 횟수입니다. 저장 시와 동일한 값을 사용해야 합니다.</param>
    /// <returns>일치하면 <c>true</c>, 아니면 <c>false</c>입니다.</returns>
    /// <remarks>
    /// <b>⚠ CPU 집약 연산:</b> PBKDF2 해시 연산(~10–50ms)을 동기적으로 수행합니다.
    /// I/O 스레드에서 호출 시 수신 루프 전체가 정지됩니다. 반드시 <c>Task.Run</c>으로 래핑하세요.
    /// </remarks>
    internal static bool Verify(string password, byte[] salt, byte[] storedHash, int iterations = DefaultIterations)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        // CryptographicOperations.FixedTimeEquals: 상수 시간 비교로 타이밍 공격(timing side-channel)을 차단.
        // 일반 byte[] 비교(LINQ/SequenceEqual)는 첫 불일치에서 early-return하므로 타이밍 정보를 누출한다.
        return CryptographicOperations.FixedTimeEquals(hash, storedHash);
    }
}
