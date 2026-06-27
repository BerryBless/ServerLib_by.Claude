# DbPerfTest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MySQL+Redis를 실제 측정 경로에 넣은 처리량+지연 백분위(p50/p95/p99) 성능 테스트 하네스를 구축한다.

**Architecture:** SoakTest의 child-process driver 패턴을 재사용. `DbPerfClient`가 closed-loop(연결당 요청 1개 in-flight)으로 login(Id=10→MySQL+Redis write) / token-resolve(Id=12→Redis read) 혼합 워크로드를 실행하고, 서버측 `[DBSTATS]` 라인이 PBKDF2를 제외한 순수 DB 지연을 분리 계측한다.

**Tech Stack:** .NET 10, xUnit 2.9.2, ServerLib/Auth/AppConfig 프로젝트 참조, docker-compose(redis:7-alpine + mysql:8), System.Threading.Channels, System.Diagnostics.Stopwatch

## Global Constraints

- Target framework: `net10.0`, `Nullable enable`, `ImplicitUsings enable`
- 모든 네트워크/메모리 선언부에 내부동작 근거 인라인 주석 필수 (CLAUDE.md 규칙)
- public API 및 public 메서드에 상세 XML 문서 주석 필수 (Thread Safety / Memory / Blocking 포함)
- Hard check 실패 → exit 1, 하네스 초기화 실패 → exit 2, PASS → exit 0
- `[DBSTATS]` 라인 형식(ASCII key=value)은 `[STATS]`와 동일한 머신 파싱 계약

---

## File Map

**신규 파일:**
- `DbPerfTest/DbPerfTest.csproj`
- `DbPerfTest/Program.cs`
- `DbPerfTest/DbPerfOptions.cs`
- `DbPerfTest/ServerProcess.cs`
- `DbPerfTest/DbPerfClient.cs`
- `DbPerfTest/LatencyRecorder.cs`
- `DbPerfTest/ClientStats.cs`
- `DbPerfTest/DbPerfReport.cs`
- `DbPerfTest.Tests/DbPerfTest.Tests.csproj`
- `DbPerfTest.Tests/LatencyRecorderTests.cs`
- `DbPerfTest.Tests/DbPerfOptionsTests.cs`
- `DbPerfTest.Tests/DbPerfReportTests.cs`
- `docker-compose.yml`

**수정 파일:**
- `Auth/DbMetrics.cs` (NEW in Auth project — LoginService와 Server.Program이 공유)
- `Auth/LoginService.cs` — DbMetrics? 파라미터 추가, MySQL/Redis 구간 Stopwatch 계측
- `Server/Program.cs` — DbMetrics 생성·주입, 게이트 Redis GET 계측, [DBSTATS] 모니터 라인
- `ServerLib.Tests/DbMetricsTests.cs` (NEW — Auth 이미 참조 중)
- `ClaudeCodeStudy.sln` — DbPerfTest + DbPerfTest.Tests 프로젝트 추가
- `plan/dbperf_test_0627.md` (NEW)
- `CLAUDE.md` — 하네스 설명 갱신

---

## Task 1: docker-compose.yml

**Files:**
- Create: `docker-compose.yml` (솔루션 루트)

- [ ] **Step 1: Write docker-compose.yml**

```yaml
# docker-compose.yml — DbPerfTest 인프라
# 사용법: docker compose up -d
# MySQL 최초 init 시간 ~30s → 헬스체크로 DB-ready 게이트 확보
services:
  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 2s
      timeout: 3s
      retries: 10
      start_period: 5s

  mysql:
    image: mysql:8
    environment:
      MYSQL_ROOT_PASSWORD: password
      MYSQL_DATABASE: gamedb
    ports:
      - "3306:3306"
    volumes:
      - ./Auth/schema.sql:/docker-entrypoint-initdb.d/01_schema.sql
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "localhost", "-u", "root", "-ppassword"]
      interval: 3s
      timeout: 5s
      retries: 20
      start_period: 30s
```

- [ ] **Step 2: Verify compose syntax**

```
docker compose config
```

Expected: prints parsed config with no errors.

- [ ] **Step 3: Commit**

```
git add docker-compose.yml
git commit -m "추가: DbPerfTest용 redis+mysql docker-compose 인프라"
```

---

## Task 2: Auth/DbMetrics.cs — TDD

**Files:**
- Create: `Auth/DbMetrics.cs`
- Create: `ServerLib.Tests/DbMetricsTests.cs`

**Interfaces:**
- Produces: `DbMetrics` class, `DbStatsSnapshot` record — Task 3 (LoginService), Task 6 (ServerProcess) 에서 소비

- [ ] **Step 1: Write failing tests**

`ServerLib.Tests/DbMetricsTests.cs`:
```csharp
using Server.Auth;
using Xunit;

namespace ServerLib.Tests;

public class DbMetricsTests
{
    [Fact]
    public void RecordMysqlSelect_IncrementsCountAndAccumulates()
    {
        var m = new DbMetrics();
        m.RecordMysqlSelect(100L);
        m.RecordMysqlSelect(200L);
        var s = m.GetSnapshot();
        Assert.Equal(2, s.MysqlCount);
        Assert.Equal(150L, s.MysqlSelectAvgUs); // (100+200)/2
    }

    [Fact]
    public void RecordRedisSet_IncrementsCountAndAccumulates()
    {
        var m = new DbMetrics();
        m.RecordRedisSet(50L);
        var s = m.GetSnapshot();
        Assert.Equal(1, s.RedisSetCount);
        Assert.Equal(50L, s.RedisSetAvgUs);
    }

    [Fact]
    public void RecordRedisGet_IncrementsCountAndAccumulates()
    {
        var m = new DbMetrics();
        m.RecordRedisGet(10L);
        m.RecordRedisGet(20L);
        m.RecordRedisGet(30L);
        var s = m.GetSnapshot();
        Assert.Equal(3, s.RedisGetCount);
        Assert.Equal(20L, s.RedisGetAvgUs); // (10+20+30)/3
    }

    [Fact]
    public void GetSnapshot_ZeroCounts_ReturnsZeroAverages()
    {
        var m = new DbMetrics();
        var s = m.GetSnapshot();
        Assert.Equal(0, s.MysqlSelectAvgUs);
        Assert.Equal(0, s.RedisSetAvgUs);
        Assert.Equal(0, s.RedisGetAvgUs);
    }
}
```

- [ ] **Step 2: Run tests — expect compile failure**

```
dotnet test ServerLib.Tests --no-build 2>&1 | head -5
```

Expected: Build error — `DbMetrics` not found.

- [ ] **Step 3: Implement Auth/DbMetrics.cs**

```csharp
using System.Threading;

namespace Server.Auth;

/// <summary>MySQL/Redis DB 연산 지연을 lock-free로 누적합니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 모든 필드는 Interlocked로 원자적 갱신.
/// <b>[Memory:]</b> Zero-allocation. 힙 할당 없음.
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class DbMetrics
{
    // Interlocked.Add: 원자적 누적 — 다수 IO 스레드가 동시에 기록해도 손실 없음
    private long _mysqlSelectUs;
    private long _mysqlCount;
    private long _redisSetUs;
    private long _redisSetCount;
    private long _redisGetUs;
    private long _redisGetCount;

    /// <summary>MySQL SELECT 구간 지연(마이크로초)을 기록합니다.</summary>
    public void RecordMysqlSelect(long us)
    {
        Interlocked.Add(ref _mysqlSelectUs, us);
        Interlocked.Increment(ref _mysqlCount);
    }

    /// <summary>Redis SET 구간 지연(마이크로초)을 기록합니다.</summary>
    public void RecordRedisSet(long us)
    {
        Interlocked.Add(ref _redisSetUs, us);
        Interlocked.Increment(ref _redisSetCount);
    }

    /// <summary>Redis GET 구간 지연(마이크로초)을 기록합니다.</summary>
    public void RecordRedisGet(long us)
    {
        Interlocked.Add(ref _redisGetUs, us);
        Interlocked.Increment(ref _redisGetCount);
    }

    /// <summary>현재까지 누적된 평균 지연 스냅샷을 반환합니다.</summary>
    public DbStatsSnapshot GetSnapshot()
    {
        long mc  = Interlocked.Read(ref _mysqlCount);
        long rsc = Interlocked.Read(ref _redisSetCount);
        long rgc = Interlocked.Read(ref _redisGetCount);
        return new DbStatsSnapshot(
            MysqlSelectAvgUs: mc  > 0 ? Interlocked.Read(ref _mysqlSelectUs) / mc  : 0,
            RedisSetAvgUs:    rsc > 0 ? Interlocked.Read(ref _redisSetUs)    / rsc : 0,
            RedisGetAvgUs:    rgc > 0 ? Interlocked.Read(ref _redisGetUs)    / rgc : 0,
            MysqlCount:    mc,
            RedisSetCount: rsc,
            RedisGetCount: rgc);
    }
}

/// <summary>DB 연산 평균 지연 스냅샷입니다.</summary>
public readonly record struct DbStatsSnapshot(
    long MysqlSelectAvgUs,
    long RedisSetAvgUs,
    long RedisGetAvgUs,
    long MysqlCount,
    long RedisSetCount,
    long RedisGetCount);
```

- [ ] **Step 4: Run tests — expect PASS**

```
dotnet test ServerLib.Tests --filter "DbMetricsTests" -v minimal
```

Expected: 4 tests passed.

- [ ] **Step 5: Commit**

```
git add Auth/DbMetrics.cs ServerLib.Tests/DbMetricsTests.cs
git commit -m "추가: DbMetrics lock-free DB 연산 지연 계측기 (TDD)"
```

---

## Task 3: LoginService + Server.Program 계측

**Files:**
- Modify: `Auth/LoginService.cs`
- Modify: `Server/Program.cs`

**Interfaces:**
- Consumes: `DbMetrics`, `DbStatsSnapshot` from Task 2
- Produces: `[DBSTATS]` stdout 라인 — Task 6 (ServerProcess) 에서 파싱

- [ ] **Step 1: Modify Auth/LoginService.cs — add DbMetrics parameter and Stopwatch instrumentation**

생성자에 `DbMetrics?` 추가, `LoginAsync`에 Stopwatch 계측:

```csharp
// LoginService.cs — 변경된 생성자와 LoginAsync 전체
using System.Diagnostics;
using System.Security.Cryptography;

namespace Server.Auth;

public sealed class LoginService
{
    private readonly IUserStore  _userStore;
    private readonly ITokenStore _tokenStore;
    private readonly TimeSpan    _tokenTtl;
    private readonly int         _pbkdfIterations;
    // DbMetrics?: null이면 계측 비활성 — 하위호환 유지
    private readonly DbMetrics?  _dbMetrics;

    private static readonly byte[] DummySalt = new byte[PasswordHasher.SaltSize];
    private static readonly byte[] DummyHash = new byte[PasswordHasher.HashSize];

    public LoginService(
        IUserStore userStore, ITokenStore tokenStore,
        TimeSpan tokenTtl, int pbkdfIterations,
        DbMetrics? dbMetrics = null)
    {
        _userStore       = userStore;
        _tokenStore      = tokenStore;
        _tokenTtl        = tokenTtl;
        _pbkdfIterations = pbkdfIterations;
        _dbMetrics       = dbMetrics;
    }

    public async Task<LoginResult> LoginAsync(
        string username, string password, CancellationToken ct = default)
    {
        // ① MySQL 사용자 조회 — Stopwatch로 순수 DB RTT 계측 (PBKDF2 포함 안 됨)
        long sw = Stopwatch.GetTimestamp();
        var user = await _userStore.FindByUsernameAsync(username, ct);
        _dbMetrics?.RecordMysqlSelect(
            (Stopwatch.GetTimestamp() - sw) * 1_000_000L / Stopwatch.Frequency);

        if (user is null)
        {
            await Task.Run(
                () => PasswordHasher.Verify(password, DummySalt, DummyHash, _pbkdfIterations), ct);
            return new LoginResult(false);
        }

        var salt       = user.Salt;
        var storedHash = user.PasswordHash;
        var iterations = _pbkdfIterations;
        bool valid = await Task.Run(
            () => PasswordHasher.Verify(password, salt, storedHash, iterations), ct);
        if (!valid) return new LoginResult(false);

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        // ④ Redis SET — Stopwatch로 순수 Redis RTT 계측
        sw = Stopwatch.GetTimestamp();
        await _tokenStore.StoreAsync(token, user.Id, user.Username, _tokenTtl, ct);
        _dbMetrics?.RecordRedisSet(
            (Stopwatch.GetTimestamp() - sw) * 1_000_000L / Stopwatch.Frequency);

        return new LoginResult(true, user.Id, user.Username, token);
    }
}
```

- [ ] **Step 2: Modify Server/Program.cs — create DbMetrics, inject, gate instrumentation, [DBSTATS] monitor**

**(a) DbMetrics 생성** — `redis = ConnectionMultiplexer.Connect(...)` 직후 (현재 Server/Program.cs:59):

현재 코드:
```csharp
if (cfg.Features.EnableLogin || cfg.Features.RequireAuth)
{
    redis = ConnectionMultiplexer.Connect(cfg.Auth.RedisConnectionString);
    tokenStore = new RedisTokenStore(redis);
    if (cfg.Features.EnableLogin)
    {
        ...
        loginService = new LoginService(
            new MySqlUserStore(cfg.Auth.MySqlConnectionString),
            tokenStore,
            TimeSpan.FromSeconds(cfg.Auth.TokenTtlSeconds),
            cfg.Auth.PbkdfIterations);
```

변경 후:
```csharp
DbMetrics? dbMetrics = null;
if (cfg.Features.EnableLogin || cfg.Features.RequireAuth)
{
    redis = ConnectionMultiplexer.Connect(cfg.Auth.RedisConnectionString);
    tokenStore = new RedisTokenStore(redis);
    // DbMetrics: lock-free 누적 카운터 — IO 스레드에서 RecordXxx 호출, 모니터 루프에서 GetSnapshot 읽기
    dbMetrics = new DbMetrics();
    if (cfg.Features.EnableLogin)
    {
        ...
        loginService = new LoginService(
            new MySqlUserStore(cfg.Auth.MySqlConnectionString),
            tokenStore,
            TimeSpan.FromSeconds(cfg.Auth.TokenTtlSeconds),
            cfg.Auth.PbkdfIterations,
            dbMetrics);   // <-- 추가
```

**(b) 게이트 핸들러** — `Server/Program.cs` AuthTokenPacket 분기 (현재 :241):

현재:
```csharp
var info = await tokenStore.TryResolveAsync(tok.Token);
```

변경:
```csharp
// Stopwatch: 순수 Redis GET RTT 계측 — PBKDF2 없는 유일한 DB read 경로
long gateSw = Stopwatch.GetTimestamp();
var info = await tokenStore.TryResolveAsync(tok.Token);
dbMetrics?.RecordRedisGet(
    (Stopwatch.GetTimestamp() - gateSw) * 1_000_000L / Stopwatch.Frequency);
```

**(c) 모니터 루프** — `[STATS]` Console.WriteLine 직후 (현재 :607):

```csharp
// [DBSTATS]: DbPerfTest 하네스가 머신 파싱하는 DB 연산 평균 지연 신호.
// [STATS]와 동일한 ASCII key=value 형식. EnableLogin/RequireAuth가 비활성이면 출력 안 됨.
if (dbMetrics is not null)
{
    var ds = dbMetrics.GetSnapshot();
    Console.WriteLine(
        $"[DBSTATS] mysqlSelectAvgUs={ds.MysqlSelectAvgUs} " +
        $"redisGetAvgUs={ds.RedisGetAvgUs} " +
        $"redisSetAvgUs={ds.RedisSetAvgUs} " +
        $"mysqlCount={ds.MysqlCount} " +
        $"redisGetCount={ds.RedisGetCount} " +
        $"redisSetCount={ds.RedisSetCount}");
}
```

- [ ] **Step 3: Build to verify no errors**

```
dotnet build -c Release --project Server 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Run existing tests to verify no regression**

```
dotnet test ServerLib.Tests -v minimal
```

Expected: all tests pass.

- [ ] **Step 5: Commit**

```
git add Auth/LoginService.cs Server/Program.cs
git commit -m "추가: DB 연산 Stopwatch 계측 및 [DBSTATS] 모니터 라인 (MySQL/Redis 순수 지연 분리)"
```

---

## Task 4: Project Scaffold

**Files:**
- Create: `DbPerfTest/DbPerfTest.csproj`
- Create: `DbPerfTest.Tests/DbPerfTest.Tests.csproj`
- Modify: `ClaudeCodeStudy.sln`

- [ ] **Step 1: Create DbPerfTest.csproj**

```xml
<!-- DbPerfTest/DbPerfTest.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>DbPerfTest</RootNamespace>
    <AssemblyName>DbPerfTest</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- ServerLib: ServerNet 팩토리, IClientConnection, 패킷 타입, BinaryPacketSerializer -->
    <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create DbPerfTest.Tests.csproj**

```xml
<!-- DbPerfTest.Tests/DbPerfTest.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <!-- DbPerfTest: LatencyRecorder, DbPerfOptions, DbPerfReport, ClientStats 테스트 -->
    <ProjectReference Include="..\DbPerfTest\DbPerfTest.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add projects to solution**

```
dotnet sln ClaudeCodeStudy.sln add DbPerfTest/DbPerfTest.csproj
dotnet sln ClaudeCodeStudy.sln add DbPerfTest.Tests/DbPerfTest.Tests.csproj
```

- [ ] **Step 4: Create placeholder Program.cs so DbPerfTest compiles**

```csharp
// DbPerfTest/Program.cs — placeholder (Task 10에서 완성)
Console.WriteLine("DbPerfTest — DB 성능 테스트 하네스");
```

- [ ] **Step 5: Build scaffold**

```
dotnet build -c Release --project DbPerfTest 2>&1 | tail -3
```

Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```
git add DbPerfTest/ DbPerfTest.Tests/ ClaudeCodeStudy.sln
git commit -m "추가: DbPerfTest + DbPerfTest.Tests 프로젝트 스캐폴드"
```

---

## Task 5: LatencyRecorder — TDD

**Files:**
- Create: `DbPerfTest/LatencyRecorder.cs`
- Create: `DbPerfTest.Tests/LatencyRecorderTests.cs`

**Interfaces:**
- Produces: `LatencyRecorder`, `PercentileResult` — Task 8 (DbPerfReport), Task 9 (DbPerfClient), Task 10 (Program.cs) 에서 소비

- [ ] **Step 1: Write failing tests**

`DbPerfTest.Tests/LatencyRecorderTests.cs`:
```csharp
using DbPerfTest;
using Xunit;

namespace DbPerfTest.Tests;

public class LatencyRecorderTests
{
    [Fact]
    public void RecordWrite_SingleValue_P50P95P99AllSame()
    {
        var r = new LatencyRecorder();
        r.RecordWrite(1000L);
        var p = r.GetWritePercentiles();
        Assert.Equal(1L, p.Count);
        Assert.Equal(1000L, p.P50);
        Assert.Equal(1000L, p.P95);
        Assert.Equal(1000L, p.P99);
        Assert.Equal(1000L, p.Max);
    }

    [Fact]
    public void RecordRead_MultipleValues_CorrectPercentiles()
    {
        var r = new LatencyRecorder();
        // 100개 값: 1~100
        for (int i = 1; i <= 100; i++) r.RecordRead((long)i);
        var p = r.GetReadPercentiles();
        Assert.Equal(100L, p.Count);
        Assert.Equal(50L, p.P50);  // ceil(0.50*100)-1 = 49 → sorted[49] = 50
        Assert.Equal(95L, p.P95);  // ceil(0.95*100)-1 = 94 → sorted[94] = 95
        Assert.Equal(99L, p.P99);  // ceil(0.99*100)-1 = 98 → sorted[98] = 99
        Assert.Equal(100L, p.Max);
    }

    [Fact]
    public void GetPercentiles_Empty_ReturnsDefaultZero()
    {
        var r = new LatencyRecorder();
        var w = r.GetWritePercentiles();
        var rd = r.GetReadPercentiles();
        Assert.Equal(0L, w.Count);
        Assert.Equal(0L, rd.Count);
    }

    [Fact]
    public void WriteCount_ReadCount_AreIndependent()
    {
        var r = new LatencyRecorder();
        r.RecordWrite(100L);
        r.RecordWrite(200L);
        r.RecordRead(50L);
        Assert.Equal(2L, r.WriteCount);
        Assert.Equal(1L, r.ReadCount);
    }

    [Fact]
    public void WriteAndRead_DoNotCrossContaminate()
    {
        var r = new LatencyRecorder();
        r.RecordWrite(9999L);
        r.RecordRead(1L);
        Assert.Equal(1L, r.GetReadPercentiles().Max);
        Assert.Equal(9999L, r.GetWritePercentiles().Max);
    }
}
```

- [ ] **Step 2: Run tests — expect build failure**

```
dotnet test DbPerfTest.Tests --no-build 2>&1 | head -5
```

Expected: build error — `LatencyRecorder` not found.

- [ ] **Step 3: Implement DbPerfTest/LatencyRecorder.cs**

```csharp
using System.Threading;

namespace DbPerfTest;

/// <summary>write/read 지연(마이크로초)을 독립적으로 기록하고 백분위를 계산합니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> RecordWrite/RecordRead는 Thread-safe(lock). GetXxxPercentiles는
/// 측정 종료 후 단일 스레드에서 호출하는 것을 전제합니다.
/// <b>[Memory:]</b> 측정값당 8B(long). 30s * 100클라 * ~20req/s = ~24,000 항목 ≈ 192KB.
/// <b>[Blocking:]</b> RecordXxx는 very-short lock. GetXxxPercentiles는 Array.Sort O(n log n).
/// </remarks>
public sealed class LatencyRecorder
{
    // List<long>: 동적 크기 배열 — 측정 전 건수 미지, Capacity 자동 증가
    private readonly List<long> _writeUs = new();
    private readonly List<long> _readUs  = new();

    // 독립 락: write/read 교차 경합 최소화 — 각 경로가 독립 임계 구간 진입
    private readonly object _wLock = new();
    private readonly object _rLock = new();

    // Interlocked 카운터: 진행 중 조회(progress reporter)가 List.Count 대신 사용 — 락 없이 Thread-safe
    private long _writeCount;
    private long _readCount;

    /// <summary>write 요청 지연(마이크로초)을 기록합니다.</summary>
    public void RecordWrite(long microseconds)
    {
        lock (_wLock) _writeUs.Add(microseconds);
        Interlocked.Increment(ref _writeCount);
    }

    /// <summary>read 요청 지연(마이크로초)을 기록합니다.</summary>
    public void RecordRead(long microseconds)
    {
        lock (_rLock) _readUs.Add(microseconds);
        Interlocked.Increment(ref _readCount);
    }

    /// <summary>기록된 write 건수입니다. Thread-safe.</summary>
    public long WriteCount => Interlocked.Read(ref _writeCount);

    /// <summary>기록된 read 건수입니다. Thread-safe.</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    /// <summary>write 지연 백분위를 계산합니다. 측정 종료 후 단일 스레드에서 호출하세요.</summary>
    public PercentileResult GetWritePercentiles() => CalcPercentiles(_writeUs);

    /// <summary>read 지연 백분위를 계산합니다. 측정 종료 후 단일 스레드에서 호출하세요.</summary>
    public PercentileResult GetReadPercentiles() => CalcPercentiles(_readUs);

    private static PercentileResult CalcPercentiles(List<long> data)
    {
        if (data.Count == 0) return default;
        var sorted = data.ToArray();
        Array.Sort(sorted);
        return new PercentileResult(
            Count: sorted.LongLength,
            P50:   Ptile(sorted, 0.50),
            P95:   Ptile(sorted, 0.95),
            P99:   Ptile(sorted, 0.99),
            Max:   sorted[^1]);
    }

    private static long Ptile(long[] sorted, double p)
    {
        int idx = Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1);
        return sorted[Math.Min(idx, sorted.Length - 1)];
    }
}

/// <summary>백분위 계산 결과입니다. 단위: 마이크로초.</summary>
public readonly record struct PercentileResult(
    long Count, long P50, long P95, long P99, long Max);
```

- [ ] **Step 4: Run tests — expect PASS**

```
dotnet test DbPerfTest.Tests --filter "LatencyRecorderTests" -v minimal
```

Expected: 5 tests passed.

- [ ] **Step 5: Commit**

```
git add DbPerfTest/LatencyRecorder.cs DbPerfTest.Tests/LatencyRecorderTests.cs
git commit -m "추가: LatencyRecorder write/read 분리 백분위 계산기 (TDD)"
```

---

## Task 6: DbPerfOptions — TDD

**Files:**
- Create: `DbPerfTest/DbPerfOptions.cs`
- Create: `DbPerfTest.Tests/DbPerfOptionsTests.cs`

**Interfaces:**
- Produces: `DbPerfOptions` — Task 7 (ServerProcess), Task 8 (DbPerfReport), Task 9 (DbPerfClient), Task 10 (Program.cs) 에서 소비

- [ ] **Step 1: Write failing tests**

`DbPerfTest.Tests/DbPerfOptionsTests.cs`:
```csharp
using DbPerfTest;
using Xunit;

namespace DbPerfTest.Tests;

public class DbPerfOptionsTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsDefaults()
    {
        var o = DbPerfOptions.Parse([]);
        Assert.Equal(20, o.Clients);
        Assert.Equal(30, o.DurationSeconds);
        Assert.Equal(5,  o.WarmupSeconds);
        Assert.Equal(80, o.ReadParts);
        Assert.Equal(20, o.WriteParts);
    }

    [Fact]
    public void Parse_ReadWriteRatio_ParsesCorrectly()
    {
        var o = DbPerfOptions.Parse(["--read-write-ratio", "95:5"]);
        Assert.Equal(95, o.ReadParts);
        Assert.Equal(5,  o.WriteParts);
    }

    [Fact]
    public void Parse_PresetReadHeavy_Sets95_5()
    {
        var o = DbPerfOptions.Parse(["--preset", "read-heavy"]);
        Assert.Equal(95, o.ReadParts);
        Assert.Equal(5,  o.WriteParts);
    }

    [Fact]
    public void Parse_PresetBalanced_Sets50_50()
    {
        var o = DbPerfOptions.Parse(["--preset", "balanced"]);
        Assert.Equal(50, o.ReadParts);
        Assert.Equal(50, o.WriteParts);
    }

    [Fact]
    public void IsReadOp_80_20_Ratio_ReturnsCorrectDistribution()
    {
        var o = DbPerfOptions.Parse(["--read-write-ratio", "80:20"]);
        int reads = 0, writes = 0;
        for (int i = 0; i < 100; i++)
        {
            if (o.IsReadOp(i)) reads++;
            else writes++;
        }
        Assert.Equal(80, reads);
        Assert.Equal(20, writes);
    }

    [Fact]
    public void Parse_TargetThroughput_ParsesCorrectly()
    {
        var o = DbPerfOptions.Parse(["--target-throughput", "500"]);
        Assert.Equal(500L, o.TargetThroughput);
    }
}
```

- [ ] **Step 2: Run tests — expect build failure**

```
dotnet test DbPerfTest.Tests --no-build 2>&1 | head -5
```

Expected: build error.

- [ ] **Step 3: Implement DbPerfTest/DbPerfOptions.cs**

```csharp
namespace DbPerfTest;

/// <summary>DbPerfTest CLI 파싱 결과입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only). Thread-safe.
/// <b>[Blocking:]</b> Parse()는 Non-blocking. 배열 순회만 수행.
/// </remarks>
public sealed class DbPerfOptions
{
    /// <summary>동시 클라이언트 수입니다.</summary>
    public int    Clients           { get; init; } = 20;
    /// <summary>게임 서버 포트입니다.</summary>
    public int    Port              { get; init; } = 9100;
    /// <summary>관리 포트입니다.</summary>
    public int    AdminPort         { get; init; } = 9101;
    /// <summary>측정 시간(초). warmup 이후부터 카운팅됩니다.</summary>
    public int    DurationSeconds   { get; init; } = 30;
    /// <summary>warmup 폐기 시간(초). cold JIT·cold DB 풀 오염 방지.</summary>
    public int    WarmupSeconds     { get; init; } = 5;
    /// <summary>read:write 비율의 read 부분입니다. IsReadOp()에서 사용됩니다.</summary>
    public int    ReadParts         { get; init; } = 80;
    /// <summary>read:write 비율의 write 부분입니다.</summary>
    public int    WriteParts        { get; init; } = 20;
    /// <summary>외부 서버 부착 모드입니다. true이면 Server.exe를 자식으로 구동하지 않습니다.</summary>
    public bool   Attach            { get; init; } = false;
    /// <summary>Hard FAIL 임계: 총 throughput(req/s) 하한. null이면 무제한.</summary>
    public long?  TargetThroughput  { get; init; } = null;
    /// <summary>Hard FAIL 임계: write 또는 read p99 상한(밀리초). null이면 무제한.</summary>
    public long?  TargetP99Ms       { get; init; } = null;
    /// <summary>Redis 연결 문자열 오버라이드. null이면 서버 기본값 사용.</summary>
    public string? RedisConn        { get; init; } = null;
    /// <summary>MySQL 연결 문자열 오버라이드. null이면 서버 기본값 사용.</summary>
    public string? MySqlConn        { get; init; } = null;
    /// <summary>PBKDF2 반복 횟수 오버라이드. null이면 서버 기본값(100,000) 사용.</summary>
    public int?   PbkdfIterations   { get; init; } = null;
    /// <summary>로그인에 사용할 사용자 이름입니다. SeedTestUser와 일치해야 합니다.</summary>
    public string Username          { get; init; } = "admin";
    /// <summary>로그인에 사용할 비밀번호입니다.</summary>
    public string Password          { get; init; } = "password123";

    /// <summary>counter번째 요청이 read여야 하는지 반환합니다.</summary>
    public bool IsReadOp(int counter) =>
        (counter % (ReadParts + WriteParts)) < ReadParts;

    /// <summary>CLI 인자를 파싱합니다.</summary>
    public static DbPerfOptions Parse(string[] args)
    {
        int   clients = 20, port = 9100, adminPort = 9101;
        int   duration = 30, warmup = 5;
        int   readParts = 80, writeParts = 20;
        bool  attach = false;
        long? targetTput = null, targetP99 = null;
        string? redisConn = null, mysqlConn = null;
        int?  pbkdf = null;
        string username = "admin", password = "password123";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--clients"           when i+1<args.Length: clients    = int.Parse(args[++i]); break;
                case "--port"              when i+1<args.Length: port       = int.Parse(args[++i]); break;
                case "--admin-port"        when i+1<args.Length: adminPort  = int.Parse(args[++i]); break;
                case "--duration"          when i+1<args.Length: duration   = int.Parse(args[++i]); break;
                case "--warmup-seconds"    when i+1<args.Length: warmup     = int.Parse(args[++i]); break;
                case "--target-throughput" when i+1<args.Length: targetTput = long.Parse(args[++i]); break;
                case "--target-p99-ms"     when i+1<args.Length: targetP99  = long.Parse(args[++i]); break;
                case "--redis-conn"        when i+1<args.Length: redisConn  = args[++i]; break;
                case "--mysql-conn"        when i+1<args.Length: mysqlConn  = args[++i]; break;
                case "--pbkdf-iterations"  when i+1<args.Length: pbkdf      = int.Parse(args[++i]); break;
                case "--username"          when i+1<args.Length: username   = args[++i]; break;
                case "--password"          when i+1<args.Length: password   = args[++i]; break;
                case "--attach": attach = true; break;

                case "--read-write-ratio" when i+1<args.Length:
                    var parts = args[++i].Split(':');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out int r)
                        && int.TryParse(parts[1], out int w)
                        && r >= 0 && w >= 0 && r + w > 0)
                    {
                        readParts = r; writeParts = w;
                    }
                    break;

                case "--preset" when i+1<args.Length:
                    switch (args[++i])
                    {
                        case "read-heavy": readParts = 95; writeParts = 5;  break;
                        case "balanced":   readParts = 50; writeParts = 50; break;
                    }
                    break;

                case "--help": case "-h":
                    PrintHelp(); Environment.Exit(0); break;
            }
        }

        return new DbPerfOptions
        {
            Clients          = Math.Max(1, clients),
            Port             = port,
            AdminPort        = adminPort,
            DurationSeconds  = Math.Max(1, duration),
            WarmupSeconds    = Math.Max(0, warmup),
            ReadParts        = Math.Max(0, readParts),
            WriteParts       = Math.Max(0, writeParts),
            Attach           = attach,
            TargetThroughput = targetTput,
            TargetP99Ms      = targetP99,
            RedisConn        = redisConn,
            MySqlConn        = mysqlConn,
            PbkdfIterations  = pbkdf,
            Username         = username,
            Password         = password,
        };
    }

    private static void PrintHelp() =>
        Console.WriteLine("""
            DbPerfTest — DB 포함 성능 테스트 하네스

            사용법:
              dotnet run -c Release --project DbPerfTest -- [options]

            공통:
              --clients N               동시 클라이언트 수                  (기본: 20)
              --port N                  게임 서버 포트                      (기본: 9100)
              --admin-port N            관리 포트                           (기본: 9101)
              --duration N              측정 시간(초)                       (기본: 30)
              --warmup-seconds N        warmup 폐기 시간(초)                (기본: 5)
              --read-write-ratio R:W    read:write 비율 e.g. 80:20          (기본: 80:20)
              --preset read-heavy       --read-write-ratio 95:5
              --preset balanced         --read-write-ratio 50:50
              --attach                  외부 서버 부착 모드

            판정 임계:
              --target-throughput N     총 req/s 하한 (미설정 시 무제한)
              --target-p99-ms N         p99 상한(ms) (미설정 시 무제한)

            DB 오버라이드:
              --redis-conn STR          Redis 연결 문자열
              --mysql-conn STR          MySQL 연결 문자열
              --pbkdf-iterations N      PBKDF2 반복 횟수
              --username STR            로그인 사용자 이름                  (기본: admin)
              --password STR            로그인 비밀번호                     (기본: password123)

            예시:
              dotnet run -c Release --project DbPerfTest -- --clients 20 --duration 30
              dotnet run -c Release --project DbPerfTest -- --preset read-heavy --clients 50
              dotnet run -c Release --project DbPerfTest -- --target-p99-ms 1 (의도적 FAIL)

            종료코드: 0=PASS, 1=FAIL, 2=하네스 초기화 실패
            """);
}
```

- [ ] **Step 4: Run tests — expect PASS**

```
dotnet test DbPerfTest.Tests --filter "DbPerfOptionsTests" -v minimal
```

Expected: 6 tests passed.

- [ ] **Step 5: Commit**

```
git add DbPerfTest/DbPerfOptions.cs DbPerfTest.Tests/DbPerfOptionsTests.cs
git commit -m "추가: DbPerfOptions CLI 파싱 + IsReadOp 비율 분배 (TDD)"
```

---

## Task 7: ClientStats

**Files:**
- Create: `DbPerfTest/ClientStats.cs`

**Interfaces:**
- Produces: `ClientStats` — Task 8 (DbPerfReport), Task 9 (DbPerfClient), Task 10 (Program.cs) 에서 소비

- [ ] **Step 1: Create DbPerfTest/ClientStats.cs**

```csharp
using System.Threading;

namespace DbPerfTest;

/// <summary>다수 DbPerfClient Task에서 공유하는 lock-free 집계 카운터입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. Interlocked 연산만 사용.
/// <b>[Memory:]</b> Zero-allocation.
/// </remarks>
public sealed class ClientStats
{
    // Interlocked: 다수 클라이언트 Task에서 경쟁 없이 원자적 증가
    private long _errors;
    private long _connects;

    /// <summary>클라이언트 오류 수를 1 증가시킵니다.</summary>
    public void IncError()   => Interlocked.Increment(ref _errors);
    /// <summary>연결 성공 수를 1 증가시킵니다.</summary>
    public void IncConnect() => Interlocked.Increment(ref _connects);

    /// <summary>총 오류 수입니다.</summary>
    public long Errors   => Interlocked.Read(ref _errors);
    /// <summary>총 연결 성공 수입니다.</summary>
    public long Connects => Interlocked.Read(ref _connects);
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build -c Release --project DbPerfTest 2>&1 | tail -3
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```
git add DbPerfTest/ClientStats.cs
git commit -m "추가: ClientStats lock-free 에러/연결 집계 카운터"
```

---

## Task 8: DbPerfReport — TDD

**Files:**
- Create: `DbPerfTest/DbPerfReport.cs`
- Create: `DbPerfTest.Tests/DbPerfReportTests.cs`

**Interfaces:**
- Consumes: `LatencyRecorder`, `ClientStats`, `ServerStatsSnapshot`, `DbStatsSnapshot`, `DbPerfOptions` from prior tasks
- Produces: `DbPerfReport` — Task 10 (Program.cs) 에서 소비

Note: `ServerStatsSnapshot` and `DbStatsSnapshot` come from `DbPerfTest/ServerProcess.cs` (Task 9). For now, define them as stubs here and fill in Task 9.

- [ ] **Step 1: Define ServerStatsSnapshot + DbStatsSnapshot stubs in DbPerfTest**

`DbPerfTest/ServerProcess.cs` (partial — records only, TryStart 등은 Task 9에서 완성):
```csharp
namespace DbPerfTest;

/// <summary>[STATS] 라인에서 파싱한 서버 스냅샷입니다.</summary>
public sealed record ServerStatsSnapshot(
    long Received  = 0,
    long Sessions  = 0,
    long HeapBytes = 0);

/// <summary>[DBSTATS] 라인에서 파싱한 DB 연산 평균 지연 스냅샷입니다.</summary>
public sealed record DbStatsSnapshot(
    long MysqlSelectAvgUs = 0,
    long RedisGetAvgUs    = 0,
    long RedisSetAvgUs    = 0,
    long MysqlCount       = 0,
    long RedisGetCount    = 0,
    long RedisSetCount    = 0);
```

- [ ] **Step 2: Write failing tests**

`DbPerfTest.Tests/DbPerfReportTests.cs`:
```csharp
using DbPerfTest;
using Xunit;

namespace DbPerfTest.Tests;

public class DbPerfReportTests
{
    private static DbPerfOptions DefaultOpt() => DbPerfOptions.Parse([]);

    private static LatencyRecorder MakeRecorder(
        long[] writeUs, long[] readUs)
    {
        var r = new LatencyRecorder();
        foreach (var v in writeUs) r.RecordWrite(v);
        foreach (var v in readUs)  r.RecordRead(v);
        return r;
    }

    [Fact]
    public void Evaluate_HappyPath_OverallPass()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var snap = new ServerStatsSnapshot(Received: 10, Sessions: 0, HeapBytes: 10_000_000);
        var dbSnap = new DbStatsSnapshot(
            MysqlSelectAvgUs: 38, RedisGetAvgUs: 1, RedisSetAvgUs: 1,
            MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1);
        var opt = DefaultOpt();

        var report = DbPerfReport.Evaluate(
            recorder, elapsedSec: 1.0, new ClientStats(),
            snap, baselineHeap: 9_000_000,
            serverCrashed: false, attachMode: false,
            dbSnap, opt);

        Assert.True(report.OverallPass);
        Assert.False(report.NoDbData);
    }

    [Fact]
    public void Evaluate_SessionLeak_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var snap = new ServerStatsSnapshot(Sessions: 1, HeapBytes: 10_000_000);
        var opt = DefaultOpt();

        var report = DbPerfReport.Evaluate(
            recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            new DbStatsSnapshot(MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1), opt);

        Assert.False(report.OverallPass);
        Assert.True(report.SessionLeak);
    }

    [Fact]
    public void Evaluate_NoDbData_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var snap = new ServerStatsSnapshot(Sessions: 0, HeapBytes: 10_000_000);
        var opt = DefaultOpt();

        var report = DbPerfReport.Evaluate(
            recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            dbSnap: null, opt);

        Assert.False(report.OverallPass);
        Assert.True(report.NoDbData);
    }

    [Fact]
    public void Evaluate_ThroughputBelowTarget_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        // 1 write + 1 read in 1s = 2 req/s, target=1000
        var opt = DbPerfOptions.Parse(["--target-throughput", "1000"]);
        var snap = new ServerStatsSnapshot(Sessions: 0, HeapBytes: 10_000_000);

        var report = DbPerfReport.Evaluate(
            recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            new DbStatsSnapshot(MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1), opt);

        Assert.False(report.OverallPass);
        Assert.True(report.ThroughputBelowTarget);
    }

    [Fact]
    public void Evaluate_LatencyAboveTarget_Fails()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]); // write 50ms, read 2ms
        // target p99 = 1ms — both paths exceed
        var opt = DbPerfOptions.Parse(["--target-p99-ms", "1"]);
        var snap = new ServerStatsSnapshot(Sessions: 0, HeapBytes: 10_000_000);

        var report = DbPerfReport.Evaluate(
            recorder, 1.0, new ClientStats(),
            snap, 9_000_000, false, false,
            new DbStatsSnapshot(MysqlCount: 1, RedisGetCount: 1, RedisSetCount: 1), opt);

        Assert.False(report.OverallPass);
        Assert.True(report.LatencyAboveTarget);
    }

    [Fact]
    public void Evaluate_AttachMode_SkipsServerChecks()
    {
        var recorder = MakeRecorder([50_000L], [2_000L]);
        var opt = DefaultOpt();

        // attachMode=true: crash/sessionLeak 무시
        var report = DbPerfReport.Evaluate(
            recorder, 1.0, new ClientStats(),
            null, 0, serverCrashed: true, attachMode: true,
            null, opt);

        // NoDbData는 attach 모드에서도 체크 — vacuous PASS 방지 위해 유지
        Assert.True(report.NoDbData);
        Assert.False(report.Crash);
        Assert.False(report.SessionLeak);
    }
}
```

- [ ] **Step 3: Run tests — expect build failure**

```
dotnet test DbPerfTest.Tests --no-build 2>&1 | head -5
```

Expected: build error — `DbPerfReport` not found.

- [ ] **Step 4: Implement DbPerfTest/DbPerfReport.cs**

```csharp
namespace DbPerfTest;

/// <summary>DbPerfTest 성능 측정 결과 및 Hard/Soft 판정 리포트입니다.</summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변(init-only). Thread-safe.
/// </remarks>
public sealed class DbPerfReport
{
    // ── Hard checks ──────────────────────────────────────────────────────────
    /// <summary>[Hard] 서버가 'q' 전에 종료(크래시)했는지를 나타냅니다.</summary>
    public bool Crash                 { get; init; }
    /// <summary>[Hard] 클라이언트 전원 종료 후 서버 세션이 남았는지를 나타냅니다.</summary>
    public bool SessionLeak           { get; init; }
    /// <summary>[Hard] 클라이언트 오류율 > 5%인지를 나타냅니다.</summary>
    public bool ClientErrorRateHigh   { get; init; }
    /// <summary>[Hard] 총 throughput이 목표 미달인지를 나타냅니다. TargetThroughput 미설정 시 항상 false.</summary>
    public bool ThroughputBelowTarget { get; init; }
    /// <summary>[Hard] write 또는 read p99가 목표 초과인지를 나타냅니다. TargetP99Ms 미설정 시 항상 false.</summary>
    public bool LatencyAboveTarget    { get; init; }
    /// <summary>[Hard] [DBSTATS] 라인 미수신(vacuous PASS 방지)입니다.</summary>
    public bool NoDbData              { get; init; }

    // ── Soft checks ───────────────────────────────────────────────────────────
    /// <summary>[Soft] 최종 heap > baseline × 4인지를 나타냅니다. verdict 무영향.</summary>
    public bool HeapGrowth            { get; init; }

    // ── 통계 ─────────────────────────────────────────────────────────────────
    public PercentileResult WritePercentiles { get; init; }
    public PercentileResult ReadPercentiles  { get; init; }
    public double WriteThroughput            { get; init; }
    public double ReadThroughput             { get; init; }
    public DbStatsSnapshot? DbStats          { get; init; }
    public long BaselineHeapBytes            { get; init; }
    public long FinalHeapBytes               { get; init; }

    /// <summary>Hard 체크 전부 통과 시 true입니다.</summary>
    public bool OverallPass => !Crash && !SessionLeak && !ClientErrorRateHigh
                            && !ThroughputBelowTarget && !LatencyAboveTarget && !NoDbData;

    /// <summary>측정 결과로부터 판정 리포트를 생성합니다.</summary>
    public static DbPerfReport Evaluate(
        LatencyRecorder recorder, double elapsedSec, ClientStats clientStats,
        ServerStatsSnapshot? serverSnap, long baselineHeap,
        bool serverCrashed, bool attachMode,
        DbStatsSnapshot? dbSnap, DbPerfOptions opt)
    {
        var writeP = recorder.GetWritePercentiles();
        var readP  = recorder.GetReadPercentiles();

        double writeTput = elapsedSec > 0 ? recorder.WriteCount / elapsedSec : 0;
        double readTput  = elapsedSec > 0 ? recorder.ReadCount  / elapsedSec : 0;
        double totalTput = writeTput + readTput;

        long connects = clientStats.Connects;
        long errors   = clientStats.Errors;
        long sessions = serverSnap?.Sessions  ?? 0;
        long heap     = serverSnap?.HeapBytes ?? 0;

        bool crash        = serverCrashed && !attachMode;
        bool sessionLeak  = !attachMode && sessions != 0;
        bool highErrRate  = connects > 0 && (errors * 100 / connects) > 5;
        bool tputBelow    = opt.TargetThroughput.HasValue && totalTput < opt.TargetThroughput.Value;
        // p99는 마이크로초 단위 → /1000으로 밀리초 변환
        bool latAbove     = opt.TargetP99Ms.HasValue &&
                            (writeP.P99 / 1000 > opt.TargetP99Ms.Value ||
                             readP.P99  / 1000 > opt.TargetP99Ms.Value);
        bool noDbData     = dbSnap is null;
        bool heapGrowth   = !attachMode && baselineHeap > 0 && heap > baselineHeap * 4;

        return new DbPerfReport
        {
            Crash                 = crash,
            SessionLeak           = sessionLeak,
            ClientErrorRateHigh   = highErrRate,
            ThroughputBelowTarget = tputBelow,
            LatencyAboveTarget    = latAbove,
            NoDbData              = noDbData,
            HeapGrowth            = heapGrowth,
            WritePercentiles      = writeP,
            ReadPercentiles       = readP,
            WriteThroughput       = writeTput,
            ReadThroughput        = readTput,
            DbStats               = dbSnap,
            BaselineHeapBytes     = baselineHeap,
            FinalHeapBytes        = heap,
        };
    }

    /// <summary>판정 결과를 콘솔에 출력합니다.</summary>
    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  [DbPerf] write  rps={WriteThroughput:F1}  " +
                          $"p50={WritePercentiles.P50/1000}ms  p95={WritePercentiles.P95/1000}ms  " +
                          $"p99={WritePercentiles.P99/1000}ms  max={WritePercentiles.Max/1000}ms  " +
                          $"n={WritePercentiles.Count:N0}");
        Console.WriteLine($"  [DbPerf]  read  rps={ReadThroughput:F1}  " +
                          $"p50={ReadPercentiles.P50/1000}ms  p95={ReadPercentiles.P95/1000}ms  " +
                          $"p99={ReadPercentiles.P99/1000}ms  max={ReadPercentiles.Max/1000}ms  " +
                          $"n={ReadPercentiles.Count:N0}");
        if (DbStats is { } ds)
            Console.WriteLine($"  [DbPerf] dbstats  mysql_select={ds.MysqlSelectAvgUs}µs(n={ds.MysqlCount})  " +
                              $"redis_get={ds.RedisGetAvgUs}µs(n={ds.RedisGetCount})  " +
                              $"redis_set={ds.RedisSetAvgUs}µs(n={ds.RedisSetCount})");
        Console.WriteLine($"  [DbPerf] heap  baseline={BaselineHeapBytes/1024:N0}KB  " +
                          $"final={FinalHeapBytes/1024:N0}KB");
        Console.WriteLine("  ⚠ known caveat: closed-loop은 지연 스파이크를 과소집계합니다");
        Console.WriteLine("───────────────────────────────────────────────────────");

        PrintCheck("Crash",              !Crash,                 "[Hard]");
        PrintCheck("SessionLeak",        !SessionLeak,           "[Hard]");
        PrintCheck("ClientErrorRate",    !ClientErrorRateHigh,   "[Hard]");
        PrintCheck("ThroughputTarget",   !ThroughputBelowTarget, "[Hard]");
        PrintCheck("LatencyTarget",      !LatencyAboveTarget,    "[Hard]");
        PrintCheck("NoDbData",           !NoDbData,              "[Hard]");
        PrintCheck("HeapGrowth",         !HeapGrowth,            "[Soft]");

        Console.WriteLine("───────────────────────────────────────────────────────");
        Console.WriteLine($"  RESULT {(OverallPass ? "PASS ✓" : "FAIL ✗")}");
        Console.WriteLine("═══════════════════════════════════════════════════════");
    }

    private static void PrintCheck(string name, bool pass, string kind)
    {
        string mark  = pass ? "✓" : "✗";
        string label = pass ? "OK" : "FAIL";
        Console.WriteLine($"  {mark} {name,-22} {kind}  {label}");
    }
}
```

- [ ] **Step 5: Run tests — expect PASS**

```
dotnet test DbPerfTest.Tests --filter "DbPerfReportTests" -v minimal
```

Expected: 6 tests passed.

- [ ] **Step 6: Commit**

```
git add DbPerfTest/DbPerfReport.cs DbPerfTest/ServerProcess.cs DbPerfTest.Tests/DbPerfReportTests.cs
git commit -m "추가: DbPerfReport Hard/Soft 판정 + ServerStatsSnapshot/DbStatsSnapshot 레코드 (TDD)"
```

---

## Task 9: DbPerfTest/ServerProcess.cs (완성)

**Files:**
- Modify: `DbPerfTest/ServerProcess.cs` (Task 8에서 records만 있음 → 전체 구현)

**Interfaces:**
- Consumes: `DbPerfOptions`, `ServerStatsSnapshot`, `DbStatsSnapshot`
- Produces: `ServerProcess` with `Latest`, `LatestDb`, `Crashed`, `WaitForReadinessAsync`, `WaitForStabilityAsync`, `DisposeAsync`

- [ ] **Step 1: Complete DbPerfTest/ServerProcess.cs**

기존 records를 유지하고 아래 클래스를 추가:

```csharp
using System.Diagnostics;

namespace DbPerfTest;

// (기존 ServerStatsSnapshot + DbStatsSnapshot records 유지)

/// <summary>
/// Server.exe 자식 프로세스를 구동하고 stdout의 [STATS]·[DBSTATS] 라인을 파싱합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Latest·LatestDb·Crashed는 volatile 기반으로 Thread-safe.
/// <b>[Memory:]</b> stdout 읽기는 별도 Task로 비동기 처리 — 메인 루프 블로킹 없음.
/// <b>[Blocking:]</b> DisposeAsync는 최대 5초 비동기 대기 후 Kill 보장.
/// </remarks>
public sealed class ServerProcess : IAsyncDisposable
{
    // Process: child Server.exe stdin/stdout 재지향 — CLI 오버라이드로 포트·모니터 주기 제어
    private readonly Process _proc;

    // volatile bool: _shutdownRequested를 IO 스레드 간 가시성 보장
    private volatile bool _shutdownRequested;
    // volatile bool: 첫 [STATS] 수신 플래그 — readiness 폴러가 읽음
    private volatile bool _hasStats;

    // volatile object: 불변 record 참조 교체 — 64비트에서 참조 쓰기는 원자적
    private volatile ServerStatsSnapshot _latest = new();
    // volatile object: [DBSTATS] 최신 스냅샷. 비계측 모드에서는 업데이트 안 됨.
    private volatile DbStatsSnapshot? _latestDb;

    private ServerProcess(Process proc) => _proc = proc;

    /// <summary>서버 크래시 여부입니다.</summary>
    public bool Crashed => _proc.HasExited && !_shutdownRequested;
    /// <summary>최신 [STATS] 스냅샷입니다.</summary>
    public ServerStatsSnapshot Latest => _latest;
    /// <summary>최신 [DBSTATS] 스냅샷입니다. [DBSTATS] 미수신 시 null.</summary>
    public DbStatsSnapshot? LatestDb => _latestDb;

    /// <summary>Server.exe를 자식 프로세스로 구동합니다.</summary>
    public static ServerProcess? TryStart(
        int port, int adminPort, int monitorIntervalSec,
        DbPerfOptions opt)
    {
        string? exePath = FindServerExe();
        if (exePath is null)
        {
            Console.Error.WriteLine("[DbPerfTest] Server.exe를 찾을 수 없습니다.");
            Console.Error.WriteLine("  먼저 빌드하세요: dotnet build -c Release --project Server");
            Console.Error.WriteLine("  또는 --attach 옵션으로 외부 서버에 부착하세요.");
            return null;
        }

        Console.WriteLine($"[DbPerfTest] 서버 구동: {exePath}");

        // EnableLogin=true: MySQL+Redis 계측 경로 활성화 필수
        // SeedTestUser=true: 최초 기동 시 테스트 유저 자동 삽입
        var sb = new System.Text.StringBuilder();
        sb.Append($"--Server:Port={port} ");
        sb.Append($"--Server:AdminPort={adminPort} ");
        sb.Append($"--Server:MonitorIntervalSeconds={monitorIntervalSec} ");
        sb.Append($"--Server:Features:EnableLogin=true ");
        sb.Append($"--Server:Auth:SeedTestUser=true ");
        // MaxConnectionsPerIp: 모든 클라가 127.0.0.1 → 기본값 초과 방지
        sb.Append($"--Server:MaxConnectionsPerIp={Math.Max(opt.Clients * 2, 100)}");

        if (opt.RedisConn is { } rc)
            sb.Append($" --Server:Auth:RedisConnectionString={rc}");
        if (opt.MySqlConn is { } mc)
            sb.Append($" --Server:Auth:MySqlConnectionString={mc}");
        if (opt.PbkdfIterations is { } pi)
            sb.Append($" --Server:Auth:PbkdfIterations={pi}");

        var psi = new ProcessStartInfo(exePath, sb.ToString())
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            UseShellExecute = false,
            CreateNoWindow  = true,
        };

        var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine("[DbPerfTest] Process.Start 실패");
            return null;
        }

        var sp = new ServerProcess(proc);
        _ = Task.Run(sp.ReadStdoutAsync);
        return sp;
    }

    private static string? FindServerExe()
    {
        string root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] candidates =
        [
            Path.Combine(root, "Server", "bin", "Release", "net10.0", "Server.exe"),
            Path.Combine(root, "Server", "bin", "Debug",   "net10.0", "Server.exe"),
        ];
        return Array.Find(candidates, File.Exists);
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                bool suppress = line.StartsWith("[STATS]",   StringComparison.Ordinal) ||
                                line.StartsWith("[DBSTATS]", StringComparison.Ordinal) ||
                                line.StartsWith("[Monitor]", StringComparison.Ordinal);

                if (!suppress)
                    Console.WriteLine($"  [Server] {line}");

                if (line.StartsWith("[STATS]", StringComparison.Ordinal))
                {
                    _latest   = ParseStats(line);
                    _hasStats = true;
                }
                else if (line.StartsWith("[DBSTATS]", StringComparison.Ordinal))
                {
                    _latestDb = ParseDbStats(line);
                }
            }
        }
        catch { /* 프로세스 종료 시 스트림 닫힘 — 정상 종료 */ }
    }

    // 형식: [STATS] received=N sessions=N heapBytes=N ...
    private static ServerStatsSnapshot ParseStats(string line) => new(
        ParseLong(line, "received="),
        ParseLong(line, "sessions="),
        ParseLong(line, "heapBytes="));

    // 형식: [DBSTATS] mysqlSelectAvgUs=N redisGetAvgUs=N redisSetAvgUs=N mysqlCount=N redisGetCount=N redisSetCount=N
    private static DbStatsSnapshot ParseDbStats(string line) => new(
        ParseLong(line, "mysqlSelectAvgUs="),
        ParseLong(line, "redisGetAvgUs="),
        ParseLong(line, "redisSetAvgUs="),
        ParseLong(line, "mysqlCount="),
        ParseLong(line, "redisGetCount="),
        ParseLong(line, "redisSetCount="));

    private static long ParseLong(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        int start = idx + key.Length;
        int end   = line.IndexOf(' ', start);
        if (end < 0) end = line.Length;
        return long.TryParse(line.AsSpan(start, end - start), out long v) ? v : 0;
    }

    /// <summary>첫 [STATS] 수신까지 대기합니다.</summary>
    public async Task<bool> WaitForReadinessAsync(int timeoutMs = 15_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_hasStats)       return true;
            if (_proc.HasExited) return false;
            await Task.Delay(200);
        }
        return false;
    }

    /// <summary>클라이언트 전원 종료 후 서버 안정화(sessions==0)를 대기하고 최종 스냅샷을 반환합니다.</summary>
    public async Task<ServerStatsSnapshot> WaitForStabilityAsync(int timeoutMs = 10_000)
    {
        long deadline    = Environment.TickCount64 + timeoutMs;
        long prevReceived = -1;
        int  stableCount  = 0;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(500);
            var snap = _latest;
            bool empty  = snap.Sessions == 0;
            bool stable = snap.Received == prevReceived;
            if (empty && stable)
            {
                if (++stableCount >= 3) return snap;
            }
            else stableCount = 0;
            prevReceived = snap.Received;
        }
        return _latest;
    }

    /// <summary>서버를 graceful 종료합니다.</summary>
    public async ValueTask DisposeAsync()
    {
        _shutdownRequested = true;
        if (_proc.HasExited) { _proc.Dispose(); return; }
        try
        {
            await _proc.StandardInput.WriteLineAsync("q");
            if (!_proc.WaitForExit(5000))
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* 이미 종료됨 */ }
        finally { _proc.Dispose(); }
    }
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build -c Release --project DbPerfTest 2>&1 | tail -3
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```
git add DbPerfTest/ServerProcess.cs
git commit -m "추가: DbPerfTest ServerProcess — [STATS]/[DBSTATS] 파싱 child-process 드라이버"
```

---

## Task 10: DbPerfClient

**Files:**
- Create: `DbPerfTest/DbPerfClient.cs`

**Interfaces:**
- Consumes: `DbPerfOptions`, `LatencyRecorder`, `ClientStats`
- Consumes: `ServerNet.CreateClient()`, `IClientConnection`, `BinaryPacketSerializer`, `PacketPool`, packets (all from ServerLib)

- [ ] **Step 1: Create DbPerfTest/DbPerfClient.cs**

```csharp
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using System.Diagnostics;
using System.Threading.Channels;

namespace DbPerfTest;

/// <summary>
/// closed-loop 방식으로 login(write) / token-resolve(read) 혼합 요청을 반복합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> RunAsync는 단일 Task에서 호출합니다. 여러 인스턴스 병렬 생성은 안전합니다.
/// <b>[Memory:]</b> 연결당 Channel(unbounded inbox) + byte[] 수신 복사본.
/// <b>[Blocking:]</b> Non-blocking. ReadNextAsync는 10초 타임아웃으로 응답 없음을 조기 탐지합니다.
/// <b>[Coordinated omission 주의:]</b> closed-loop은 지연 스파이크를 과소집계합니다(리포트에 명기됨).
/// </remarks>
public sealed class DbPerfClient
{
    private readonly string _host;
    private readonly int    _port;
    private readonly DbPerfOptions _opt;
    private readonly LatencyRecorder _recorder;
    private readonly ClientStats     _stats;

    // BinaryPacketSerializer: 내부 상태 없음(Thread-safe) — 인스턴스당 1개 공유 안전
    private readonly BinaryPacketSerializer _serializer = new();

    public DbPerfClient(
        string host, int port,
        DbPerfOptions opt,
        LatencyRecorder recorder,
        ClientStats stats)
    {
        _host     = host;
        _port     = port;
        _opt      = opt;
        _recorder = recorder;
        _stats    = stats;
    }

    /// <summary>취소 신호가 올 때까지 closed-loop 요청을 반복합니다.</summary>
    /// <param name="isRecording">true를 반환하면 지연을 기록합니다 (warmup 후 플립됨).</param>
    /// <param name="ct">루프 중단 신호입니다.</param>
    public async Task RunAsync(Func<bool> isRecording, CancellationToken ct)
    {
        // IClientConnection: IAsyncDisposable — await using으로 graceful FIN 보장
        await using var conn = ServerNet.CreateClient();

        // Channel<byte[]>: lock-free MPSC — OnReceived(IO 스레드) → 단일 루프 스레드
        // SingleReader/SingleWriter: 런타임이 최적화 경로(lock-free) 선택
        var inbox = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        conn.OnReceived = data =>
        {
            // Span은 콜백 기간만 유효 → ToArray()로 복사 후 enqueue
            inbox.Writer.TryWrite(data.Span.ToArray());
            return ValueTask.CompletedTask;
        };

        try
        {
            await conn.ConnectAsync(_host, _port, ct);
            _stats.IncConnect();

            // Prelude: 초기 로그인 → token 확보 (warmup/measure 전 1회)
            string token = await PreludeLoginAsync(conn, inbox, ct);

            int opCounter = 0;
            while (!ct.IsCancellationRequested)
            {
                bool isRead = _opt.IsReadOp(opCounter++);
                long startTicks = Stopwatch.GetTimestamp();

                if (isRead)
                {
                    // read path: AuthToken(Id=12) → Redis GET → Id=11 응답
                    await conn.SendAsync(new AuthTokenPacket { Token = token });
                    await ReadNextAsync(inbox, ct);
                }
                else
                {
                    // write path: LoginRequest(Id=10) → MySQL SELECT+PBKDF2+Redis SET → Id=11 응답
                    await conn.SendAsync(
                        new LoginRequestPacket { Username = _opt.Username, Password = _opt.Password });
                    var raw  = await ReadNextAsync(inbox, ct);
                    var resp = _serializer.Deserialize<LoginResponsePacket>(raw.AsSpan());
                    // 토큰 갱신: 새 로그인마다 새 토큰 발급 — read path에서 최신 토큰 사용
                    if (resp.Success && resp.Token.Length > 0)
                        token = resp.Token;
                }

                if (isRecording())
                {
                    // Stopwatch.GetTimestamp 차이를 마이크로초로 변환
                    long us = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000L / Stopwatch.Frequency;
                    if (isRead) _recorder.RecordRead(us);
                    else        _recorder.RecordWrite(us);
                }
            }
        }
        catch (OperationCanceledException) { /* 정상 취소 */ }
        catch (Exception ex)
        {
            _stats.IncError();
            Console.Error.WriteLine($"[DbPerfClient] 오류: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<string> PreludeLoginAsync(
        IClientConnection conn, Channel<byte[]> inbox, CancellationToken ct)
    {
        await conn.SendAsync(
            new LoginRequestPacket { Username = _opt.Username, Password = _opt.Password });
        var raw  = await ReadNextAsync(inbox, ct);
        var resp = _serializer.Deserialize<LoginResponsePacket>(raw.AsSpan());

        if (!resp.Success)
            throw new InvalidOperationException(
                $"[DbPerfClient] 초기 로그인 실패 (user={_opt.Username}). " +
                "서버에 EnableLogin=true·SeedTestUser=true인지 확인하세요. " +
                "또는 docker compose up -d 후 재시도하세요.");

        return resp.Token;
    }

    // CancellationTokenSource: 10초 per-response 타임아웃 — 서버 응답 없음 조기 탐지
    private static async ValueTask<byte[]> ReadNextAsync(
        Channel<byte[]> inbox, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return await inbox.Reader.ReadAsync(linked.Token);
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build -c Release --project DbPerfTest 2>&1 | tail -3
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```
git add DbPerfTest/DbPerfClient.cs
git commit -m "추가: DbPerfClient closed-loop write/read 혼합 워크로드 (Channel inbox, 연결당 1 in-flight)"
```

---

## Task 11: Program.cs (orchestrator)

**Files:**
- Modify: `DbPerfTest/Program.cs` (placeholder → full implementation)

- [ ] **Step 1: Write DbPerfTest/Program.cs**

```csharp
using DbPerfTest;
using System.Diagnostics;

// ── CLI 파싱 ─────────────────────────────────────────────────────────────────
var opt = DbPerfOptions.Parse(args);

Console.WriteLine(
    $"[DbPerf] DB 성능 테스트 시작 — clients={opt.Clients}  duration={opt.DurationSeconds}s  " +
    $"warmup={opt.WarmupSeconds}s  ratio={opt.ReadParts}:{opt.WriteParts}  attach={opt.Attach}");
Console.WriteLine("[DbPerf] 종료: Ctrl+C");

// CancellationTokenSource: Ctrl+C를 graceful 종료 신호로 통합
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[DbPerf] Ctrl+C — 종료 중...");
    cts.Cancel();
};

// ── 서버 구동 ─────────────────────────────────────────────────────────────────
ServerProcess? server = null;
if (!opt.Attach)
{
    server = ServerProcess.TryStart(opt.Port, opt.AdminPort, monitorIntervalSec: 1, opt);
    if (server is null)
    {
        Environment.Exit(2);
        return;
    }

    Console.WriteLine("[DbPerf] 서버 준비 대기 중 (첫 [STATS] 수신까지, 최대 15s)...");
    if (!await server.WaitForReadinessAsync(15_000))
    {
        Console.Error.WriteLine("[DbPerf] 서버 준비 타임아웃");
        await server.DisposeAsync();
        Environment.Exit(2);
        return;
    }
    Console.WriteLine("[DbPerf] 서버 준비 완료");
}
else
{
    Console.WriteLine($"[DbPerf] attach 모드 — port={opt.Port}. Crash/SessionLeak 체크 생략.");
}

// 기준 heap: 부하 전 안정 상태 측정 (HeapGrowth Soft 체크 기준값)
long baselineHeap = server?.Latest.HeapBytes ?? 0;

// ── 공유 계측 객체 ────────────────────────────────────────────────────────────
var recorder    = new LatencyRecorder();
var clientStats = new ClientStats();

// recording: volatile bool — warmup 종료 후 true로 플립, 측정 종료 후 false
bool recording = false;

// ── N개 클라이언트 Task 기동 ─────────────────────────────────────────────────
// Task.Run: 각 클라이언트를 독립 ThreadPool 작업으로 기동
var clientTasks = Enumerable.Range(0, opt.Clients)
    .Select(_ => Task.Run(async () =>
    {
        var client = new DbPerfClient("127.0.0.1", opt.Port, opt, recorder, clientStats);
        await client.RunAsync(() => Volatile.Read(ref recording), cts.Token);
    }))
    .ToArray();

// ── Warmup ────────────────────────────────────────────────────────────────────
Console.WriteLine($"[DbPerf] 워밍업 중 ({opt.WarmupSeconds}s) — 이 구간 지연은 기록하지 않습니다...");
// ContinueWith: Task.Delay의 OCE를 삼켜 Ctrl+C 시 warmup 즉시 종료
await Task.Delay(TimeSpan.FromSeconds(opt.WarmupSeconds), cts.Token)
         .ContinueWith(_ => { });

if (cts.Token.IsCancellationRequested)
{
    await Task.WhenAll(clientTasks);
    if (server is not null) await server.DisposeAsync();
    Environment.Exit(2);
    return;
}

// ── 측정 구간 ─────────────────────────────────────────────────────────────────
Console.WriteLine($"[DbPerf] 측정 중 ({opt.DurationSeconds}s)...");
// Stopwatch.GetTimestamp: OS 고해상도 타이머 — wall-clock 측정
long measureStartTicks = Stopwatch.GetTimestamp();
Volatile.Write(ref recording, true);

// 진행 리포터: 5초 주기로 현황 출력
using var progressCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!progressCts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(5_000, progressCts.Token); }
        catch (OperationCanceledException) { break; }
        Console.WriteLine(
            $"[DbPerf] 진행 — errors={clientStats.Errors}  " +
            $"writes={recorder.WriteCount}  reads={recorder.ReadCount}");
    }
});

await Task.Delay(TimeSpan.FromSeconds(opt.DurationSeconds), cts.Token)
         .ContinueWith(_ => { });

progressCts.Cancel();
Volatile.Write(ref recording, false);
double elapsedSec =
    (Stopwatch.GetTimestamp() - measureStartTicks) / (double)Stopwatch.Frequency;

Console.WriteLine($"[DbPerf] 측정 완료 — 경과={elapsedSec:F1}s  " +
                  $"writes={recorder.WriteCount}  reads={recorder.ReadCount}");

// ── 클라이언트 종료 ───────────────────────────────────────────────────────────
cts.Cancel();
await Task.WhenAll(clientTasks);

// ── 서버 안정화 대기 → 최종 스냅샷 ──────────────────────────────────────────
ServerStatsSnapshot? finalSnap = null;
if (server is not null)
{
    Console.WriteLine("[DbPerf] 서버 안정화 대기 중 (sessions=0)...");
    finalSnap = await server.WaitForStabilityAsync(10_000);
}

// ── 판정 리포트 출력 ──────────────────────────────────────────────────────────
var report = DbPerfReport.Evaluate(
    recorder, elapsedSec, clientStats,
    finalSnap, baselineHeap,
    server?.Crashed ?? false, opt.Attach,
    server?.LatestDb, opt);

report.Print();

if (server is not null)
    await server.DisposeAsync();

Environment.Exit(report.OverallPass ? 0 : 1);
```

- [ ] **Step 2: Build**

```
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Error(s)` (전체 솔루션).

- [ ] **Step 3: Run all unit tests**

```
dotnet test 2>&1 | tail -10
```

Expected: all tests pass (DbMetricsTests + LatencyRecorderTests + DbPerfOptionsTests + DbPerfReportTests + existing tests).

- [ ] **Step 4: Commit**

```
git add DbPerfTest/Program.cs
git commit -m "추가: DbPerfTest Program.cs — warmup→측정→판정 오케스트레이터 완성"
```

---

## Task 12: plan doc + CLAUDE.md

**Files:**
- Create: `plan/dbperf_test_0627.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Create plan/dbperf_test_0627.md**

`plan/dbperf_test_0627.md` 파일을 생성하고 spec 문서(`docs/superpowers/specs/2026-06-27-dbperf-test-design.md`)의 주요 내용을 요약:

```markdown
# DB 포함 성능 테스트 하네스 (DbPerfTest)

## 배경 및 목적
...
(spec 요약 1~3페이지)
```

- [ ] **Step 2: Update CLAUDE.md — 하네스 설명 추가**

CLAUDE.md의 plan 문서 목록 테이블에 행 추가:

```
| `plan/dbperf_test_0627.md` | 2026-06-27 | DB 포함 성능 테스트 하네스 (closed-loop login·token-resolve, [DBSTATS] 순수 DB 지연 분리, docker-compose) |
```

- [ ] **Step 3: Commit**

```
git add plan/dbperf_test_0627.md CLAUDE.md
git commit -m "문서: DbPerfTest 설계 문서 및 CLAUDE.md 갱신"
```

---

## Task 13: End-to-end Smoke Test

- [ ] **Step 1: Start infrastructure**

```
docker compose up -d
docker compose ps  # redis + mysql が healthy になるまで待つ (~30s)
```

Expected: both services showing `healthy`.

- [ ] **Step 2: Build Release**

```
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Run harness — basic smoke**

```
dotnet run -c Release --project DbPerfTest -- --clients 5 --duration 10 --warmup-seconds 3
```

Expected output includes:
- `[DbPerf] 서버 준비 완료`
- `[DBSTATS]` 라인이 서버 stdout에 출력됨
- write/read 백분위 출력
- `RESULT PASS ✓`
- Exit code 0

Verify:
```
echo "Exit: $?"
```

- [ ] **Step 4: Read-heavy preset**

```
dotnet run -c Release --project DbPerfTest -- --preset read-heavy --clients 10 --duration 10 --warmup-seconds 3
```

Expected: read rps > write rps significantly.

- [ ] **Step 5: Intentional FAIL — latency threshold**

```
dotnet run -c Release --project DbPerfTest -- --clients 5 --duration 5 --warmup-seconds 2 --target-p99-ms 1
```

Expected: `LatencyTarget ... FAIL`, exit code 1.

- [ ] **Step 6: Run full test suite — regression check**

```
dotnet test -v minimal 2>&1 | tail -10
```

Expected: all existing tests + new tests PASS.

- [ ] **Step 7: Final commit**

```
git add .
git commit -m "추가: DbPerfTest DB 성능 테스트 하네스 — closed-loop MySQL+Redis 계측, docker-compose, 종단간 검증 PASS"
```

---

## Self-Review Checklist

**Spec coverage:**
- ✅ docker-compose (redis + mysql, healthcheck, schema 부트스트랩) → Task 1
- ✅ DbMetrics (MySQL/Redis Stopwatch, Interlocked, [DBSTATS] 출력) → Tasks 2-3
- ✅ LoginService DbMetrics 주입 → Task 3
- ✅ LatencyRecorder (write/read 분리, 백분위) → Task 5
- ✅ DbPerfOptions (CLI, preset, IsReadOp) → Task 6
- ✅ ClientStats (lock-free counters) → Task 7
- ✅ DbPerfReport (Hard checks: Crash/SessionLeak/ErrorRate/Throughput/Latency/NoDbData, Soft: HeapGrowth) → Task 8
- ✅ ServerProcess ([STATS]+[DBSTATS] 파싱, child-process driver) → Task 9
- ✅ DbPerfClient (Channel inbox, closed-loop, warmup 폐기, token 캐싱) → Task 10
- ✅ Program.cs (warmup→measure→report orchestration) → Task 11
- ✅ Warmup 폐기 → Task 11 (Volatile.Write(ref recording, true) 플립)
- ✅ read-dominant 프리셋 → Task 6 (--preset read-heavy)
- ✅ PBKDF2 분리 보고 → DbPerfReport.Print() write/read 별도 + [DBSTATS] DB 지연 출력
- ✅ Coordinated omission caveat 명기 → DbPerfReport.Print() ⚠ 라인
- ✅ DB-ready 게이트 → docker-compose healthcheck + WaitForReadinessAsync(15s)
- ✅ NoDbData vacuous PASS 방지 → DbPerfReport Hard check
- ✅ CLAUDE.md + plan doc → Task 12

**Type consistency:**
- `PercentileResult` — LatencyRecorder.cs에서 정의, DbPerfReport.cs에서 소비 ✅
- `DbStatsSnapshot` — ServerProcess.cs에서 정의, DbPerfReport.cs에서 소비 ✅
- `ServerStatsSnapshot` — ServerProcess.cs에서 정의, DbPerfReport.cs에서 소비 ✅
- `DbMetrics` — Auth/DbMetrics.cs에서 정의, LoginService.cs + Server/Program.cs에서 소비 ✅

**주석 규칙:**
- Channel<byte[]>, Stopwatch, Interlocked, List<long>, Process 모두 내부동작 근거 인라인 주석 포함 ✅
- public 클래스/메서드 XML 문서 (Thread Safety / Memory / Blocking) 포함 ✅
