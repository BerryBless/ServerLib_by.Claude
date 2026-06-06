# StabilityTest 버스트 안정성 하네스 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 임의 시점의 연결 폭주·트래픽 스파이크 하에서 라이브러리(`SocketPipelineListener`/`SocketPipelineSession`)가 행·크래시·메모리누수·데이터유실 중 어느 것도 일으키지 않음을 자동 검증하고 PASS/FAIL + 종료 코드를 반환하는 콘솔 하네스를 만든다.

**Architecture:** 새 `StabilityTest` 콘솔 프로젝트가 출하 예제 `Server.exe`를 **자식 프로세스**로 띄우고, 시드 고정 RNG로 무작위 폭주를 loopback으로 가한다. 서버는 토글 독립 `[STATS]` 라인(received/test/sessions/heapBytes/gen2)을 출력해 하네스가 머신 파싱한다. 신뢰 클라이언트(`SocketPipelineClient`, graceful FIN)는 카운트 송신으로 데이터유실·손상을 검증하고, 카오스 클라이언트(raw `Socket`, RST, 0바이트)는 연결 폭주·세션 정리를 자극한다. 종료 시 4개 체크를 평가한다.

**Tech Stack:** .NET 10, C#, System.Net.Sockets, System.Diagnostics.Process, System.IO.Pipelines(클라이언트), xUnit, Microsoft.Extensions.Configuration.CommandLine.

**스펙:** `docs/superpowers/specs/2026-06-06-stability-burst-harness-design.md`

---

## 파일 구조

```
StabilityTest/                       (신규 Exe)
├─ StabilityTest.csproj              ServerLib 참조
├─ Program.cs                        오케스트레이터: 수명주기·판정·종료 코드
├─ StabilityConfig.cs                파라미터 + args 파서
├─ StatsSnapshot.cs                  readonly record struct + StatsLineParser
├─ BurstEvent.cs                     BurstEventType, BurstEvent, BurstScheduler
├─ StabilityEvidence.cs             측정 증거 + CheckResult + StabilityReport
├─ ServerProcess.cs                  child 실행·[STATS] 파싱·관찰·graceful Stop
├─ ReliableClient.cs                 SocketPipelineClient 신뢰 클라이언트
├─ ChaosClient.cs                    raw Socket 카오스 클라이언트
└─ StabilityMonitor.cs               라이브 콘솔 모니터

StabilityTest.Tests/                 (신규 xUnit)
├─ StabilityTest.Tests.csproj        StabilityTest 참조
├─ StatsLineParserTests.cs
├─ BurstSchedulerTests.cs
└─ StabilityReportTests.cs

Server/Program.cs                    (수정) AddCommandLine + _totalReceived + [STATS]
Server/Server.csproj                 (수정) CommandLine 패키지
ServerLib/Core/Transport/SocketPipelineListener.cs  (수정) ActiveSessionCount
ClaudeCodeStudy.sln                  (수정) 두 프로젝트 등록
```

타입 계약(전 태스크 공유):
- `readonly record struct StatsSnapshot(long Received, long Test, int Sessions, long HeapBytes, int Gen2)`
- `static bool StatsLineParser.TryParse(string line, out StatsSnapshot snapshot)`
- `enum BurstEventType { ConnectionStorm, TrafficSpike }`
- `readonly record struct BurstEvent(int TimeOffsetMs, BurstEventType Type, int Magnitude)`
- `sealed class BurstScheduler(StabilityConfig config)` → `IReadOnlyList<BurstEvent> BuildTimeline()`
- `sealed class StabilityConfig` (프로퍼티 + `static StabilityConfig Parse(string[] args)`)
- `enum CheckSeverity { Hard, Soft }`
- `readonly record struct CheckResult(string Name, bool Passed, CheckSeverity Severity, string Detail)`
- `sealed class StabilityEvidence` (측정값 모음)
- `static (IReadOnlyList<CheckResult> Results, bool OverallPass) StabilityReport.Evaluate(StabilityEvidence e)`

---

### Task 1: 라이브러리 — `ActiveSessionCount` 노출

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineListener.cs`
- Test: `ServerLib.Tests/ActiveSessionCountTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

Create `ServerLib.Tests/ActiveSessionCountTests.cs`:

```csharp
using System.Net.Sockets;
using ServerLib.Core.Transport;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

public class ActiveSessionCountTests
{
    [Fact]
    public void ActiveSessionCount_reflects_injected_sessions()
    {
        var listener = new SocketPipelineListener();
        Assert.Equal(0, listener.ActiveSessionCount);

        var s1 = new StubSession();
        var s2 = new StubSession();
        listener.InjectSessionForTest(s1);
        listener.InjectSessionForTest(s2);

        Assert.Equal(2, listener.ActiveSessionCount);
    }
}
```

(주의: `StubSession`이 매번 고유 `SessionId`를 갖는지 확인. 아래 Step 2에서 컴파일/실행으로 검증.)

- [ ] **Step 2: 실패 확인**

Run: `dotnet test ServerLib.Tests/ServerLib.Tests.csproj -c Debug --filter "FullyQualifiedName~ActiveSessionCountTests"`
Expected: FAIL — `SocketPipelineListener`에 `ActiveSessionCount` 멤버 없음(컴파일 에러). 만약 `StubSession.SessionId`가 항상 동일하면 count가 1 → 그 경우 테스트의 두 스텁이 다른 ID를 갖도록 `StubSession` 생성자를 확인(이미 `Guid.NewGuid()`면 통과 가능).

- [ ] **Step 3: 구현 추가**

`SocketPipelineListener.cs`의 `public bool IsRunning => _listenSocket != null;` 바로 아래에 추가:

```csharp
    /// <summary>현재 활성 세션 수입니다. 세션 레지스트리·메트릭 토글과 무관하게 항상 사용 가능합니다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.Count"/> 경유 — 주기적 통계(초 단위)용이라 비용 무시 가능.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. 정수 프로퍼티 읽기.</description></item>
    /// </list>
    /// </remarks>
    // ConcurrentDictionary.Count: 누수 검증의 결정적 신호원 — 폭주(FIN+RST) 후 0 복귀가 정리 경로 완결을 증명한다.
    public int ActiveSessionCount => _activeSessions.Count;
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test ServerLib.Tests/ServerLib.Tests.csproj -c Debug --filter "FullyQualifiedName~ActiveSessionCountTests"`
Expected: PASS (1 passed).

- [ ] **Step 5: 회귀 + 커밋**

Run: `dotnet test ServerLib.Tests/ServerLib.Tests.csproj -c Debug`
Expected: 기존 전부 + 신규 1 통과.

```bash
git add ServerLib/Core/Transport/SocketPipelineListener.cs ServerLib.Tests/ActiveSessionCountTests.cs
git commit -m "추가: SocketPipelineListener.ActiveSessionCount(토글 독립 세션 수)"
```

---

### Task 2: 서버 — 커맨드라인 설정 + 권위 카운터 + [STATS] 라인

**Files:**
- Modify: `Server/Server.csproj`
- Modify: `Server/Program.cs`

- [ ] **Step 1: CommandLine 패키지 추가**

`Server/Server.csproj`의 기존 `Microsoft.Extensions.Configuration.*` `<PackageReference>` 묶음에 한 줄 추가:

```xml
        <PackageReference Include="Microsoft.Extensions.Configuration.CommandLine" Version="9.0.5" />
```

- [ ] **Step 2: 설정 빌더에 AddCommandLine**

`Server/Program.cs`의 ConfigurationBuilder를 수정:

```csharp
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    // AddCommandLine: appsettings.json 위에 args 오버라이드 계층 → 하네스가 포트·주기를 인자로 제어.
    // 예: Server.exe --Server:Port=9100 --Server:MonitorIntervalSeconds=1
    .AddCommandLine(args)
    .Build();
```

- [ ] **Step 3: always-on 권위 카운터 선언 + 증가**

`var test = 0;` 다음 줄에 추가:

```csharp
// 권위 수신 카운트: EnableMetrics 토글과 무관하게 항상 증가 — 하네스의 데이터유실 검증 기준값.
long totalReceived = 0;
```

`listener.OnReceived` 콜백 안, `metrics?.OnPacketReceived();` 다음 줄에 추가:

```csharp
    Interlocked.Increment(ref totalReceived);
```

- [ ] **Step 4: 모니터 루프에 [STATS] 라인 추가**

`_ = Task.Run(async () => { ... })` 모니터 루프 안, 기존 `Console.WriteLine($"[Monitor] ...")` 바로 다음에 추가:

```csharp
        // [STATS]: 하네스가 머신 파싱하는 권위 신호(ASCII·고정 key=value). 토글 독립 소스만 사용.
        Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} " +
                          $"test={Volatile.Read(ref test)} " +
                          $"sessions={listener.ActiveSessionCount} " +     // 토글 독립
                          $"heapBytes={GC.GetTotalMemory(false)} " +        // 서버측 관리 힙(누수 보조 신호)
                          $"gen2={GC.CollectionCount(2)}");
```

- [ ] **Step 5: 종료 시 최종 [STATS] 라인 추가**

파일 끝 `Console.WriteLine($"종료 ...")` 다음 줄에 추가(`record GameContext` 선언 위):

```csharp
Console.WriteLine($"[STATS] received={Volatile.Read(ref totalReceived)} test={test} " +
                  $"sessions={listener.ActiveSessionCount} heapBytes={GC.GetTotalMemory(false)} gen2={GC.CollectionCount(2)}");
```

- [ ] **Step 6: 빌드 + 스모크 검증**

Run: `dotnet build Server/Server.csproj -c Release`
Expected: 빌드 성공.

스모크(서버를 2초 실행 → [STATS] 출력 확인, PowerShell):

```powershell
$p = Start-Process -FilePath "Server\bin\Release\net10.0\Server.exe" -ArgumentList "--Server:Port=9101","--Server:MonitorIntervalSeconds=1" -RedirectStandardOutput "stats_smoke.txt" -RedirectStandardInput "NUL" -PassThru -NoNewWindow
Start-Sleep -Seconds 3
Stop-Process -Id $p.Id -Force
Get-Content stats_smoke.txt | Select-String "\[STATS\]" | Select-Object -First 1
Remove-Item stats_smoke.txt
```

Expected: `[STATS] received=0 test=0 sessions=0 heapBytes=... gen2=...` 형태의 라인 1개 이상.

- [ ] **Step 7: 커밋**

```bash
git add Server/Server.csproj Server/Program.cs
git commit -m "추가: 서버 커맨드라인 설정 오버라이드 + [STATS] 권위 라인(하네스 검증용)"
```

---

### Task 3: StabilityTest 프로젝트 스캐폴드 + Config + record 타입

**Files:**
- Create: `StabilityTest/StabilityTest.csproj`
- Create: `StabilityTest/StabilityConfig.cs`
- Create: `StabilityTest/StatsSnapshot.cs`
- Create: `StabilityTest/BurstEvent.cs`
- Create: `StabilityTest/StabilityEvidence.cs`
- Create: `StabilityTest/Program.cs` (임시 스텁)

- [ ] **Step 1: csproj 생성**

`StabilityTest/StabilityTest.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: StabilityConfig 생성**

`StabilityTest/StabilityConfig.cs`:

```csharp
namespace StabilityTest;

/// <summary>버스트 안정성 테스트 실행 파라미터. 모두 args로 오버라이드 가능합니다.</summary>
public sealed class StabilityConfig
{
    public int Seed { get; set; } = 12345;            // 시드 RNG — 실패 폭주 재현용
    public int Port { get; set; } = 9100;             // 전용 테스트 포트(개발 서버 9000과 분리)
    public int BurstSeconds { get; set; } = 90;        // 폭주 구간 길이
    public int SettleSeconds { get; set; } = 20;       // drain/settle 구간 길이
    public int MaxReliableClients { get; set; } = 200; // 동시 신뢰 클라이언트 상한
    public int StormMinClients { get; set; } = 50;     // 연결 폭주 최소 클라이언트 수
    public int StormMaxClients { get; set; } = 500;    // 연결 폭주 최대 클라이언트 수
    public int SpikeMinPackets { get; set; } = 500;    // 트래픽 스파이크 최소 패킷 수
    public int SpikeMaxPackets { get; set; } = 5000;   // 트래픽 스파이크 최대 패킷 수
    public int GapMinMs { get; set; } = 200;           // 폭주 이벤트 간 최소 간격
    public int GapMaxMs { get; set; } = 2000;          // 폭주 이벤트 간 최대 간격
    public int CountStableSamples { get; set; } = 3;   // received 안정 판정 연속 표본 수
    public int HangFrozenSamples { get; set; } = 5;    // 부하 중 received 정지 행 판정 연속 표본 수
    public double HeapTolerance { get; set; } = 2.0;   // settle 후 heap ≤ baseline×tol (소프트)
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>`--key value` 형태 인자를 파싱합니다. 알 수 없는 키는 무시합니다.</summary>
    public static StabilityConfig Parse(string[] args)
    {
        var c = new StabilityConfig();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            var key = args[i].TrimStart('-').ToLowerInvariant();
            var val = args[i + 1];
            switch (key)
            {
                case "seed": c.Seed = int.Parse(val); break;
                case "port": c.Port = int.Parse(val); break;
                case "burst": c.BurstSeconds = int.Parse(val); break;
                case "settle": c.SettleSeconds = int.Parse(val); break;
                case "maxclients": c.MaxReliableClients = int.Parse(val); break;
                case "host": c.Host = val; break;
            }
        }
        return c;
    }
}
```

- [ ] **Step 3: StatsSnapshot 생성(파서는 Task 5에서 TDD)**

`StabilityTest/StatsSnapshot.cs`:

```csharp
namespace StabilityTest;

/// <summary>서버 [STATS] 라인 1개의 스냅샷. 모든 값은 누적/순간값입니다.</summary>
public readonly record struct StatsSnapshot(long Received, long Test, int Sessions, long HeapBytes, int Gen2);
```

- [ ] **Step 4: BurstEvent 타입 생성(스케줄러는 Task 6에서 TDD)**

`StabilityTest/BurstEvent.cs`:

```csharp
namespace StabilityTest;

public enum BurstEventType { ConnectionStorm, TrafficSpike }

/// <summary>폭주 타임라인의 단일 이벤트. <paramref name="TimeOffsetMs"/>는 폭주 구간 시작 기준 오프셋입니다.</summary>
public readonly record struct BurstEvent(int TimeOffsetMs, BurstEventType Type, int Magnitude);
```

- [ ] **Step 5: StabilityEvidence 타입 생성(평가기는 Task 7에서 TDD)**

`StabilityTest/StabilityEvidence.cs`:

```csharp
namespace StabilityTest;

/// <summary>폭주 실행 종료 후 수집한 측정 증거. <see cref="StabilityReport.Evaluate"/>의 입력입니다.</summary>
public sealed class StabilityEvidence
{
    public bool Crashed { get; set; }          // 실행 중 child가 예기치 않게 종료됨
    public int ExitCode { get; set; }          // graceful 종료 코드
    public bool HangDetected { get; set; }     // 부하 활성 구간에 received 정지
    public long ReceivedFinal { get; set; }    // count-stable 권위 수신값
    public long SentTotal { get; set; }        // Σ 신뢰 클라 송신(inc+dec)
    public long TestFinal { get; set; }        // 서버 test 순증감
    public long SentInc { get; set; }          // Σ 신뢰 클라 increment 송신
    public long SentDec { get; set; }          // Σ 신뢰 클라 decrement 송신
    public int SessionsFinal { get; set; }     // settle 후 활성 세션 수
    public long HeapBaseline { get; set; }     // 워밍업 시 heapBytes
    public long HeapFinal { get; set; }        // settle 후 heapBytes
    public double HeapTolerance { get; set; }  // 허용 배수
}
```

- [ ] **Step 6: 임시 Program 스텁(빌드 가능하게)**

`StabilityTest/Program.cs`:

```csharp
// 임시 스텁 — Task 12에서 오케스트레이터로 교체.
Console.WriteLine("StabilityTest scaffold");
return 0;
```

- [ ] **Step 7: 빌드 + 커밋**

Run: `dotnet build StabilityTest/StabilityTest.csproj -c Debug`
Expected: 빌드 성공.

```bash
git add StabilityTest/
git commit -m "추가: StabilityTest 프로젝트 스캐폴드(Config·record 타입·스텁)"
```

---

### Task 4: StabilityTest.Tests 프로젝트 스캐폴드

**Files:**
- Create: `StabilityTest.Tests/StabilityTest.Tests.csproj`

- [ ] **Step 1: 테스트 csproj 생성**

`StabilityTest.Tests/StabilityTest.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>StabilityTest.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\StabilityTest\StabilityTest.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 빌드 + 커밋**

Run: `dotnet build StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug`
Expected: 빌드 성공(테스트 0개).

```bash
git add StabilityTest.Tests/
git commit -m "추가: StabilityTest.Tests xUnit 프로젝트 스캐폴드"
```

---

### Task 5: StatsLineParser (TDD)

**Files:**
- Modify: `StabilityTest/StatsSnapshot.cs`
- Test: `StabilityTest.Tests/StatsLineParserTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`StabilityTest.Tests/StatsLineParserTests.cs`:

```csharp
using StabilityTest;
using Xunit;

namespace StabilityTest.Tests;

public class StatsLineParserTests
{
    [Fact]
    public void TryParse_valid_line_extracts_all_fields()
    {
        var line = "[STATS] received=12345 test=-7 sessions=42 heapBytes=987654 gen2=3";
        Assert.True(StatsLineParser.TryParse(line, out var s));
        Assert.Equal(12345, s.Received);
        Assert.Equal(-7, s.Test);
        Assert.Equal(42, s.Sessions);
        Assert.Equal(987654, s.HeapBytes);
        Assert.Equal(3, s.Gen2);
    }

    [Fact]
    public void TryParse_line_with_leading_noise_still_parses()
    {
        // 콘솔에 다른 텍스트가 같은 줄에 섞여 들어오는 경우 방어: [STATS] 토큰부터 파싱
        var line = "garbage [STATS] received=1 test=2 sessions=3 heapBytes=4 gen2=5";
        Assert.True(StatsLineParser.TryParse(line, out var s));
        Assert.Equal(1, s.Received);
        Assert.Equal(5, s.Gen2);
    }

    [Theory]
    [InlineData("[Monitor] sessions=0 packets/1s=0")]   // 다른 라인
    [InlineData("[STATS] received=1 test=2")]            // 키 누락
    [InlineData("[STATS] received=abc test=2 sessions=3 heapBytes=4 gen2=5")] // 파싱 불가
    [InlineData("")]
    public void TryParse_invalid_line_returns_false(string line)
    {
        Assert.False(StatsLineParser.TryParse(line, out _));
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug --filter "FullyQualifiedName~StatsLineParserTests"`
Expected: FAIL — `StatsLineParser` 타입 없음(컴파일 에러).

- [ ] **Step 3: 파서 구현**

`StabilityTest/StatsSnapshot.cs`에 추가(record struct 아래):

```csharp
/// <summary>서버 stdout의 <c>[STATS]</c> 라인을 <see cref="StatsSnapshot"/>으로 파싱합니다.</summary>
public static class StatsLineParser
{
    private const string Marker = "[STATS]";

    /// <summary><paramref name="line"/>에서 [STATS] 토큰 이후 5개 키를 모두 파싱하면 true.</summary>
    public static bool TryParse(string line, out StatsSnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrEmpty(line)) return false;
        int idx = line.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0) return false;

        // [STATS] 이후를 공백 단위로 분할해 key=value 수집
        var tokens = line[(idx + Marker.Length)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        long received = 0, test = 0, heap = 0;
        int sessions = 0, gen2 = 0;
        bool hasR = false, hasT = false, hasS = false, hasH = false, hasG = false;

        foreach (var tok in tokens)
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            var key = tok[..eq];
            var val = tok[(eq + 1)..];
            switch (key)
            {
                case "received": hasR = long.TryParse(val, out received); break;
                case "test": hasT = long.TryParse(val, out test); break;
                case "sessions": hasS = int.TryParse(val, out sessions); break;
                case "heapBytes": hasH = long.TryParse(val, out heap); break;
                case "gen2": hasG = int.TryParse(val, out gen2); break;
            }
        }

        if (!(hasR && hasT && hasS && hasH && hasG)) return false;
        snapshot = new StatsSnapshot(received, test, sessions, heap, gen2);
        return true;
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug --filter "FullyQualifiedName~StatsLineParserTests"`
Expected: PASS (6 passed).

- [ ] **Step 5: 커밋**

```bash
git add StabilityTest/StatsSnapshot.cs StabilityTest.Tests/StatsLineParserTests.cs
git commit -m "추가: StatsLineParser([STATS] 라인 머신 파싱)"
```

---

### Task 6: BurstScheduler (TDD — 시드 결정성)

**Files:**
- Modify: `StabilityTest/BurstEvent.cs`
- Test: `StabilityTest.Tests/BurstSchedulerTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`StabilityTest.Tests/BurstSchedulerTests.cs`:

```csharp
using StabilityTest;
using Xunit;

namespace StabilityTest.Tests;

public class BurstSchedulerTests
{
    private static StabilityConfig Cfg(int seed) => new()
    {
        Seed = seed, BurstSeconds = 30,
        StormMinClients = 50, StormMaxClients = 500,
        SpikeMinPackets = 500, SpikeMaxPackets = 5000,
        GapMinMs = 200, GapMaxMs = 2000,
    };

    [Fact]
    public void Same_seed_produces_identical_timeline()
    {
        var a = new BurstScheduler(Cfg(42)).BuildTimeline();
        var b = new BurstScheduler(Cfg(42)).BuildTimeline();
        Assert.Equal(a, b); // record struct 값 동등 + 순서
    }

    [Fact]
    public void Different_seed_produces_different_timeline()
    {
        var a = new BurstScheduler(Cfg(1)).BuildTimeline();
        var b = new BurstScheduler(Cfg(2)).BuildTimeline();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Timeline_is_nonempty_sorted_and_within_window()
    {
        var t = new BurstScheduler(Cfg(7)).BuildTimeline();
        Assert.NotEmpty(t);
        for (int i = 1; i < t.Count; i++)
            Assert.True(t[i].TimeOffsetMs >= t[i - 1].TimeOffsetMs, "오프셋은 비감소여야 함");
        Assert.All(t, e => Assert.True(e.TimeOffsetMs < 30_000, "오프셋은 폭주 구간 내"));
    }

    [Fact]
    public void Magnitudes_respect_configured_ranges()
    {
        var t = new BurstScheduler(Cfg(9)).BuildTimeline();
        Assert.All(t, e =>
        {
            if (e.Type == BurstEventType.ConnectionStorm)
                Assert.InRange(e.Magnitude, 50, 500);
            else
                Assert.InRange(e.Magnitude, 500, 5000);
        });
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug --filter "FullyQualifiedName~BurstSchedulerTests"`
Expected: FAIL — `BurstScheduler` 타입 없음.

- [ ] **Step 3: 스케줄러 구현**

`StabilityTest/BurstEvent.cs`에 추가:

```csharp
/// <summary>시드 고정 RNG로 무작위 폭주 타임라인을 결정적으로 생성합니다.</summary>
/// <remarks>동일 시드 → 동일 타임라인. 실패한 폭주를 재현하기 위한 핵심 장치입니다.</remarks>
public sealed class BurstScheduler
{
    private readonly StabilityConfig _config;

    public BurstScheduler(StabilityConfig config) => _config = config;

    /// <summary>폭주 구간(0..BurstSeconds*1000ms)에 걸친 이벤트 목록을 비감소 오프셋 순으로 반환합니다.</summary>
    public IReadOnlyList<BurstEvent> BuildTimeline()
    {
        // new Random(seed): 결정적 의사난수열 — 같은 시드는 같은 수열 → 타임라인 재현 가능
        var rng = new Random(_config.Seed);
        var events = new List<BurstEvent>();
        int windowMs = _config.BurstSeconds * 1000;
        int t = 0;

        while (true)
        {
            t += rng.Next(_config.GapMinMs, _config.GapMaxMs + 1);
            if (t >= windowMs) break;

            // 50/50로 이벤트 종류 선택, 종류별 범위에서 크기 선택
            var type = rng.Next(2) == 0 ? BurstEventType.ConnectionStorm : BurstEventType.TrafficSpike;
            int magnitude = type == BurstEventType.ConnectionStorm
                ? rng.Next(_config.StormMinClients, _config.StormMaxClients + 1)
                : rng.Next(_config.SpikeMinPackets, _config.SpikeMaxPackets + 1);

            events.Add(new BurstEvent(t, type, magnitude));
        }

        return events;
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug --filter "FullyQualifiedName~BurstSchedulerTests"`
Expected: PASS (4 passed).

- [ ] **Step 5: 커밋**

```bash
git add StabilityTest/BurstEvent.cs StabilityTest.Tests/BurstSchedulerTests.cs
git commit -m "추가: BurstScheduler(시드 결정적 무작위 폭주 타임라인)"
```

---

### Task 7: StabilityReport 판정 로직 (TDD)

**Files:**
- Modify: `StabilityTest/StabilityEvidence.cs`
- Test: `StabilityTest.Tests/StabilityReportTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`StabilityTest.Tests/StabilityReportTests.cs`:

```csharp
using StabilityTest;
using Xunit;

namespace StabilityTest.Tests;

public class StabilityReportTests
{
    private static StabilityEvidence Healthy() => new()
    {
        Crashed = false, ExitCode = 0, HangDetected = false,
        ReceivedFinal = 10_000, SentTotal = 10_000,
        TestFinal = 200, SentInc = 600, SentDec = 400,   // 600-400=200
        SessionsFinal = 0,
        HeapBaseline = 10_000_000, HeapFinal = 12_000_000, HeapTolerance = 2.0,
    };

    [Fact]
    public void Healthy_run_passes_all_hard_checks()
    {
        var (results, pass) = StabilityReport.Evaluate(Healthy());
        Assert.True(pass);
        Assert.All(results.Where(r => r.Severity == CheckSeverity.Hard), r => Assert.True(r.Passed));
    }

    [Fact]
    public void Crash_fails_overall()
    {
        var e = Healthy(); e.Crashed = true;
        var (_, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
    }

    [Fact]
    public void Data_loss_fails_overall()
    {
        var e = Healthy(); e.ReceivedFinal = 9_999; // 1개 유실
        var (results, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
        Assert.Contains(results, r => r.Name == "DataLoss" && !r.Passed);
    }

    [Fact]
    public void Corruption_fails_overall()
    {
        var e = Healthy(); e.TestFinal = 199; // inc-dec 불일치
        var (_, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
    }

    [Fact]
    public void Leaked_sessions_fail_overall()
    {
        var e = Healthy(); e.SessionsFinal = 3; // 정리 안 됨
        var (_, pass) = StabilityReport.Evaluate(e);
        Assert.False(pass);
    }

    [Fact]
    public void Heap_over_tolerance_is_soft_and_does_not_fail_overall()
    {
        var e = Healthy(); e.HeapFinal = 100_000_000; // baseline×2 초과
        var (results, pass) = StabilityReport.Evaluate(e);
        Assert.True(pass); // 소프트 — 전체 FAIL 아님
        Assert.Contains(results, r => r.Name == "LeakHeap" && r.Severity == CheckSeverity.Soft && !r.Passed);
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug --filter "FullyQualifiedName~StabilityReportTests"`
Expected: FAIL — `StabilityReport`, `CheckResult`, `CheckSeverity` 없음.

- [ ] **Step 3: 평가기 구현**

`StabilityTest/StabilityEvidence.cs`에 추가(StabilityEvidence 클래스 아래):

```csharp
public enum CheckSeverity { Hard, Soft }

/// <summary>단일 체크 결과. <paramref name="Severity"/>가 Hard이고 미통과면 전체 FAIL입니다.</summary>
public readonly record struct CheckResult(string Name, bool Passed, CheckSeverity Severity, string Detail);

/// <summary>측정 증거로부터 4대 실패모드 체크를 평가하여 PASS/FAIL을 산출합니다.</summary>
public static class StabilityReport
{
    public static (IReadOnlyList<CheckResult> Results, bool OverallPass) Evaluate(StabilityEvidence e)
    {
        long expectedTest = e.SentInc - e.SentDec;
        long heapLimit = (long)(e.HeapBaseline * e.HeapTolerance);

        var results = new List<CheckResult>
        {
            new("Crash", !e.Crashed && e.ExitCode == 0, CheckSeverity.Hard,
                e.Crashed ? "실행 중 child 종료" : $"exitCode={e.ExitCode}"),
            new("Hang", !e.HangDetected, CheckSeverity.Hard,
                e.HangDetected ? "부하 중 received 정지" : "부하 구간 내 처리 지속"),
            new("DataLoss", e.ReceivedFinal == e.SentTotal, CheckSeverity.Hard,
                $"received={e.ReceivedFinal} sent={e.SentTotal}"),
            new("Corruption", e.TestFinal == expectedTest, CheckSeverity.Hard,
                $"test={e.TestFinal} expected={expectedTest} (inc={e.SentInc} dec={e.SentDec})"),
            new("LeakSessions", e.SessionsFinal == 0, CheckSeverity.Hard,
                $"sessions={e.SessionsFinal} (기대 0)"),
            new("LeakHeap", e.HeapFinal <= heapLimit, CheckSeverity.Soft,
                $"heapFinal={e.HeapFinal} limit={heapLimit} (baseline={e.HeapBaseline}×{e.HeapTolerance})"),
        };

        bool overallPass = results.Where(r => r.Severity == CheckSeverity.Hard).All(r => r.Passed);
        return (results, overallPass);
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test StabilityTest.Tests/StabilityTest.Tests.csproj -c Debug --filter "FullyQualifiedName~StabilityReportTests"`
Expected: PASS (6 passed).

- [ ] **Step 5: 커밋**

```bash
git add StabilityTest/StabilityEvidence.cs StabilityTest.Tests/StabilityReportTests.cs
git commit -m "추가: StabilityReport(4대 실패모드 판정·하드/소프트 분리)"
```

---

### Task 8: ServerProcess — child 실행·[STATS] 관찰·graceful Stop

**Files:**
- Create: `StabilityTest/ServerProcess.cs`

- [ ] **Step 1: 구현 작성**

`StabilityTest/ServerProcess.cs`:

```csharp
using System.Diagnostics;

namespace StabilityTest;

/// <summary>출하 예제 <c>Server.exe</c>를 자식 프로세스로 실행하고 관찰합니다.</summary>
/// <remarks>
/// <b>[수명주기]</b> 하네스가 child 전체 수명을 소유합니다. <see cref="Dispose"/>는 살아있는 child를 강제 종료해
/// orphan 프로세스를 남기지 않습니다.
/// </remarks>
public sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile StatsSnapshot _latest;
    private volatile bool _hasStats;

    public ServerProcess(StabilityConfig config)
    {
        string exePath = ResolveServerExe(config);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // 하네스가 포트·모니터 주기를 제어 — [STATS]를 1초마다 받아 count-stable/모니터 양쪽에 사용
                Arguments = $"--Server:Port={config.Port} --Server:MonitorIntervalSeconds=1",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _process.OutputDataReceived += OnOutput;
    }

    /// <summary>최신 [STATS] 스냅샷. 아직 1개도 못 받았으면 <paramref name="snapshot"/>=default, false.</summary>
    public bool TryGetLatest(out StatsSnapshot snapshot)
    {
        snapshot = _latest;
        return _hasStats;
    }

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    /// <summary>현재 child의 private 메모리(바이트). 호출 시점 값으로 갱신합니다.</summary>
    public long PrivateMemoryBytes
    {
        get { _process.Refresh(); return _process.HasExited ? 0 : _process.PrivateMemorySize64; }
    }

    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
    }

    /// <summary>서버가 수신 대기 준비(`[Server] port` 라인)될 때까지 대기합니다.</summary>
    public async Task WaitForReadyAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_ready.Task, Task.Delay(timeout));
        if (done != _ready.Task)
            throw new TimeoutException("서버가 제한 시간 내 준비되지 않았습니다.");
    }

    /// <summary>stdin에 "q"를 보내 graceful 종료를 요청하고 종료를 기다립니다. 시한 초과 시 강제 종료.</summary>
    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (_process.HasExited) return;
        try { await _process.StandardInput.WriteLineAsync("q"); await _process.StandardInput.FlushAsync(); }
        catch { /* stdin 닫힘 — 아래에서 강제 종료 */ }

        using var cts = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { _process.Kill(entireProcessTree: true); } catch { } }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        if (!_ready.Task.IsCompleted && e.Data.Contains("[Server] port", StringComparison.Ordinal))
            _ready.TrySetResult();
        if (StatsLineParser.TryParse(e.Data, out var snapshot))
        {
            _latest = snapshot;
            _hasStats = true;
        }
    }

    // 솔루션 폴더를 거슬러 올라가 Server의 빌드 출력 exe 경로를 해석한다.
    private static string ResolveServerExe(StabilityConfig _)
    {
        // 현재 빌드 구성(Debug/Release)을 BaseDirectory 경로에서 추론
        string baseDir = AppContext.BaseDirectory;
        string config = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudeCodeStudy.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("ClaudeCodeStudy.sln을 찾지 못했습니다 — 솔루션 트리 밖에서 실행됨.");

        string exe = Path.Combine(dir.FullName, "Server", "bin", config, "net10.0",
            OperatingSystem.IsWindows() ? "Server.exe" : "Server");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"서버 실행 파일을 찾지 못했습니다: {exe}. 먼저 Server를 {config}로 빌드하세요.");
        return exe;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}
```

- [ ] **Step 2: 빌드 검증**

Run: `dotnet build StabilityTest/StabilityTest.csproj -c Debug`
Expected: 빌드 성공.

- [ ] **Step 3: 커밋**

```bash
git add StabilityTest/ServerProcess.cs
git commit -m "추가: ServerProcess(child 서버 실행·[STATS] 관찰·graceful 종료)"
```

---

### Task 9: ReliableClient — SocketPipelineClient 신뢰 클라이언트

**Files:**
- Create: `StabilityTest/ReliableClient.cs`

- [ ] **Step 1: 구현 작성**

`StabilityTest/ReliableClient.cs`:

```csharp
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

namespace StabilityTest;

/// <summary>
/// 데이터유실·손상 검증용 신뢰 클라이언트. 라이브러리 <see cref="SocketPipelineClient"/>를 사용하며
/// 반드시 graceful(FIN)로 종료하여 보낸 모든 패킷이 서버에 도달함을 보장합니다.
/// </summary>
public sealed class ReliableClient : IAsyncDisposable
{
    // SocketPipelineClient: 라이브러리 클라이언트(C2 dispose 경로 포함)도 SUT로 검증하기 위해 사용
    private readonly SocketPipelineClient _client = new();
    // 본문 없는 4바이트 패킷을 1회 빌드해 재사용 — 송신마다 직렬화/할당 회피
    private readonly byte[] _inc = new byte[PacketPool.HeaderSize];
    private readonly byte[] _dec = new byte[PacketPool.HeaderSize];

    public long SentInc { get; private set; }
    public long SentDec { get; private set; }
    public long SentTotal => SentInc + SentDec;

    public ReliableClient()
    {
        PacketPool.WriteHeader(_inc, IncrementPacket.Id, 0);
        PacketPool.WriteHeader(_dec, DecrementPacket.Id, 0);
    }

    public Task ConnectAsync(string host, int port, CancellationToken ct)
        => _client.ConnectAsync(host, port, ct);

    /// <summary><paramref name="count"/>개 패킷을 연속 송신합니다. 짝수 인덱스=increment, 홀수=decrement.</summary>
    /// <remarks>합쳐진(coalesced) 소형 패킷 스트림으로 서버 파이프라인 프레이밍을 자극합니다.</remarks>
    public async Task SendBurstAsync(int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            if ((i & 1) == 0) { await _client.SendAsync(_inc, ct); SentInc++; }
            else { await _client.SendAsync(_dec, ct); SentDec++; }
        }
    }

    // graceful 종료: DisposeAsync가 _cts 취소 후 소켓 Dispose → 큐된 데이터 전송 후 FIN.
    // 매 SendAsync를 await했으므로 종료 시점에 모든 바이트가 커널 송신 버퍼에 있어 FIN 이전에 도달한다.
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
```

- [ ] **Step 2: 빌드 검증**

Run: `dotnet build StabilityTest/StabilityTest.csproj -c Debug`
Expected: 빌드 성공.

- [ ] **Step 3: 커밋**

```bash
git add StabilityTest/ReliableClient.cs
git commit -m "추가: ReliableClient(SocketPipelineClient·카운트 송신·graceful 종료)"
```

---

### Task 10: ChaosClient — raw Socket 연결 폭주·RST

**Files:**
- Create: `StabilityTest/ChaosClient.cs`

- [ ] **Step 1: 구현 작성**

`StabilityTest/ChaosClient.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace StabilityTest;

/// <summary>
/// 연결 폭주·세션 정리 자극용 카오스 클라이언트. <b>앱 데이터를 0바이트 송신</b>하므로
/// 서버의 권위 received 집계를 오염시키지 않습니다(데이터유실 단언의 결정성 보장).
/// </summary>
public static class ChaosClient
{
    /// <summary>
    /// <paramref name="count"/>개 연결을 거의 동시에 열고, 짧게 유휴 후 RST로 급작 종료합니다.
    /// 서버의 accept 루프와 세션 정리(누수) 경로를 자극합니다.
    /// </summary>
    public static Task StormAsync(string host, int port, int count, CancellationToken ct)
    {
        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
            tasks[i] = OneAsync(host, port, ct);
        return Task.WhenAll(tasks);
    }

    private static async Task OneAsync(string host, int port, CancellationToken ct)
    {
        // raw Socket: LingerOption(true,0)으로 close 시 FIN 대신 RST를 보내 급작 이탈을 정밀 재현
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Parse(host), port, ct);
            // 짧은 유휴 — accept 직후 세션이 등록된 상태에서 RST가 나도록
            await Task.Delay(Random.Shared.Next(5, 50), ct);
            // SO_LINGER=0: 커널이 큐를 버리고 즉시 RST 전송 → 서버는 비정상 종료 경로로 세션을 정리해야 함
            socket.LingerState = new LingerOption(true, 0);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { /* 폭주 중 연결 거부/리셋은 정상 — 서버가 죽지만 않으면 됨 */ }
        finally
        {
            socket.Dispose(); // Linger 0이면 RST, 미설정 경로(예외 시)면 일반 close
        }
    }
}
```

(주: `Random.Shared`는 thread-safe이며 유휴 시간은 검증 대상이 아니라 자극 다양화용이므로 시드 결정성에서 제외해도 무방.)

- [ ] **Step 2: 빌드 검증**

Run: `dotnet build StabilityTest/StabilityTest.csproj -c Debug`
Expected: 빌드 성공.

- [ ] **Step 3: 커밋**

```bash
git add StabilityTest/ChaosClient.cs
git commit -m "추가: ChaosClient(raw Socket 연결 폭주·RST·0바이트 송신)"
```

---

### Task 11: StabilityMonitor — 라이브 콘솔 모니터

**Files:**
- Create: `StabilityTest/StabilityMonitor.cs`

- [ ] **Step 1: 구현 작성**

`StabilityTest/StabilityMonitor.cs`:

```csharp
using System.Diagnostics;

namespace StabilityTest;

/// <summary>2초 주기로 진행 상황(클라이언트·서버 received·세션·heap)을 콘솔에 출력합니다.</summary>
public sealed class StabilityMonitor
{
    private readonly ServerProcess _server;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public StabilityMonitor(ServerProcess server) => _server = server;

    public async Task RunAsync(Func<long> sentTotal, Func<int> activeReliable, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await SafeTickAsync(timer, ct))
        {
            _server.TryGetLatest(out var s);
            Console.WriteLine(
                $"[{_sw.Elapsed:hh\\:mm\\:ss}] " +
                $"clients={activeReliable(),5} | sent={sentTotal(),12:N0} | " +
                $"srvRecv={s.Received,12:N0} | sessions={s.Sessions,6} | " +
                $"heapMB={s.HeapBytes / (1024 * 1024),6:N0} | gen2={s.Gen2,4} | alive={!_server.HasExited}");
        }
    }

    private static async Task<bool> SafeTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
```

- [ ] **Step 2: 빌드 검증**

Run: `dotnet build StabilityTest/StabilityTest.csproj -c Debug`
Expected: 빌드 성공.

- [ ] **Step 3: 커밋**

```bash
git add StabilityTest/StabilityMonitor.cs
git commit -m "추가: StabilityMonitor(라이브 진행 콘솔 출력)"
```

---

### Task 12: Program — 오케스트레이터(수명주기·판정·종료 코드)

**Files:**
- Modify: `StabilityTest/Program.cs` (스텁 → 전체 교체)

- [ ] **Step 1: 오케스트레이터 작성**

`StabilityTest/Program.cs` 전체 교체:

```csharp
using System.Diagnostics;
using StabilityTest;

var config = StabilityConfig.Parse(args);
Console.WriteLine($"=== StabilityTest === seed={config.Seed} port={config.Port} " +
                  $"burst={config.BurstSeconds}s settle={config.SettleSeconds}s maxClients={config.MaxReliableClients}");

using var server = new ServerProcess(config);
server.Start();
await server.WaitForReadyAsync(TimeSpan.FromSeconds(15));
Console.WriteLine("[harness] 서버 준비 완료.");

var evidence = new StabilityEvidence { HeapTolerance = config.HeapTolerance };
var reliable = new List<ReliableClient>();
long SentTotal() => SumSent(reliable);

using var monitorCts = new CancellationTokenSource();
var monitor = new StabilityMonitor(server);
var monitorTask = monitor.RunAsync(SentTotal, () => reliable.Count, monitorCts.Token);

// 1) 워밍업 & baseline ----------------------------------------------------------
for (int i = 0; i < Math.Min(10, config.MaxReliableClients); i++)
{
    var c = new ReliableClient();
    await c.ConnectAsync(config.Host, config.Port, CancellationToken.None);
    reliable.Add(c);
}
await Task.Delay(2000); // [STATS] 몇 개 수신해 baseline 확보
evidence.HeapBaseline = server.TryGetLatest(out var bs) ? bs.HeapBytes : server.PrivateMemoryBytes;
Console.WriteLine($"[harness] baseline heapBytes={evidence.HeapBaseline:N0}");

// 2) 폭주 구간 -----------------------------------------------------------------
var timeline = new BurstScheduler(config).BuildTimeline();
Console.WriteLine($"[harness] 폭주 이벤트 {timeline.Count}개 스케줄됨.");
var burstSw = Stopwatch.StartNew();
long lastReceived = 0;
int frozenSamples = 0;
int hangSampleAccumMs = 0;

foreach (var ev in timeline)
{
    // 다음 이벤트 시각까지 대기 — 그 사이 행/크래시 감시
    while (burstSw.ElapsedMilliseconds < ev.TimeOffsetMs)
    {
        await Task.Delay(200);
        hangSampleAccumMs += 200;
        if (server.HasExited) { evidence.Crashed = true; goto AfterBurst; }
        // 부하 활성 구간(폭주 중) — received가 1초 주기로 전진하는지 감시
        if (hangSampleAccumMs >= 1000)
        {
            hangSampleAccumMs = 0;
            server.TryGetLatest(out var snap);
            if (snap.Received == lastReceived) frozenSamples++;
            else { frozenSamples = 0; lastReceived = snap.Received; }
            if (frozenSamples >= config.HangFrozenSamples && SentTotal() > 0)
            {
                evidence.HangDetected = true;
                Console.WriteLine("[harness] HANG 감지 — 부하 중 received 정지.");
                goto AfterBurst;
            }
        }
    }

    if (server.HasExited) { evidence.Crashed = true; goto AfterBurst; }

    if (ev.Type == BurstEventType.ConnectionStorm)
    {
        // 연결 폭주: 카오스 클라이언트 — 0바이트, fire-and-forget
        _ = ChaosClient.StormAsync(config.Host, config.Port, ev.Magnitude, CancellationToken.None);
    }
    else
    {
        // 트래픽 스파이크: 활성 신뢰 클라이언트(부족하면 새로 연결)에게 burst 송신
        await EnsureReliableAsync(reliable, config, Math.Min(config.MaxReliableClients, 50));
        var spikeTargets = reliable.ToArray();
        _ = Task.WhenAll(spikeTargets.Select(c => SafeSendAsync(c, ev.Magnitude / Math.Max(1, spikeTargets.Length))));
    }
}

AfterBurst:
Console.WriteLine($"[harness] 폭주 구간 종료 (crashed={evidence.Crashed} hang={evidence.HangDetected}).");

// 3) drain & settle: 부하 중단 후 received가 count-stable 될 때까지 ------------------
if (!evidence.Crashed)
{
    long prev = -1; int stable = 0;
    var settleDeadline = Stopwatch.StartNew();
    while (settleDeadline.Elapsed < TimeSpan.FromSeconds(config.SettleSeconds))
    {
        await Task.Delay(1000);
        if (server.HasExited) { evidence.Crashed = true; break; }
        server.TryGetLatest(out var snap);
        if (snap.Received == prev) { if (++stable >= config.CountStableSamples) break; }
        else { stable = 0; prev = snap.Received; }
    }
}

// 신뢰 클라이언트 graceful 종료(FIN) → 모든 송신분이 서버에 도달했음을 보장
foreach (var c in reliable)
{
    try { await c.DisposeAsync(); } catch { }
}
await Task.Delay(2000); // FIN 처리·세션 정리 반영 대기

// 4) 권위 읽기 -----------------------------------------------------------------
if (!evidence.Crashed)
{
    // 세션 정리 완료를 위해 잠시 더 폴링(연결 폭주 RST 정리 포함)
    StatsSnapshot finalSnap = default;
    var pollSw = Stopwatch.StartNew();
    while (pollSw.Elapsed < TimeSpan.FromSeconds(10))
    {
        await Task.Delay(1000);
        if (server.HasExited) { evidence.Crashed = true; break; }
        server.TryGetLatest(out finalSnap);
        if (finalSnap.Sessions == 0) break; // 정리 완료
    }
    evidence.ReceivedFinal = finalSnap.Received;
    evidence.TestFinal = finalSnap.Test;
    evidence.SessionsFinal = finalSnap.Sessions;
    evidence.HeapFinal = finalSnap.HeapBytes;
}

evidence.SentInc = reliable.Sum(c => c.SentInc);
evidence.SentDec = reliable.Sum(c => c.SentDec);
evidence.SentTotal = evidence.SentInc + evidence.SentDec;

// 5) 종료 & 판정 ----------------------------------------------------------------
monitorCts.Cancel();
try { await monitorTask; } catch { }
await server.StopGracefullyAsync(TimeSpan.FromSeconds(10));
evidence.ExitCode = server.HasExited ? SafeExitCode(server) : -1;

var (results, pass) = StabilityReport.Evaluate(evidence);
Console.WriteLine();
Console.WriteLine("================ STABILITY REPORT ================");
Console.WriteLine($" seed={config.Seed}  (실패 시 동일 seed로 재현)");
foreach (var r in results)
    Console.WriteLine($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name,-13} ({r.Severity}) — {r.Detail}");
Console.WriteLine("==================================================");
Console.WriteLine(pass ? "RESULT: PASS ✅" : "RESULT: FAIL ❌");
return pass ? 0 : 1;

// ---- 로컬 헬퍼 ----
static long SumSent(List<ReliableClient> clients) => clients.Sum(c => c.SentTotal);

static int SafeExitCode(ServerProcess s) { try { return s.ExitCode; } catch { return -1; } }

static async Task SafeSendAsync(ReliableClient c, int count)
{
    try { await c.SendBurstAsync(count, CancellationToken.None); }
    catch { /* 개별 클라 송신 실패가 전체를 중단시키지 않음 */ }
}

static async Task EnsureReliableAsync(List<ReliableClient> clients, StabilityConfig cfg, int target)
{
    while (clients.Count < target && clients.Count < cfg.MaxReliableClients)
    {
        var c = new ReliableClient();
        try { await c.ConnectAsync(cfg.Host, cfg.Port, CancellationToken.None); clients.Add(c); }
        catch { await c.DisposeAsync(); break; }
    }
}
```

- [ ] **Step 2: 빌드 검증**

Run: `dotnet build StabilityTest/StabilityTest.csproj -c Debug`
Expected: 빌드 성공.

- [ ] **Step 3: 커밋**

```bash
git add StabilityTest/Program.cs
git commit -m "추가: StabilityTest 오케스트레이터(수명주기·행/크래시 감시·count-stable·판정)"
```

---

### Task 13: 솔루션 등록 + 엔드투엔드 인수 실행

**Files:**
- Modify: `ClaudeCodeStudy.sln`

- [ ] **Step 1: 솔루션에 두 프로젝트 등록**

Run:
```bash
dotnet sln ClaudeCodeStudy.sln add StabilityTest/StabilityTest.csproj
dotnet sln ClaudeCodeStudy.sln add StabilityTest.Tests/StabilityTest.Tests.csproj
```
Expected: 두 프로젝트가 솔루션에 추가됨.

- [ ] **Step 2: 전체 빌드 + 전체 테스트(회귀)**

Run:
```bash
dotnet build ClaudeCodeStudy.sln -c Release
dotnet test ClaudeCodeStudy.sln -c Release
```
Expected: 빌드 성공; 기존 테스트 전부 + 신규(ActiveSessionCount 1, StatsLineParser 6, BurstScheduler 4, StabilityReport 6) 통과.

- [ ] **Step 3: 짧은 엔드투엔드 인수 실행(PASS 확인)**

먼저 Server를 Release로 빌드했는지 확인(ServerProcess가 Release 출력 exe를 찾음). 짧은 구간으로 스모크:

Run:
```bash
dotnet run -c Release --project StabilityTest -- --seed 12345 --port 9100 --burst 15 --settle 8 --maxclients 50
```
Expected:
- `서버 준비 완료` 출력
- 2초 주기 라이브 모니터 라인(`clients=… srvRecv=… sessions=… heapMB=…`)
- 마지막에 STABILITY REPORT 표 + `RESULT: PASS ✅`
- 종료 코드 0 — PowerShell에서 `echo $LASTEXITCODE` → 0

만약 `DataLoss FAIL`(received < sent)이 나오면: settle/count-stable 대기가 짧아 서버가 미처 다 처리하지 못한 것 → `--settle` 값을 늘려 재확인. `LeakSessions FAIL`(sessions>0)이면: 연결 폭주 RST 정리 지연 → Task 12 Step 1의 권위 읽기 폴링 시간(10초)을 늘려 재확인. 이는 임계값 튜닝이며 라이브러리 결함과 구분할 것.

- [ ] **Step 4: 최종 커밋**

```bash
git add ClaudeCodeStudy.sln
git commit -m "추가: StabilityTest·Tests 솔루션 등록(버스트 안정성 하네스 완성)"
```

---

## 실행 후 검증 체크리스트

- [ ] `dotnet test ClaudeCodeStudy.sln -c Release` — 전부 통과
- [ ] `dotnet run -c Release --project StabilityTest -- --burst 15 --settle 8` — `RESULT: PASS`, 종료 코드 0
- [ ] 동일 seed 재실행 시 동일 폭주 타임라인(이벤트 수 동일) 출력 확인
- [ ] 실행 후 `Server.exe` orphan 프로세스 없음(작업 관리자/`Get-Process Server`)

## 향후 확장 포인트(스펙 참조)

- 송신 경로(P3/`SessionSendTimeout`/`BroadcastAsync`) 검증: 에코/브로드캐스트 서버 변형 + 느린 리더 클라이언트. 본 하네스 골격 재사용.
- CI 야간 잡: 종료 코드 기반 게이트, 실패 시 seed 아티팩트 보존.
- 자원 한계 자극(포트 고갈·backlog 초과) 극한 모드.
```