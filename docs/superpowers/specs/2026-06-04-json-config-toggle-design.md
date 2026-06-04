# JSON 설정 기반 기능 토글 설계 문서

**날짜:** 2026-06-04
**상태:** 승인됨

---

## 1. 배경 및 목적

하트비트(PingInterval), 유휴 타임아웃(IdleTimeout), 세션 레지스트리, 포트·호스트 등 모든 동작이 `Server/Program.cs`·`Client/Program.cs`에 하드코딩되어 있고 설정 인프라(JSON 파일·Configuration 패키지)가 전무하다.

**목표:** 하트비트 같은 기능을 **JSON 설정 파일로 켜고 끄고**, 핵심 파라미터를 외부에서 조정한다.

**핵심 원칙:** ServerLib(라이브러리)은 설정 의존성 없이 순수 유지. 설정 로딩·바인딩은 **애플리케이션(Server/Client) 관심사**다. `Microsoft.Extensions.Configuration`으로 `appsettings.json`을 POCO에 바인딩하고, 토글에 따라 기존 nullable 프로퍼티(`PingInterval`/`IdleTimeout`)와 컴포넌트 생성을 조건부 적용한다.

---

## 2. 설계 결정

| 항목 | 채택안 | 비고 |
|------|--------|------|
| 설정 로더 | Microsoft.Extensions.Configuration(+Json+Binder) | 표준 패턴, 환경변수·CLI 오버라이드 확장 가능 |
| 설정 파일 | `Server/appsettings.json`, `Client/appsettings.json` | CopyToOutputDirectory: PreserveNewest |
| 파일 부재 | `optional: true` → 내장 기본값으로 동작 | 설정 없어도 기존 동작 유지 |
| POCO 위치 | Server/Client 프로젝트 내부 | ServerLib 순수 유지 |
| 토글 방식 | `Features.EnableXxx` bool + 파라미터 분리 | off면 해당 프로퍼티/컴포넌트 미적용 |

---

## 3. JSON 스키마

`Server/appsettings.json`:
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

`Client/appsettings.json`:
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

---

## 4. 컴포넌트 구조

```
Server/
├── appsettings.json          ← NEW (CopyToOutputDirectory: PreserveNewest)
├── ServerConfig.cs           ← NEW (POCO)
├── Server.csproj             ← MOD
└── Program.cs                ← MOD

Client/
├── appsettings.json          ← NEW
├── ClientConfig.cs           ← NEW (POCO)
├── Client.csproj             ← MOD
└── Program.cs                ← MOD

ServerLib/                    ← 변경 없음 (순수 유지)
```

## 5. 설정 POCO

```csharp
// Server/ServerConfig.cs
public sealed class ServerConfig
{
    public int Port { get; set; } = 9000;
    public int MonitorIntervalSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 30;
    public ServerFeatures Features { get; set; } = new();
}
public sealed class ServerFeatures
{
    public bool EnableSessionRegistry { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableIdleTimeout { get; set; } = true;
}

// Client/ClientConfig.cs
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
public sealed class ClientFeatures
{
    public bool EnableHeartbeat { get; set; } = true;
    public bool EnableRttDisplay { get; set; } = true;
}
```
> POCO 기본값 = 설정 파일/키 부재 시 기존 동작 유지(안전망).

## 6. Program.cs 적용 패턴

Server:
```csharp
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();
var cfg = config.GetSection("Server").Get<ServerConfig>() ?? new ServerConfig();

var registry = cfg.Features.EnableSessionRegistry ? new SessionRegistry() : null;
var listener = new SocketPipelineListener(registry);
if (cfg.Features.EnableIdleTimeout)
{
    listener.IdleTimeout = TimeSpan.FromSeconds(cfg.IdleTimeoutSeconds);
    listener.OnIdleTimeout = session => { ...; return ValueTask.CompletedTask; };
}
listener.Start(cfg.Port);
// 모니터링 주기 = cfg.MonitorIntervalSeconds, registry는 null-safe 사용
```

Client:
```csharp
if (cfg.Features.EnableHeartbeat)
    conn.PingInterval = TimeSpan.FromSeconds(cfg.PingIntervalSeconds);
// RTT 출력은 cfg.Features.EnableRttDisplay + cfg.RttDisplayIntervalSeconds로 토글
```

---

## 7. 변경 파일 목록

| 파일 | 유형 | 내용 |
|------|------|------|
| `Server/appsettings.json` | 신규 | 서버 설정 + Features |
| `Server/ServerConfig.cs` | 신규 | POCO + 기본값 |
| `Server/Server.csproj` | 수정 | Configuration 패키지 + appsettings 복사 |
| `Server/Program.cs` | 수정 | 설정 로드 + 토글 적용 |
| `Client/appsettings.json` | 신규 | 클라이언트 설정 + Features |
| `Client/ClientConfig.cs` | 신규 | POCO + 기본값 |
| `Client/Client.csproj` | 수정 | Configuration 패키지 + appsettings 복사 |
| `Client/Program.cs` | 수정 | 설정 로드 + 토글 적용 |

> ServerLib 및 ServerLib.Tests 변경 없음. CLI 인자(threadCount/sendCount)는 미지정 시 설정값을 기본으로 사용하도록 통합.

---

## 8. 빌드 검증

```bash
dotnet build ClaudeCodeStudy.sln
dotnet test ServerLib.Tests --logger "console;verbosity=normal"
```
- 라이브러리 무변경 → 36/36 테스트 통과 유지
- 수동: `EnableHeartbeat=false` → Client에서 PING 미송신·RTT 미출력 / `EnableIdleTimeout=false` → 서버 유휴 스윕 미동작 / 설정 파일 삭제 → 기본값 동작

---

## 9. 향후 확장 포인트

- 소켓 옵션(NoDelay/KeepAlive/Backlog) 외부화 (현재 ServerLib 하드코딩 유지)
- 환경변수·CLI 오버라이드 레이어(`AddEnvironmentVariables`/`AddCommandLine`)
- `reloadOnChange:true` + IOptionsMonitor 런타임 핫리로드
