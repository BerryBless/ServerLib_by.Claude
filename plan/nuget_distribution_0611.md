# ServerLib NuGet 배포 (소스 비공개)

작성일: 2026-06-11

## 1. 배경 및 목적

`ServerLib`(고성능 .NET 10 비동기 소켓 서버 라이브러리)를 **다른 프로젝트에서 소스(.cs)를 받지 않고** 사용할 수 있게 한다.

- 기존: `Interface/`(인터페이스 6종)와 `Core/`(구현체, 전부 `public sealed`)가 단일 어셈블리 `ServerLib.dll`에 포함. 소비자는 `ProjectReference`로 소스째 참조.
- 목표: 컴파일된 바이너리(+IntelliSense용 XML 주석)만 담은 NuGet 패키지로 배포. 소스 비공개.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 배포 형태 | **NuGet 패키지(.nupkg)** | DLL 직접 참조 | 버전 관리·의존성(ObjectPool) 자동 해결, 소비자는 `PackageReference` 한 줄 |
| 캡슐화 수준 | **소스만 숨김(현행 코드 유지)** | 인터페이스만 노출(Core internal + 팩토리 + Abstractions 분리) | 코드 구조 변경 없는 최소 작업. 구현 클래스는 그대로 `public`(소비자가 `new`로 직접 생성) |
| 문서 | **`GenerateDocumentationFile=true`로 XML 동봉** | 미동봉 | XML이 없으면 IntelliSense에 상세 주석이 사라짐. `dotnet pack`이 출력 폴더의 `.xml`을 자동 포함 |

> 소스는 전달되지 않지만 **구현 클래스 타입·시그니처와 XML 주석은 노출**된다(사용자 의도와 일치).

## 3. 컴포넌트 구조

```
ClaudeCodeStudy/
├─ ServerLib/ServerLib.csproj   (수정: 패키징 메타데이터 + 문서 생성)
├─ nupkgs/                       (산출물: ServerLib.1.0.0.nupkg)  ← 로컬 피드
├─ pack.ps1                      (편의 패킹 스크립트)
└─ plan/nuget_distribution_0611.md
```

배포 대상은 `ServerLib` 단독. `AppConfig`(ServerConfig/ClientConfig)는 예제 앱 전용 설정이며 `ServerLib`가 참조하지 않으므로 패킹 제외.

## 4. 핵심 사용 패턴 (소비 프로젝트)

`nuget.config`(로컬 폴더 피드 등록):
```xml
<configuration>
  <packageSources>
    <add key="local-serverlib" value="C:\path\to\ClaudeCodeStudy\nupkgs" />
  </packageSources>
</configuration>
```

소비 프로젝트:
```xml
<PackageReference Include="ServerLib" Version="1.0.0" />
```
```csharp
using ServerLib.Core.Transport;   // 구현 클래스 직접 사용 (소스 .cs 없이 DLL만 참조)
using ServerLib.Interface;        // ISession 등 인터페이스

var listener = new SocketPipelineListener();
listener.MaxConnections = 1000;
listener.OnReceived = (session, data) => ValueTask.CompletedTask;
listener.Start(7777);
```

## 5. 변경 파일 목록

| 파일 | 구분 | 내용 |
|------|------|------|
| `ServerLib/ServerLib.csproj` | 수정 | PackageId/Version/Authors/Description/PackageTags, `GenerateDocumentationFile=true`, `NoWarn=CS1591` 추가 |
| `ServerLib/Interface/IServerListener.cs` | 수정 | `Start` 문서 블록이 `MaxConnections` 위로 오배치된 버그 교정(CS1572) — 문서 생성 활성화로 표면화 |
| `ServerLib/Interface/IClientConnection.cs` | 수정 | `DisposeAsync` cref → `System.IAsyncDisposable.DisposeAsync` 한정(CS1574) |
| `ServerLib/Interface/ISession.cs` | 수정 | 동일 cref 한정(CS1574) |
| `pack.ps1` | 신규 | `dotnet pack` 편의 스크립트 |
| `plan/nuget_distribution_0611.md` | 신규 | 본 문서 |

## 6. 빌드 검증

```powershell
# 1) 빌드 — ServerLib.dll + ServerLib.xml 생성 (경고 0)
dotnet build ServerLib/ServerLib.csproj -c Release

# 2) 패킹 — nupkgs/ServerLib.1.0.0.nupkg 생성
pwsh ./pack.ps1
#   또는: dotnet pack ServerLib/ServerLib.csproj -c Release -o nupkgs

# 3) 패키지 내용 확인 — lib/net10.0/{ServerLib.dll, ServerLib.xml},
#    nuspec 의존성에 Microsoft.Extensions.ObjectPool 9.0.5 기록

# 4) 소비 테스트 — 임시 콘솔에서 PackageReference + new SocketPipelineListener() 빌드 성공
```

**검증 결과(2026-06-11):** 빌드 경고 0 / 패키지에 DLL+XML 포함 / ObjectPool 의존성 자동 기록 / 소비 프로젝트 빌드 성공 / NuGet 캐시에 `ServerLib.xml`(IntelliSense) 확인. ✅

## 7. 향후 확장 포인트

- **인터페이스만 노출 강화:** `Core`를 `internal`로, `ServerLib.Abstractions`(인터페이스 전용 어셈블리) 분리, public 팩토리(`ServerFactory.CreateListener()`) 추가.
- **디버깅 지원:** `<IncludeSymbols>true</IncludeSymbols>` + `SymbolPackageFormat=snupkg`로 step-into 지원.
- **공개 배포:** nuget.org 푸시 시 `PackageLicenseExpression`·`RepositoryUrl`·`PackageReadmeFile` 추가.
