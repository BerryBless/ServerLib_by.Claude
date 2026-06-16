using MySqlConnector;

namespace Server.Auth;

/// <summary>
/// MySQL 기반 사용자 저장소 구현입니다. ADO.NET 커넥션 풀을 사용합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. _connectionString은 불변이며 MySqlConnection은
/// per-query 생성 패턴으로 경합이 없습니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 DB 작업은 async/await로 반환됩니다.</description></item>
/// <item><description><b>SQL Injection:</b> 파라미터라이즈드 쿼리(@u 등)로 SQL 인젝션을 원천 차단합니다.</description></item>
/// </list>
/// </remarks>
internal sealed class MySqlUserStore : IUserStore
{
    // MySqlConnection: ADO.NET 커넥션 풀이 내부적으로 실제 TCP 소켓을 재사용합니다.
    // per-query 생성·폐기 패턴(using var conn = new MySqlConnection(...))이 안전한 이유:
    // Dispose() 호출 시 물리 연결이 끊기지 않고 풀로 반환되어 다음 요청이 재사용합니다.
    private readonly string _connectionString;

    /// <param name="connectionString">MySQL 연결 문자열입니다. (예: "Server=127.0.0.1;Database=gamedb;User ID=root;Password=...;")</param>
    internal MySqlUserStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task<UserRecord?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        // MySqlConnection: per-query 생성 — ADO.NET 풀이 물리 커넥션을 관리하므로 생성 비용은 사실상 O(1)
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        // @u 파라미터: 사용자 입력이 SQL로 해석되지 않도록 바인딩 — SQL 인젝션 방지 핵심
        cmd.CommandText = "SELECT id, username, password_hash, salt FROM users WHERE username = @u LIMIT 1";
        cmd.Parameters.AddWithValue("@u", username);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var id   = reader.GetInt64(0);
        var name = reader.GetString(1);
        // BINARY(32)/BINARY(16) 컬럼: MySqlConnector는 byte[]로 반환
        var hash = reader.GetFieldValue<byte[]>(2);
        var salt = reader.GetFieldValue<byte[]>(3);
        return new UserRecord(id, name, hash, salt);
    }

    /// <summary>
    /// users 테이블이 없으면 생성합니다. 서버 시작 시 1회 호출합니다.
    /// </summary>
    internal static async Task EnsureSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS users (
                id            BIGINT      AUTO_INCREMENT PRIMARY KEY,
                username      VARCHAR(64) NOT NULL UNIQUE,
                password_hash BINARY(32)  NOT NULL,
                salt          BINARY(16)  NOT NULL,
                created_at    DATETIME    DEFAULT CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 사용자가 존재하지 않으면 PBKDF2 해시를 생성하여 삽입합니다. 최초 1회 시드용입니다.
    /// </summary>
    /// <param name="connectionString">MySQL 연결 문자열입니다.</param>
    /// <param name="username">삽입할 사용자 이름입니다.</param>
    /// <param name="password">평문 비밀번호입니다. 내부에서 PBKDF2 해시로 변환됩니다.</param>
    internal static async Task SeedAsync(string connectionString, string username, string password,
        CancellationToken ct = default)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // 이미 존재하면 스킵
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = @u";
        checkCmd.Parameters.AddWithValue("@u", username);
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct));
        if (count > 0) return;

        // PBKDF2 해시 생성 — 시드는 저빈도이므로 Task.Run 없이 허용
        var (salt, hash) = PasswordHasher.Hash(password);

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = "INSERT INTO users (username, password_hash, salt) VALUES (@u, @h, @s)";
        insertCmd.Parameters.AddWithValue("@u", username);
        insertCmd.Parameters.AddWithValue("@h", hash);
        insertCmd.Parameters.AddWithValue("@s", salt);
        await insertCmd.ExecuteNonQueryAsync(ct);

        Console.WriteLine($"[Seed] 테스트 사용자 생성 완료: {username}  (SeedTestUser를 false로 되돌리세요)");
    }
}
