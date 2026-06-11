# ClaudeCodeStudy

ProudNet급 **고성능 서버 라이브러리(.NET 10)**와, 그 코드 품질을 자동으로 지키는 **Claude Code 멀티에이전트 하네스 시스템**을 함께 담은 학습/실험 프로젝트입니다.

- **라이브러리** — System.IO.Pipelines 기반 Zero-copy TCP 서버/클라이언트, 바이너리 패킷 직렬화, RPC 디스패치, 세션 레지스트리.
- **설계 원칙** — Interface는 순수 추상화만, Core는 구현만. 의존성 방향은 **Core → Interface** (역방향 금지).
- **하네스** — 코드 리뷰·동시성·GC·파이프라인·TDD·Git 자동화를 21개 전문 에이전트가 팀으로 감사/구현하는 자동화 파이프라인.

## 목차

1. [인터페이스 명세](#1-인터페이스-명세-interface-specifications)
   - [라이브러리 사용법 — 다른 프로젝트에서 (NuGet)](#라이브러리-사용법--다른-프로젝트에서-nuget)
2. [하네스 & 플러그인](#2-하네스--플러그인)
3. [에이전트 활용](#3-에이전트-활용-agent-orchestration)

---

## 1. 인터페이스 명세 (Interface Specifications)

ServerLib이 노출하는 공개 계약 전체입니다. 모든 인터페이스는 고성능 서버 라이브러리 설계 원칙(Zero-allocation hot path, `ValueTask` 반환, Thread-safety 명시)을 계약으로 강제합니다.

> 네임스페이스: `ServerLib.Interface` (단, 직렬화 계약인 `IPacket`·`IPacketSerializer`은 `ServerLib.Core.Serialization` — Interface 레이어가 Core를 역참조하지 않도록 직렬화 서브시스템에 함께 둔다)

### 프로젝트 레이어 구조

```
ClaudeCodeStudy.sln
├── ServerLib/
│   ├── Interface/        # 순수 추상화 — 아래 7개 인터페이스
│   ├── ServerNet.cs      # 공개 팩토리 — CreateListener/CreateClient/CreateSessionRegistry
│   └── Core/             # 구현 (의존성: Core → Interface)
│       ├── Transport/    # SocketPipelineListener / ~Client / ~Session  (internal)
│       ├── Serialization/# BinaryPacketSerializer, SpanReader/Writer, IPacket  (public)
│       ├── Rpc/          # RpcDispatcher
│       └── Memory/       # PacketPool (ArrayPool 래퍼)
├── Server/               # ServerNet.CreateListener() 사용 예제 (Program.cs)
├── Client/               # ServerNet.CreateClient() 사용 예제 (다중 스레드 부하)
├── AppConfig/            # ServerConfig / ClientConfig (JSON 설정 모델)
└── Rpc.Generator/        # [RpcService] → 디스패처 Source Generator
```

| 인터페이스 | 구현체 (`ServerLib.Core`) |
|-----------|--------------------------|
| `IServerListener` | `SocketPipelineListener` |
| `ISession` | `SocketPipelineSession` |
| `IClientConnection` | `SocketPipelineClient` |
| `ISessionRegistry` / `ISessionRegistrar` | `SessionRegistry` (`ConcurrentDictionary` 기반) |
| `IRpcHandler` | `RpcDispatcher` (배열 인덱싱 O(1) 라우팅) |
| `IPacketSerializer` | `BinaryPacketSerializer` |

> **캡슐화(v1.1.0~):** Transport 구현체(`SocketPipelineListener`/`~Client`/`~Session`)와 `SessionRegistry`는 `internal`로 은닉된다. 외부 소비자는 **`ServerNet` 팩토리**가 반환하는 인터페이스로만 이들을 사용한다(아래 [라이브러리 사용법](#라이브러리-사용법--다른-프로젝트에서-nuget) 참고). 직렬화 빌딩블록(`IPacket`·`IPacketSerializer`·`BinaryPacketSerializer`·패킷 타입·`PacketPool`)은 그대로 `public`.

---

### 라이브러리 사용법 — 다른 프로젝트에서 (NuGet)

ServerLib은 **구현체를 숨기고 인터페이스 + 팩토리만** 노출한다. 다른 프로젝트는 소스(`.cs`) 없이 NuGet 패키지(DLL + XML 주석)만 참조하며, `SocketPipeline*` 구현체는 `internal`이라 보이지 않는다.

**1) 패키지 만들기 (배포 측)**
```powershell
pwsh ./pack.ps1            # → nupkgs/ServerLib.1.1.0.nupkg (DLL + XML 문서 주석 동봉)
```

**2) 패키지 참조 (소비 측)** — `nuget.config`에 로컬 폴더 피드를 등록하고:
```xml
<configuration>
  <packageSources>
    <add key="local-serverlib" value="C:\path\to\ClaudeCodeStudy\nupkgs" />
  </packageSources>
</configuration>
```
프로젝트 `.csproj`에 한 줄:
```xml
<PackageReference Include="ServerLib" Version="1.1.0" />
```

**3) 서버 — `IServerListener`**
```csharp
using ServerLib;             // ServerNet 팩토리
using ServerLib.Interface;   // IServerListener / ISession / ISessionRegistry
using ServerLib.Core.Memory; // PacketPool — 헤더 파싱 유틸(public)

ISessionRegistry registry = ServerNet.CreateSessionRegistry();
IServerListener listener  = ServerNet.CreateListener(registry);
listener.MaxConnections     = 1000;                       // 연결 폭주 방어(B1)
listener.SessionSendTimeout = TimeSpan.FromSeconds(30);   // 죽은 피어 송신 게이트 점유 방지
listener.OnReceived = (session, data) =>
{
    // data는 콜백 반환 시 무효화 — 보관하려면 복사할 것
    return ValueTask.CompletedTask;
};
listener.Start(7777);
// 활성 세션 수: listener.ActiveSessionCount · 전체 스냅샷: registry.GetAll()
```

**4) 클라이언트 — `IClientConnection`**
```csharp
using ServerLib;
using ServerLib.Interface;   // IClientConnection

await using IClientConnection conn = ServerNet.CreateClient();
conn.SendTimeout = TimeSpan.FromSeconds(5);               // 응답불능 서버 송신 무한 블록 방지
conn.OnReceived  = data => ValueTask.CompletedTask;
await conn.ConnectAsync("127.0.0.1", 7777);
await conn.SendAsync(myBytes);
```

> **공개 표면 요약** — public: `ServerNet`(팩토리) · `ServerLib.Interface`의 전체 인터페이스 · 직렬화 빌딩블록(`IPacket`·`IPacketSerializer`·`BinaryPacketSerializer`·패킷 타입·`PacketPool`) · `ServerMetrics` · `SessionContextExtensions`. internal: `SocketPipelineListener`/`~Client`/`~Session` · `SessionRegistry`.
> 동작하는 전체 예제는 `Server/Program.cs`·`Client/Program.cs`를 참고.

---

### `ISession` — 클라이언트 세션
서버에 연결된 클라이언트 1개의 생명주기(연결 → 수신/송신 → 해제)를 정의합니다. `IAsyncDisposable`을 상속하며 Zero-allocation 설계를 계약으로 강제합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `Guid SessionId { get; }` | 세션 전역 고유 식별자 (불변) | Thread-safe, Zero-allocation |
| `EndPoint? RemoteEndPoint { get; }` | 클라이언트 원격 IP:포트 (UDP·로컬 파이프는 null 가능) | 연결 수립 후 불변 |
| `DateTimeOffset ConnectedAt { get; }` | 세션 수립 UTC 시각 (만료 판정·모니터링용) | Thread-safe, 불변 |
| `Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }` | 데이터 수신 시 호출 콜백 | I/O 스레드에서 호출; 반환 후 메모리 슬라이스 무효화; 동기 블로킹 금지 |
| `Func<ValueTask>? OnDisconnected { get; set; }` | 연결 종료 시 호출 콜백 | 세션 생명주기 동안 정확히 1회 발화 |
| `ValueTask SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | 데이터 비동기 전송 | Thread-safe, Zero-allocation, Non-blocking |

> 편의 오버로드: `session.SendAsync(packet)` (`PacketSendExtensions`, `ServerLib.Core.Serialization`) — `IPacket`을 직접 받아 직렬화·풀 버퍼 관리를 캡슐화한다. 송신 동기 완료 시 무할당. `IClientConnection`에도 동일 오버로드 제공. (hot loop는 1회 직렬화 후 `ReadOnlyMemory` 직접 송신이 유리.)

> 컨텍스트 접근: `session.GetContext<T>()` / `session.TryGetContext<T>(out var ctx)` (`SessionContextExtensions`, `ServerLib.Core`) — `object? Context`를 캐스팅 없이 타입 안전하게 읽는다(미설정·불일치 시 default).

> 상태 소유권(`TransitionTo`): transport 생명주기 상태(`Connecting`/`Connected`/`Disconnecting`/`Disconnected`)는 라이브러리가 소유·구동한다. 소비자는 `Authenticated`/`Custom(≥5)` 앱 레벨 상태만 설정할 것(직접 transport 전환 시 보고 상태와 실제 소켓 상태 불일치). 하드 강제는 Disconnected 부활 차단(CAS)뿐.

---

### `IServerListener` — TCP 서버 리스너
지정 포트에서 클라이언트 연결을 수락하고 세션 이벤트(연결·수신·해제)를 콜백으로 전달합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `bool IsRunning { get; }` | accept 루프 구동 여부 | Thread-safe 스냅샷 |
| `Func<ISession, ValueTask>? OnClientConnected { get; set; }` | 클라이언트 접속 시 콜백 | OnClientDisconnected보다 항상 먼저 발화 |
| `Func<ISession, ValueTask>? OnClientDisconnected { get; set; }` | 세션 해제 시 콜백 | 세션당 정확히 1회; 반환 후 세션 해제 |
| `Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }` | 데이터 수신 시 콜백 | 다수 세션 병렬 호출 가능; 메모리 슬라이스는 반환 후 무효화 |
| `TimeSpan? SessionSendTimeout { get; set; }` | 신규 세션 송신 타임아웃 (`null`=무한) | `Start()` 전 설정 권장; 이후 수락 세션부터 적용 |
| `int ActiveSessionCount { get; }` | 현재 활성 세션 수 | Thread-safe; 폭주 후 0 복귀로 세션 누수 부재 검증 |
| `void Start(int port)` | 지정 포트에서 연결 수락 시작 | Non-blocking; Not thread-safe |
| `void Stop()` | accept 루프 중지 및 소켓 종료 | Non-blocking; Not thread-safe |

---

### `IRpcHandler` — RPC 디스패처
수신된 패킷 페이로드를 패킷 ID 기준으로 적절한 RPC 핸들러로 라우팅합니다. `Rpc.Generator`의 Source Generator가 `[RpcService]` 인터페이스를 분석하여 구현체를 자동 생성합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `ValueTask DispatchAsync(ISession, ReadOnlyMemory<byte>, CancellationToken)` | 패킷 ID 앞 2바이트로 라우팅 | I/O 스레드 호출; 디스패치 자체는 Zero-allocation; Thread-safe |

---

### `IClientConnection` — 클라이언트 연결
원격 서버에 대한 단일 TCP 연결을 나타냅니다. `IAsyncDisposable`을 상속하며 System.IO.Pipelines 기반 Zero-copy 수신을 강제합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `bool IsConnected { get; }` | 서버 연결 여부 (스냅샷) | Thread-safe; 확인 직후 끊길 수 있으므로 방어적 처리 필요 |
| `Func<ValueTask>? OnConnected { get; set; }` | 연결 수립 직후 콜백 | OnDisconnected보다 항상 먼저 발화; 연결 실패 시 미발화 |
| `Func<ValueTask>? OnDisconnected { get; set; }` | 연결 종료 시 콜백 | 연결 수립 후 반드시 1회 발화 |
| `Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }` | 데이터 수신 시 콜백 | I/O 스레드 호출; 메모리는 반환 후 무효화 |
| `TimeSpan? SendTimeout { get; set; }` | 단일 송신 타임아웃 (`null`=무한) | `ConnectAsync()` 전 설정 권장; 응답불능 서버 송신 무한 블록 방지 |
| `Task ConnectAsync(string host, int port, CancellationToken)` | 비동기 TCP 연결 수립 | 성공 시 수신 루프 자동 시작; Non-blocking; Not thread-safe |
| `ValueTask SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | 서버에 데이터 비동기 전송 | Zero-allocation; Thread-safe; Non-blocking |
| `void Disconnect()` | 연결 즉시 동기 종료 | Non-blocking; Thread-safe; 진행 중 I/O 강제 취소 |

---

### `IPacketSerializer` — 패킷 직렬화기
패킷 객체와 바이트 버퍼 사이의 직렬화/역직렬화를 정의합니다. 모든 연산이 `Span`/`ReadOnlySpan` 기반으로 동작하여 힙 할당 없는 Zero-copy를 보장합니다.

> 네임스페이스: `ServerLib.Core.Serialization` (`IPacket`과 함께 직렬화 서브시스템에 위치)

> 패킷 구조: `PacketId(2B) | BodyLength(2B) | Body(NB)` — LittleEndian

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `int Serialize<T>(T packet, Span<byte> destination) where T : IPacket` | 패킷을 destination에 직접 기록, 기록 바이트 수 반환 | Zero-allocation; Thread-safe; Non-blocking |
| `T Deserialize<T>(ReadOnlySpan<byte> source) where T : IPacket, new()` | 전체 패킷 버퍼 역직렬화 | T가 struct면 Zero-allocation; class면 1회 힙 할당; Thread-safe |
| `bool TryReadPacketLength(ReadOnlySpan<byte> header, out int totalLength)` | 헤더 파싱으로 전체 패킷 길이 반환 (부분 수신 판단용) | Zero-allocation; Thread-safe |

---

### `ISessionRegistry` — 세션 레지스트리 (읽기)
서버에 연결된 활성 세션 전체를 추적합니다. 외부 소비자용 읽기 전용 인터페이스입니다. 등록·해제는 `ISessionRegistrar`를 통해 수행됩니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `int Count { get; }` | 현재 연결된 세션 수 | Thread-safe; Zero-allocation |
| `bool TryGet(Guid sessionId, out ISession? session)` | SessionId로 세션 조회 (없으면 false) | Thread-safe; Zero-allocation |
| `IReadOnlyCollection<ISession> GetAll()` | 호출 시점 활성 세션 스냅샷 반환 | Thread-safe; 호출마다 배열 할당 — hot path 반복 호출 금지 |
| `ValueTask BroadcastAsync(ReadOnlyMemory<byte>, CancellationToken)` | 전체 세션 병렬 전송 (개별 실패는 무시, `OperationCanceledException`은 전파) | Thread-safe; 호출마다 스냅샷·ValueTask[] 할당 |

---

### `ISessionRegistrar` — 세션 레지스트리 (쓰기)
세션 등록·해제 전용 인터페이스입니다. `SocketPipelineListener` 내부에서만 사용하며 외부 코드는 `ISessionRegistry`만 참조해야 합니다.

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `void Register(ISession session)` | 세션을 레지스트리에 등록 | Thread-safe; Zero-allocation; Non-blocking |
| `void Unregister(Guid sessionId)` | 세션을 레지스트리에서 제거 (없는 ID여도 예외 없음) | Thread-safe; Zero-allocation; Non-blocking |

---

### `IPacket` — 네트워크 패킷 계약
바이너리 직렬화 가능한 패킷의 최소 계약입니다. 구현체는 헤더를 제외한 본문(body)만 직렬화·역직렬화하며, 헤더 기록은 `BinaryPacketSerializer`가 담당합니다.

> 네임스페이스: `ServerLib.Core.Serialization`

| 멤버 | 설명 | 주요 제약 |
|------|------|----------|
| `ushort PacketId { get; }` | 패킷 식별자 — RpcDispatcher 라우팅 키, 헤더 첫 2바이트에 기록 | 불변 |
| `void Serialize(ref SpanWriter writer)` | 패킷 본문 필드를 순서대로 기록 (헤더 미포함) | 문자열 외 Zero-allocation; Non-blocking |
| `void Deserialize(ref SpanReader reader)` | reader에서 본문 필드를 순서대로 읽어 채움 | Serialize와 동일 순서 유지 필수 |
| `int GetBodySize()` | 직렬화에 필요한 본문 바이트 수 반환 (헤더 제외) | Zero-allocation; 사전 버퍼 대여에 사용 |

---

## 2. 하네스 & 플러그인

### 하네스 (Harnesses)
도메인별 에이전트 팀을 조율하는 자동화 파이프라인입니다. 트리거 키워드를 입력하면 오케스트레이터 스킬이 자동 실행됩니다.

| 이름 | 오케스트레이터 스킬 | 기능 요약 |
|------|-------------------|----------|
| **Git 자동 커밋 & 푸시** | `commitandpush` | 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋·푸시 파이프라인. 민감 정보 감지 시 커밋 차단 |
| **종합 코드 리뷰** | `code-review-orchestrator` | 아키텍처·보안·성능·스타일 4개 에이전트 병렬 감사 → 단일 통합 리포트 생성 |
| **동시성 가드** | `concurrency-guard-orchestrator` | Lock-Free 설계 강제 · 락 정당화 주석 감사 · 데드락 정적 분석(생성-검증) → 단일 동시성 리포트 |
| **GC 가드** | `gc-guard-orchestrator` | 힙 할당 스캐너 · 풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴 제거 |
| **파이프라인 아키텍처** | `pipeline-architect-orchestrator` | Pipelines 기반 Zero-copy IO 루프 + Channel\<T\> 락-프리 디스패처 설계 및 부하 테스트 감사 |
| **TDD** | `tdd-orchestrator` | Red(실패 테스트) → Green(최소 구현) → Refactor(검증) 사이클을 에이전트 팀으로 완주 + 진화 델타 포착 |

### 플러그인 (Plugins)

| 이름 | 마켓플레이스 | 기능 요약 |
|------|------------|----------|
| **harness** | harness-marketplace | 전문 에이전트 정의 + 해당 에이전트용 스킬 생성 — 도메인 하네스 구축·점검·동기화를 위한 메타 도구 |
| **superpowers** | claude-plugins-official | `brainstorming` · `test-driven-development` · `writing-plans` · `requesting-code-review` · `using-git-worktrees` 등 범용 개발 워크플로 스킬 모음 |

---

## 3. 에이전트 활용 (Agent Orchestration)

각 하네스는 단일 에이전트가 아니라 **전문 에이전트 팀**이 협력해 동작합니다. 트리거 키워드 → **오케스트레이터 스킬**이 팀을 구성(`TeamCreate`)하고, 팀원 에이전트들이 각자의 **개별 스킬**로 작업한 뒤 단일 리포트로 통합됩니다.

```
하네스 트리거("코드 리뷰해줘")
   └─▶ 오케스트레이터 스킬 (code-review-orchestrator)
          └─▶ 에이전트 팀 (architecture / security / performance / style-reviewer)
                 └─▶ 개별 스킬 (architecture-review, security-review, …) → 통합 리포트
```

- 에이전트 정의: `.claude/agents/*.md` — **총 21개**
- 스킬 정의: `.claude/skills/*/SKILL.md` — 오케스트레이터 5개 + 개별 스킬 12개 + `harness` 메타스킬

### 3.1 에이전트 카탈로그 (21개)

| 팀 | 에이전트 | 역할 | 주요 산출물 |
|----|---------|------|-----------|
| **Git 자동화** | `git-security-auditor` | diff 전체에서 민감 정보(API 키·`.env`·비밀번호) 탐지 → 차단 게이트 | PASS/FAIL 판정 |
| | `git-commit-writer` | git log 스타일 학습 → WHY 중심 한국어 커밋 메시지 생성 | 커밋 메시지 |
| | `git-push-controller` | 커밋 실행 → pre-commit hook 처리 → push (원격 없으면 로컬만) | push 결과 |
| **종합 코드 리뷰** | `architecture-reviewer` | SOLID·레이어 경계·결합도·의존성 방향 감사 | `02_architecture_findings.json` |
| | `security-reviewer` | OWASP Top 10·CWE 기반 인젝션·인증·민감정보 노출 스캔 | `02_security_findings.json` |
| | `performance-reviewer` | N+1·동기 I/O 블로킹·힙 할당·LINQ 비효율·캐싱 누락 | `02_performance_findings.json` |
| | `style-reviewer` | 네이밍·복잡도·중복·문서화·테스트 커버리지 갭 | `02_style_findings.json` |
| **GC 가드** | `heap-allocation-scanner` | hot path 힙 할당(boxing·루프 내 new·LINQ·클로저) 탐지 | `02_allocation_findings.json` |
| | `pooling-enforcer` | `ValueTask`·`ReadOnlySpan<T>`·`ArrayPool<T>` 적용 강제 | `02_pooling_findings.json` |
| | `allocation-peer-reviewer` | 위 두 보고서 교차 검증(FP 기각·FN 보완·fix 검증) | `03_peer_review.json` |
| **동시성 가드** | `lock-free-enforcer` | 전통적 락 탐지 → Lock-Free(Interlocked·Channel) 대체 판정 | `02_lockfree_findings.json` |
| | `lock-justification-auditor` | 모든 락에 `[LOCK-REQUIRED]` 정당화 주석 존재·품질 감사 | `02_lockjustification_findings.json` |
| | `deadlock-analyzer` | async 데드락 정적 분석(`.Result`·lock+await·세마포어 누수) | `03_deadlock_analysis.json` |
| | `deadlock-reviewer` | 위 분석 독립 검증(FP 제거·FN 추가·최대 1회 재분석) | `03_deadlock_review.json` |
| **파이프라인 아키텍처** | `pipeline-supervisor` | 워커 할당·인터페이스 협상·품질 게이트·통합 (감독자) | `04_pipeline_architecture.md` |
| | `io-loop-designer` | System.IO.Pipelines 기반 IO 루프(백프레셔·AdvanceTo) 설계 | `02_io_loop/IoLoop.cs` |
| | `thread-dispatcher-designer` | `Channel<T>` 기반 락-프리 스레드 디스패처 설계 | `02_dispatcher/ThreadDispatcher.cs` |
| | `load-test-auditor` | 부하 관점 감사(버퍼 누수·Zero-copy 위반·백프레셔 오작동) | `03_load_test_audit.md` |
| **TDD** | `tdd-analyst` | Red: 요구사항 → 실패하는 xUnit 테스트 + 스텁 설계 | `Tests/*.cs`, `Src/*.cs` 스텁 |
| | `tdd-builder` | Green: 테스트 통과 최소 구현 (Gold Plating 금지) | `Src/*.cs` 구현 |
| | `tdd-qa` | Refactor: `dotnet test` 실행 → 검증 게이트 → 리팩토링 가이드 | `test_results.txt`, `refactor_guide.md` |

### 3.2 오케스트레이션 패턴 4종

에이전트 팀을 묶는 방식은 작업 성격에 따라 네 가지로 나뉩니다.

**① 병렬 팬아웃 (Parallel Fan-out)** — 독립 도메인을 동시에 감사하고 통합.
- 사용: **종합 코드 리뷰**(4명 동시), **GC 가드 / 동시성 가드**의 감사 단계(2명 동시).
- 에이전트 간 동일 위치 발견은 `SendMessage`로 조율해 중복 제거.
- 통합 시 가중치 적용 (예: 코드 리뷰 종합 점수 = security 35% · architecture 25% · performance 25% · style 15%).

```
오케스트레이터 ─┬─▶ architecture-reviewer ─┐
               ├─▶ security-reviewer ──────┤
               ├─▶ performance-reviewer ───┼─▶ 통합 리포트
               └─▶ style-reviewer ─────────┘
```

**② 순차 파이프라인 (Pipeline)** — 단계별 게이트, 앞 단계 실패 시 중단.
- 사용: **Git 자동화**(`commitandpush`).
- 보안 감사 FAIL 시 즉시 중단 → 커밋·푸시 단계로 진행하지 않음.

```
git-security-auditor ─(PASS)─▶ git-commit-writer ─▶ git-push-controller
        └─(FAIL)─▶ 파이프라인 중단
```

**③ 생성-검증 (Producer–Reviewer)** — 생성자 산출물을 독립 리뷰어가 교차 검증.
- 사용: **GC 가드**(scanner/enforcer → `allocation-peer-reviewer`), **동시성 가드**(`deadlock-analyzer` → `deadlock-reviewer`, 최대 1회 재분석), **TDD**(`tdd-builder` → `tdd-qa` Review Gate, 재작업 최대 2회).
- 리뷰어는 False Positive 기각·False Negative 보완·수정 코드 안전성을 독립적으로 판정. `tdd-qa`는 코드 리뷰만으로 PASS 금지 — 반드시 `dotnet test` 실행.

```
deadlock-analyzer ─▶ deadlock-reviewer ─(FP 기각 / FN 추가 / 재분석 요청)─▶ 확정 리포트
```

**④ 감독자 (Supervisor)** — 중앙 감독자가 워커를 할당·모니터링·동적 재할당.
- 사용: **파이프라인 아키텍처**(`pipeline-supervisor`).
- 감독자가 워커 간 인터페이스(IO 루프 출력 ↔ 디스패처 입력)를 먼저 협상시키고, 각 워커 완료 시 품질 게이트로 검증, 미달 시 재작업 지시(한도 초과 시 직접 보완), 마지막에 `load-test-auditor`에게 감사 위임.

```
pipeline-supervisor ─┬─ 인터페이스 협상
                     ├─▶ io-loop-designer ──────┐ 품질 게이트
                     ├─▶ thread-dispatcher-designer ┘ → load-test-auditor → 통합
```

### 3.3 Stop 훅 자동화

세션 종료 시 변경사항을 자동 커밋하는 훅이 구성돼 있습니다.

- `.claude/settings.json`의 **Stop 훅**이 `scripts/auto-commit.ps1`을 실행.
- 코드 변경을 끝낸 에이전트는 WHY 중심 한국어 커밋 메시지를 **`.git/auto_commit_msg.txt`** (UTF-8)에 작성.
- 훅이 이 파일을 읽어 커밋 후 즉시 삭제. 파일이 없으면 접두사 기반 폴백 메시지로 커밋(안전망).
- 파일 기반 전달을 쓰는 이유: nested `claude -p`의 stdin/콜드스타트 취약성으로 폴백이 빈발했기 때문(2026-06-03 재설계).

### 3.4 에이전트 · 스킬 · 하네스 관계

| 계층 | 위치 | 역할 |
|------|------|------|
| 하네스 | `CLAUDE.md` 하네스 섹션 | 트리거 키워드·목표·변경 이력 정의 |
| 오케스트레이터 스킬 | `.claude/skills/*-orchestrator/SKILL.md` | 팀 구성·페이즈 조율·리포트 통합 |
| 에이전트 | `.claude/agents/*.md` | 단일 도메인 전문 작업 수행 |
| 개별 스킬 | `.claude/skills/*/SKILL.md` | 각 에이전트의 구체적 작업 절차 |
