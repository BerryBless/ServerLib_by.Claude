using Server.Auth;
using Xunit;

namespace ServerLib.Tests;

// ──────────────────────────────────────────────────────────────────────────
// PasswordHasher 테스트 (Tier 1 — 인프라 불필요)
// ──────────────────────────────────────────────────────────────────────────
public class PasswordHasherTests
{
    [Fact]
    public void Hash_Verify_CorrectPassword_ReturnsTrue()
    {
        // Arrange
        const string password = "s3cur3P@ssw0rd!";

        // Act
        var (salt, hash) = PasswordHasher.Hash(password, iterations: 1_000); // 테스트 속도 위해 낮은 반복
        bool result = PasswordHasher.Verify(password, salt, hash, iterations: 1_000);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Hash_Verify_WrongPassword_ReturnsFalse()
    {
        // Arrange
        var (salt, hash) = PasswordHasher.Hash("correct", iterations: 1_000);

        // Act
        bool result = PasswordHasher.Verify("wrong", salt, hash, iterations: 1_000);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Hash_TwoCalls_ProduceDifferentSalts()
    {
        // Arrange / Act
        var (salt1, _) = PasswordHasher.Hash("password", iterations: 1_000);
        var (salt2, _) = PasswordHasher.Hash("password", iterations: 1_000);

        // Assert: per-user 랜덤 salt — 같은 패스워드여도 salt가 달라야 Rainbow Table 공격을 차단
        Assert.False(salt1.AsSpan().SequenceEqual(salt2));
    }

    [Fact]
    public void Hash_OutputSizes_AreCorrect()
    {
        // Arrange / Act
        var (salt, hash) = PasswordHasher.Hash("password", iterations: 1_000);

        // Assert: NIST 권고 salt=16B, SHA-256 다이제스트=32B
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
    }

    /// <summary>
    /// admin / password123 으로 INSERT 문을 생성합니다.
    /// schema.sql 시드가 필요할 때 이 테스트를 실행하고 출력을 MySQL에 붙여 넣으세요.
    /// (방법 B — schema.sql 주석 참조)
    /// </summary>
    [Fact]
    public void PasswordHasher_GenerateSeedSql()
    {
        const string username = "admin";
        const string password = "password123";

        var (salt, hash) = PasswordHasher.Hash(password);

        // MySQL INSERT 문 출력 (BINARY literal은 0x 접두어 헥스)
        string saltHex = Convert.ToHexString(salt);
        string hashHex = Convert.ToHexString(hash);
        Console.WriteLine("-- 아래 SQL을 gamedb에서 실행하세요:");
        Console.WriteLine($"INSERT INTO users (username, password_hash, salt) VALUES ('{username}', 0x{hashHex}, 0x{saltHex});");

        // 검증: 생성한 해시가 Verify를 통과하는지 확인
        Assert.True(PasswordHasher.Verify(password, salt, hash));
    }
}

// ──────────────────────────────────────────────────────────────────────────
// LoginService 테스트 (Tier 1 — 페이크 IUserStore / ITokenStore 사용)
// ──────────────────────────────────────────────────────────────────────────

/// <summary>인메모리 페이크 사용자 저장소 — 인프라 없이 LoginService를 단위 테스트하기 위한 대역.</summary>
file sealed class FakeUserStore : IUserStore
{
    private readonly Dictionary<string, UserRecord> _users = new(StringComparer.Ordinal);

    public void Add(long id, string username, string password, int iterations = 1_000)
    {
        var (salt, hash) = PasswordHasher.Hash(password, iterations);
        _users[username] = new UserRecord(id, username, hash, salt);
    }

    public Task<UserRecord?> FindByUsernameAsync(string username, CancellationToken ct = default)
        => Task.FromResult(_users.TryGetValue(username, out var u) ? u : null);
}

/// <summary>인메모리 페이크 토큰 저장소 — 저장 호출 기록 및 조회 검증용.</summary>
file sealed class FakeTokenStore : ITokenStore
{
    public List<(string Token, long UserId, TimeSpan Ttl)> Stored { get; } = [];

    public Task StoreAsync(string token, long userId, TimeSpan ttl, CancellationToken ct = default)
    {
        Stored.Add((token, userId, ttl));
        return Task.CompletedTask;
    }

    // TryGetUserIdAsync: ITokenStore 인터페이스 확장 — Stored 목록에서 토큰 매칭(테스트용, TTL 무시)
    public Task<long?> TryGetUserIdAsync(string token, CancellationToken ct = default)
    {
        var match = Stored.Find(s => s.Token == token);
        long? result = match.Token != null ? match.UserId : null;
        return Task.FromResult(result);
    }
}

public class LoginServiceTests
{
    private static LoginService BuildService(IUserStore userStore, ITokenStore tokenStore,
        TimeSpan? ttl = null, int iterations = 1_000)
        => new(userStore, tokenStore, ttl ?? TimeSpan.FromHours(1), iterations);

    // ── 성공 경로 ────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsSuccess()
    {
        // Arrange
        var store = new FakeUserStore();
        store.Add(1, "alice", "p@ssword", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var svc = BuildService(store, tokenStore);

        // Act
        var result = await svc.LoginAsync("alice", "p@ssword");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1L, result.UserId);
        Assert.Equal("alice", result.Username);
        Assert.NotEmpty(result.Token);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_StoresTokenInTokenStore()
    {
        // Arrange
        var store = new FakeUserStore();
        store.Add(2, "bob", "secret", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var ttl = TimeSpan.FromMinutes(30);
        var svc = BuildService(store, tokenStore, ttl: ttl);

        // Act
        var result = await svc.LoginAsync("bob", "secret");

        // Assert: 토큰이 ITokenStore.StoreAsync로 전달되었는지 확인
        Assert.Single(tokenStore.Stored);
        var (storedToken, storedUserId, storedTtl) = tokenStore.Stored[0];
        Assert.Equal(result.Token, storedToken);
        Assert.Equal(2L, storedUserId);
        Assert.Equal(ttl, storedTtl);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_TokenIsBase64Url()
    {
        // Arrange
        var store = new FakeUserStore();
        store.Add(3, "carol", "pass", iterations: 1_000);
        var svc = BuildService(store, new FakeTokenStore());

        // Act
        var result = await svc.LoginAsync("carol", "pass");

        // Assert: base64url 인코딩 — '+', '/', '=' 문자가 없어야 함
        Assert.DoesNotContain("+", result.Token);
        Assert.DoesNotContain("/", result.Token);
        Assert.DoesNotContain("=", result.Token);
        Assert.NotEmpty(result.Token);
    }

    // ── 실패 경로 ────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_WrongPassword_ReturnsFailure()
    {
        // Arrange
        var store = new FakeUserStore();
        store.Add(1, "dave", "correct", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var svc = BuildService(store, tokenStore);

        // Act
        var result = await svc.LoginAsync("dave", "wrong");

        // Assert
        Assert.False(result.Success);
        Assert.Empty(result.Token);
        // 실패 시 토큰 저장 호출이 없어야 함
        Assert.Empty(tokenStore.Stored);
    }

    [Fact]
    public async Task LoginAsync_UnknownUsername_ReturnsFailure()
    {
        // Arrange
        var store = new FakeUserStore(); // 사용자 없음
        var tokenStore = new FakeTokenStore();
        var svc = BuildService(store, tokenStore);

        // Act
        var result = await svc.LoginAsync("nobody", "anything");

        // Assert
        Assert.False(result.Success);
        Assert.Empty(result.Token);
        Assert.Empty(tokenStore.Stored);
    }

    [Fact]
    public async Task LoginAsync_EmptyUsername_ReturnsFailure()
    {
        // Arrange
        var store = new FakeUserStore();
        var svc = BuildService(store, new FakeTokenStore());

        // Act
        var result = await svc.LoginAsync(string.Empty, "password");

        // Assert: 빈 이름은 존재하지 않으므로 실패
        Assert.False(result.Success);
    }

    // ── 동시성·멱등성 ─────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_SameCreds_TwiceConcurrently_BothSucceed()
    {
        // Arrange
        var store = new FakeUserStore();
        store.Add(1, "eve", "pass", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var svc = BuildService(store, tokenStore);

        // Act: 동시 로그인 2회 (LoginService는 stateless → Thread-safe)
        var t1 = svc.LoginAsync("eve", "pass");
        var t2 = svc.LoginAsync("eve", "pass");
        var results = await Task.WhenAll(t1, t2);

        // Assert: 두 요청 모두 성공하고 서로 다른 토큰을 발급
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);
        Assert.NotEqual(results[0].Token, results[1].Token);
        Assert.Equal(2, tokenStore.Stored.Count);
    }
}
