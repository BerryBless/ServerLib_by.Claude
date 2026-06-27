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
    /// <remarks>
    /// QUALITY-I-03: iterations 기본값(100,000회 PBKDF2)을 사용하여 CI에서 수십 ms를 낭비하고
    /// Console.WriteLine 사이드이펙트가 있는 순수 단위 테스트 위반 도구성 메서드이므로 Skip 처리.
    /// 로컬에서 수동 실행: dotnet test --filter PasswordHasher_GenerateSeedSql
    /// </remarks>
    [Fact(Skip = "manual seed tool — run locally")]
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
    public List<(string Token, long UserId, string Username, TimeSpan Ttl)> Stored { get; } = [];

    public Task StoreAsync(string token, long userId, string username, TimeSpan ttl, CancellationToken ct = default)
    {
        Stored.Add((token, userId, username, ttl));
        return Task.CompletedTask;
    }

    // TryResolveAsync: ITokenStore 인터페이스 — Stored 목록에서 토큰 매칭 후 TokenInfo(UserId·Username) 반환(테스트용, TTL 무시)
    public Task<TokenInfo?> TryResolveAsync(string token, CancellationToken ct = default)
    {
        var match = Stored.Find(s => s.Token == token);
        TokenInfo? result = match.Token != null ? new TokenInfo(match.UserId, match.Username) : null;
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

        // Assert: 토큰·userId·username·TTL 모두 ITokenStore.StoreAsync로 전달되었는지 확인
        Assert.Single(tokenStore.Stored);
        var (storedToken, storedUserId, storedUsername, storedTtl) = tokenStore.Stored[0];
        Assert.Equal(result.Token, storedToken);
        Assert.Equal(2L, storedUserId);
        Assert.Equal("bob", storedUsername);
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
    public async Task LoginAsync_ValidCredentials_TokenStorePreservesUsername()
    {
        // Arrange: store→resolve 라운드트립에서 username이 손실되지 않는지 검증 (토큰 게이팅 Username 복원 핵심)
        var store = new FakeUserStore();
        store.Add(10, "alice_gate", "pass", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var svc = BuildService(store, tokenStore);

        // Act: 로그인 → 토큰 발급 후 TryResolveAsync로 역조회
        var result = await svc.LoginAsync("alice_gate", "pass");
        var info = await tokenStore.TryResolveAsync(result.Token);

        // Assert: UserId·Username 모두 올바르게 복원
        Assert.NotNull(info);
        Assert.Equal(10L, info!.Value.UserId);
        Assert.Equal("alice_gate", info.Value.Username);
    }

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

    // ── GAP-I-07: 이미 취소된 CancellationToken 전달 시 TaskCanceledException 전파 ─────────
    [Fact]
    public async Task LoginAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Task.Run(verify, cancelledToken)은 즉시 TaskCanceledException(←OperationCanceledException) 발생
        var store = new FakeUserStore();
        store.Add(1, "alice", "p@ssword", iterations: 1_000);
        var svc = BuildService(store, new FakeTokenStore());

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        // ThrowsAnyAsync: TaskCanceledException(파생 타입)을 포함해 허용
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.LoginAsync("alice", "p@ssword", cts.Token));
    }

    // ── GAP-I-08: DbMetrics 계측 경로 ────────────────────────────────────────────────────
    [Fact]
    public async Task LoginAsync_WithDbMetrics_SuccessPath_RecordsMysqlAndRedis()
    {
        // 성공 경로: MySQL SELECT 1회 + Redis SET 1회(토큰 저장) 기록
        // BuildService는 dbMetrics를 지원하지 않으므로 생성자 직접 호출
        var store = new FakeUserStore();
        store.Add(1, "alice", "p@ssword", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var dbMetrics = new DbMetrics();
        var svc = new LoginService(store, tokenStore, TimeSpan.FromHours(1), 1_000, dbMetrics);

        await svc.LoginAsync("alice", "p@ssword");

        var snap = dbMetrics.GetSnapshot();
        Assert.Equal(1, snap.MysqlCount);    // 사용자 조회 1회
        Assert.Equal(1, snap.RedisSetCount); // 토큰 저장 1회
    }

    [Fact]
    public async Task LoginAsync_WithDbMetrics_FailedAuth_RecordsMysqlOnly()
    {
        // 인증 실패(비밀번호 불일치): MySQL 기록됨, Redis 기록 안 됨
        var store = new FakeUserStore();
        store.Add(1, "alice", "p@ssword", iterations: 1_000);
        var tokenStore = new FakeTokenStore();
        var dbMetrics = new DbMetrics();
        var svc = new LoginService(store, tokenStore, TimeSpan.FromHours(1), 1_000, dbMetrics);

        await svc.LoginAsync("alice", "wrong-password"); // 인증 실패

        var snap = dbMetrics.GetSnapshot();
        Assert.Equal(1, snap.MysqlCount);    // 사용자 조회 1회
        Assert.Equal(0, snap.RedisSetCount); // 실패 → 토큰 저장 없음
    }
}
