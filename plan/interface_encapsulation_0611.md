# 인터페이스 전용 노출 — Transport 구현체 internal화 + ServerNet 팩토리

작성일: 2026-06-11

## 1. 배경 및 목적

[nuget_distribution_0611.md](nuget_distribution_0611.md)로 소스 비공개 NuGet 배포를 구성했으나, 당시 캡슐화 수준은 "소스만 숨김"이라 소비자에게 `SocketPipelineListener` 등 **구현 클래스가 그대로 노출**되었다.

이번 작업의 목적: 소비자가 **인터페이스 + 팩토리만** 보게 하여 구현 세부사항을 완전히 은닉한다. 구현 변경이 소비자 코드에 파급되지 않도록 한다.

> 단, "Core 전체 internal"은 불가능하다 — `IPacket`·`IPacketSerializer`(인터페이스)·패킷 타입·`PacketPool`·`BinaryPacketSerializer`는 소비자가 패킷을 **정의·직렬화·파싱**할 때 필요한 공개 빌딩블록이다. 따라서 **Transport 구현체와 SessionRegistry만** 은닉한다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 은닉 범위 | **Transport 구현체 + SessionRegistry만 internal** | Core 전체 internal | 직렬화/패킷/풀은 소비자 필수 빌딩블록. 전체 internal 시 라이브러리 사용 불가 |
| 생성 진입점 | **public 정적 팩토리 `ServerNet`** | DI 컨테이너 등록 / 구현체 public 유지 | 의존성 없이 `new` 한 줄을 팩토리 한 줄로 대체. 반환 타입은 인터페이스 |
| 어셈블리 분리 | **단일 `ServerLib.dll` 유지** | `ServerLib.Abstractions` 분리 | 인터페이스 public + 구현 internal + 팩토리 public이면 단일 어셈블리로 충분(YAGNI) |
| 구현체 전용 멤버 | **인터페이스에 추가** | 인터페이스에서 제외 | `SessionSendTimeout`·`ActiveSessionCount`·`SendTimeout`을 인터페이스에 올려 팩토리 경로로도 동일 기능 사용 |

## 3. 변경 내용

### 3.1 인터페이스 보완 (구현체와 동일 시그니처 + XML 주석)
- `IServerListener`: `TimeSpan? SessionSendTimeout { get; set; }`, `int ActiveSessionCount { get; }`
- `IClientConnection`: `TimeSpan? SendTimeout { get; set; }`

### 3.2 구현체 은닉 (`public sealed` → `internal sealed`)
- `SocketPipelineListener`, `SocketPipelineClient`, `SocketPipelineSession`, `SessionRegistry`

### 3.3 공개 팩토리 신규 — `ServerLib/ServerNet.cs` (`namespace ServerLib`)
```csharp
public static IServerListener   CreateListener(ISessionRegistry? registry = null);
public static IClientConnection CreateClient();
public static ISessionRegistry  CreateSessionRegistry();
```
`CreateListener`는 내부에서 `registry as ISessionRegistrar`로 등록 인터페이스를 전달한다(레지스트리는 `CreateSessionRegistry()` 산출물이어야 함). `SessionRegistry`가 `ISessionRegistry`·`ISessionRegistrar`를 동시 구현하므로 캐스팅이 성립한다.

### 3.4 예제 갱신 (CLAUDE.md: Program.cs = 사용 예제)
- `Server/Program.cs`: `new SessionRegistry()`→`ServerNet.CreateSessionRegistry()`, `new SocketPipelineListener(registry)`→`ServerNet.CreateListener(registry)`. 변수 타입을 `ISessionRegistry?`·`IServerListener`로
- `Client/Program.cs`: `new SocketPipelineClient()`→`ServerNet.CreateClient()`
- `ServerMetrics`·`PacketPool`·패킷 타입·`BinaryPacketSerializer`는 public이므로 그대로 사용

### 3.5 배포·문서
- `ServerLib.csproj` 버전 `1.0.0`→`1.1.0`(API 변경)
- `README.md`: "라이브러리 사용법 — 다른 프로젝트에서 (NuGet)" 섹션 추가, 멤버 표에 신규 멤버·캡슐화 노트 반영

## 4. 변경 파일 목록

| 파일 | 구분 | 내용 |
|------|------|------|
| `ServerLib/Interface/IServerListener.cs` | 수정 | `SessionSendTimeout`·`ActiveSessionCount` 추가(XML 주석) |
| `ServerLib/Interface/IClientConnection.cs` | 수정 | `SendTimeout` 추가(XML 주석) |
| `ServerLib/Core/Transport/SocketPipeline{Listener,Client,Session}.cs` | 수정 | `internal sealed`로 전환 |
| `ServerLib/Core/SessionRegistry.cs` | 수정 | `internal sealed`로 전환 |
| `ServerLib/ServerNet.cs` | 신규 | 공개 팩토리 |
| `Server/Program.cs`, `Client/Program.cs` | 수정 | 팩토리+인터페이스 사용 |
| `ServerLib/ServerLib.csproj` | 수정 | 버전 1.1.0 |
| `README.md` | 수정 | 사용법 섹션·멤버 표 갱신 |

## 5. 빌드 검증

```powershell
# 1) 솔루션 전체 빌드 (Server/Client 예제가 팩토리로 컴파일되는지)
dotnet build ClaudeCodeStudy.sln -c Release          # 경고 0 / 오류 0

# 2) 재패킹
pwsh ./pack.ps1                                       # → nupkgs/ServerLib.1.1.0.nupkg

# 3) 소비 테스트 (임시 프로젝트, 로컬 피드)
#  (A) ServerNet.CreateListener()/CreateClient() + 신규 멤버 → 빌드 성공
#  (B) new SocketPipelineListener() → CS0122(보호 수준) 컴파일 에러
```

**검증 결과(2026-06-11):** 솔루션 빌드 경고 0/오류 0. 소비 테스트 (A) 성공 / (B) CS0122 차단 확인. ✅

## 6. 향후 확장 포인트
- **직렬화기도 팩토리화:** `BinaryPacketSerializer`를 internal로, `ServerNet.CreateSerializer()` 추가(단 패킷 struct는 boxing 때문에 public 유지).
- **Abstractions 어셈블리 분리:** 컴파일 타임 의존을 인터페이스 전용 경량 어셈블리로 줄이고 싶을 때.
