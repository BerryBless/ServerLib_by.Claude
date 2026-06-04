# JSON 설정 기반 기능 토글 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 하트비트·유휴 타임아웃·세션 레지스트리·메트릭 등 기능을 `appsettings.json`의 토글로 켜고 끄고, 핵심 파라미터(포트·호스트·주기)를 외부에서 조정한다.

**Architecture:** ServerLib(라이브러리)은 무변경. Server/Client 실행 프로젝트가 `Microsoft.Extensions.Configuration`으로 `appsettings.json`을 POCO에 바인딩하고, `Features.EnableXxx` 토글에 따라 기존 nullable 프로퍼티(`PingInterval`/`IdleTimeout`)와 컴포넌트(`SessionRegistry`/`ServerMetrics`) 생성을 조건부 적용한다. POCO 기본값으로 설정 파일이 없어도 기존 동작을 유지한다.

**Tech Stack:** .NET 10, Microsoft.Extensions.Configuration(.Json/.Binder) 9.0.5, System.Text.Json(내장)

---

## File Map

| 경로 | 유형 | 역할 |
|------|------|------|
| `Server/ServerConfig.cs` | 신규 | POCO: Port, IdleTimeoutSeconds, MonitorIntervalSeconds, Features |
| `Server/appsettings.json` | 신규 | 서버 설정 + Features 토글 |
| `Server/Server.csproj` | 수정 | Configuration 패키지 3종 + appsettings 복사 |
| `Server/Program.cs` | 수정 | 설정 로드 + 토글 적용 (전체 교체) |
| `Client/ClientConfig.cs` | 신규 | POCO: Host, Port, BatchSize, ..., Features |
| `Client/appsettings.json` | 신규 | 클라이언트 설정 + Features 토글 |
| `Client/Client.csproj` | 수정 | Configuration 패키지 3종 + appsettings 복사 |
| `Client/Program.cs` | 수정 | 설정 로드 + 토글 적용 (타겟 편집) |

> ServerLib·ServerLib.Tests 변경 없음. 앱 레벨 설정이므로 단위 테스트 대신 빌드 성공 + 토글 동작 수동 검증으로 확인.

---

## Task 1: Server 설정 토글

**Files:**
- Modify: `Server/Server.csproj`
- Create: `Server/ServerConfig.cs`
- Create: `Server/appsettings.json`
- Modify: `Server/Program.cs`

- [ ] **Step 1: Server.csproj에 Configuration 패키지 + appsettings 복사 추가**

`Server/Server.csproj` 전체를 다음으로 교체:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.Configuration" Version="9.0.5" />
        <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.0.5" />
        <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="9.0.5" />
    </ItemGroup>

    <ItemGroup>
        <None Update="appsettings.json">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>

</Project>
```

- [ ] **Step 2: ServerConfig.cs 작성**

`Server/ServerConfig.cs` 신규 생성 (전역 네임스페이스 — top-level Program.cs와 동일 컴파일 단위):

```csharp
/// <summary>서버 실행 설정. appsettings.json의 "Server" 섹션에 바인딩됩니다.</summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 9000;
    public int MonitorIntervalSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 30;
    public ServerFeatures Features { get; set; } = new();
}

/// <summary>서버 기능 on/off 토글.</summary>
public sealed class ServerFeatures
{
    public bool EnableSessionRegistry { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableIdleTimeout { get; set; } = true;
}
```

- [ ] **Step 3: appsettings.json 작성**

`Server/appsettings.json` 신규 생성:

```json
{
  "Server": {
    "Port": 9000,
    "MonitorIntervalSeconds": 10,
    "IdleTimeoutSeconds": 30,
    "Features": {
      "EnableSessionRegistry": true,
      "EnableMetrics": true,
      "EnableIdleTimeout": true
    }
  }
}
```

- [ ] **Step 4: Program.cs 전체 교체**

`Server/Program.cs` 전체를 다음으로 교체 (registry/metrics null-safe, 토글 적용, 주기 외부화):

```csharp
using Microsoft.Extensions.Configuration;
using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;
using ServerLib.Interface;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();
var cfg = config.GetSection("Server").Get<ServerConfig>() ?? new ServerConfig();

// 토글: 레지스트리/메트릭은 비활성 시 생성 자체를 생략(null)
var registry = cfg.Features.EnableSessionRegistry ? new SessionRegistry() : null;
var metrics = cfg.Features.EnableMetrics ? new ServerMetrics() : null;
var listener = new SocketPipelineListener(registry);

var test = 0;
long windowPackets = 0;
using var cts = new CancellationTokenSource();

listener.OnClientConnected = session =>
{
    session.Context = new GameContext(PlayerId: 1001, Nickname: "홍길동");
    metrics?.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  state={session.State}  (sessions: {metrics?.ConnectedCount ?? 0})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics?.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (sessions: {metrics?.ConnectedCount ?? 0})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
        return ValueTask.CompletedTask;

    metrics?.OnPacketReceived();
    Interlocked.Increment(ref windowPackets);

    if (packetId == IncrementPacket.Id)
        Interlocked.Increment(ref test);
    else if (packetId == DecrementPacket.Id)
        Interlocked.Decrement(ref test);

    return ValueTask.CompletedTask;
};

// 토글: 유휴 타임아웃은 활성 시에만 설정(미설정 시 ServerLib가 스윕 루프를 시작하지 않음)
if (cfg.Features.EnableIdleTimeout)
{
    listener.IdleTimeout = TimeSpan.FromSeconds(cfg.IdleTimeoutSeconds);
    listener.OnIdleTimeout = session =>
    {
        Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
        return ValueTask.CompletedTask;
    };
}

listener.Start(cfg.Port);
Console.WriteLine($"[Server] port {cfg.Port} — 증가(Id={IncrementPacket.Id}) / 감소(Id={DecrementPacket.Id}).");
Console.WriteLine($"  Features: registry={cfg.Features.EnableSessionRegistry} metrics={cfg.Features.EnableMetrics} idleTimeout={cfg.Features.EnableIdleTimeout}");
Console.WriteLine($"  Enter: 현재 세션 목록 출력 | 'q'+Enter: 서버 종료");

_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.MonitorIntervalSeconds), cts.Token); }
        catch (OperationCanceledException) { break; }

        long count = Interlocked.Exchange(ref windowPackets, 0);
        Console.WriteLine($"[Monitor] sessions={metrics?.ConnectedCount ?? 0}  packets/{cfg.MonitorIntervalSeconds}s={count:N0}  test={Volatile.Read(ref test)}  registry={registry?.Count ?? 0}");
    }
});

while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) break;

    if (registry is null)
    {
        Console.WriteLine("[Sessions] 세션 레지스트리 비활성화됨 (EnableSessionRegistry=false)");
        continue;
    }
    var sessions = registry.GetAll();
    Console.WriteLine($"[Sessions] count={sessions.Count}");
    foreach (var s in sessions)
        Console.WriteLine($"  {s.SessionId:N}  {s.RemoteEndPoint}  connected={s.ConnectedAt:HH:mm:ss}");
}

cts.Cancel();
listener.Stop();
Console.WriteLine($"종료  total={metrics?.TotalPacketsReceived ?? 0}  final test={test}");

// 세션에 부착할 커스텀 컨텍스트 예제
record GameContext(int PlayerId = 0, string Nickname = "Guest");
```

- [ ] **Step 5: 빌드 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\Server
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: 커밋**

```bash
git add Server/Server.csproj Server/ServerConfig.cs Server/appsettings.json Server/Program.cs
git commit -m "추가: Server appsettings.json 기능 토글 (레지스트리·메트릭·유휴 타임아웃 on/off)"
```

---

## Task 2: Client 설정 토글

**Files:**
- Modify: `Client/Client.csproj`
- Create: `Client/ClientConfig.cs`
- Create: `Client/appsettings.json`
- Modify: `Client/Program.cs`

- [ ] **Step 1: Client.csproj 교체**

`Client/Client.csproj` 전체를 다음으로 교체:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="9.0.5" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.0.5" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="9.0.5" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: ClientConfig.cs 작성**

`Client/ClientConfig.cs` 신규 생성:

```csharp
/// <summary>클라이언트 실행 설정. appsettings.json의 "Client" 섹션에 바인딩됩니다.</summary>
public sealed class ClientConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public int BatchSize { get; set; } = 1000;
    public int DefaultThreadCount { get; set; } = 4;
    public int PingIntervalSeconds { get; set; } = 1;
    public int RttDisplayIntervalSeconds { get; set; } = 2;
    public ClientFeatures Features { get; set; } = new();
}

/// <summary>클라이언트 기능 on/off 토글.</summary>
public sealed class ClientFeatures
{
    public bool EnableHeartbeat { get; set; } = true;
    public bool EnableRttDisplay { get; set; } = true;
}
```

- [ ] **Step 3: appsettings.json 작성**

`Client/appsettings.json` 신규 생성:

```json
{
  "Client": {
    "Host": "127.0.0.1",
    "Port": 9000,
    "BatchSize": 1000,
    "DefaultThreadCount": 4,
    "PingIntervalSeconds": 1,
    "RttDisplayIntervalSeconds": 2,
    "Features": {
      "EnableHeartbeat": true,
      "EnableRttDisplay": true
    }
  }
}
```

- [ ] **Step 4: Program.cs — 설정 로드로 const 블록 교체**

먼저 `Client/Program.cs`를 읽는다. 파일 최상단 `using` 블록에 다음을 추가(기존 using 아래):

```csharp
using Microsoft.Extensions.Configuration;
```

그리고 다음 const 블록:

```csharp
const string Host = "127.0.0.1";
const int Port = 9000;
const int BatchSize = 1000;  // 회당 전송 수 (진행 출력 단위)
```

을 다음으로 교체:

```csharp
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();
var cfg = config.GetSection("Client").Get<ClientConfig>() ?? new ClientConfig();

string Host = cfg.Host;
int Port = cfg.Port;
int BatchSize = cfg.BatchSize;  // 회당 전송 수 (진행 출력 단위)
```

- [ ] **Step 5: threadCount 기본값을 설정값으로**

다음 줄:

```csharp
int threadCount = args.Length > 0 ? int.Parse(args[0]) : 4;
```

을 다음으로 교체:

```csharp
int threadCount = args.Length > 0 ? int.Parse(args[0]) : cfg.DefaultThreadCount;
```

- [ ] **Step 6: 하트비트 토글 적용**

다음 줄:

```csharp
    conn.PingInterval = TimeSpan.FromSeconds(1); // 1초마다 자동 PING → RTT 측정
```

을 다음으로 교체:

```csharp
    if (cfg.Features.EnableHeartbeat)
        conn.PingInterval = TimeSpan.FromSeconds(cfg.PingIntervalSeconds); // 자동 PING → RTT 측정
```

- [ ] **Step 7: RTT 출력 토글 적용**

다음 블록:

```csharp
    if (i == 0)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
                Console.WriteLine($"  [T0] RTT={conn.Rtt.TotalMilliseconds:F1}ms");
            }
        });
    }
```

을 다음으로 교체:

```csharp
    if (i == 0 && cfg.Features.EnableRttDisplay)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(cfg.RttDisplayIntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
                Console.WriteLine($"  [T0] RTT={conn.Rtt.TotalMilliseconds:F1}ms");
            }
        });
    }
```

- [ ] **Step 8: 빌드 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\Client
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 9: 커밋**

```bash
git add Client/Client.csproj Client/ClientConfig.cs Client/appsettings.json Client/Program.cs
git commit -m "추가: Client appsettings.json 기능 토글 (하트비트·RTT 출력 on/off)"
```

---

## Task 3: 최종 검증

- [ ] **Step 1: 전체 솔루션 빌드**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 2: 전체 테스트 (ServerLib 무변경 → 36 통과 유지)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 36`

- [ ] **Step 3: appsettings.json 출력 복사 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\Server
```
빌드 후 `Server/bin/Debug/net10.0/appsettings.json`이 존재하는지 확인:
```bash
ls E:\project\ClaudeCodeStudy\Server\bin\Debug\net10.0\appsettings.json
```
Expected: 파일 존재 (CopyToOutputDirectory 동작 확인).

- [ ] **Step 4: 수동 토글 동작 확인 (선택)**

`Client/appsettings.json`에서 `"EnableHeartbeat": false`로 변경 →
터미널1: `dotnet run --project Server`
터미널2: `dotnet run --project Client -- 1 50`
→ 클라이언트에 `[T0] RTT=...` 출력이 없고, 서버에 PONG 트래픽이 없는지 확인.
다시 `true`로 되돌린다.
