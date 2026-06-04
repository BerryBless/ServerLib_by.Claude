# 설계: 콜백 설정 안전성 강화 (E3)

**날짜:** 2026-06-05
**출처:** 성능 우선 코드 리뷰(`plan/perf_review_0604.md`) 권장 항목 **E3**
**상태:** 설계 승인됨 → 구현 계획 대기

## 배경 및 목적

세션/리스너/클라이언트의 이벤트 콜백은 모두 mutable `Func<…>? { get; set; }` 프로퍼티다. 두 가지 잠재 문제가 있다:

1. **Start/Connect 이후 설정이 조용히 허용됨** — 현재 가드는 `IServerListener.IdleTimeout` 단 하나뿐(`SocketPipelineListener.cs:25` `if (IsRunning) throw`). 나머지 콜백은 plain auto-property라 IO 루프가 이미 도는 중에 재할당해도 예외가 없다.
2. **가시성 fragility** — 델리게이트 필드가 non-volatile이고 IO 스레드에서 읽힌다. 현재는 "start 이전 설정"이라는 happens-before 덕에 안전하지만, start 이후 재할당하면 IO 스레드가 옛 값을 볼 수 있다(lock-free 감사가 지적).

**실사용 패턴은 항상 "Start/Connect 이전 설정"** (`Server/Program.cs`, `Client/Program.cs`, `SocketPipelineListener`의 내부 세션 배선 모두). 따라서 목표는 *기존 안전한 패턴을 계약으로 강제*하고 *재할당 fragility를 제거*하는 것이다.

**목표:** 설정 모델(`=` 할당, 단일 구독)과 인터페이스 시그니처를 유지하면서, 모든 소비자 대면 콜백에 "Start/Connect 이후 설정 시 `InvalidOperationException`" 가드를 추가하고, 그로써 가시성 문제도 근본 제거한다.

**비목표(YAGNI):**
- 멀티 구독(`event +=`) — hot path(`OnReceived`)에 멀티캐스트 순회·할당 비용이 생겨 성능 1순위와 충돌하며, 다중 구독 수요 근거 없음(단일-핸들러 내부 라우팅 모델). 배제.
- init-only/옵션 객체 주입 — 소비자 코드 형태를 바꿔야 함. 안전성 목표는 가드로 충분히 달성되므로 과도. 배제.
- 설정 프로퍼티(`SendTimeout`/`PingInterval`)에 대한 가드 — E3는 *콜백* 범위. `SendTimeout`은 P3에서 의도적으로 "언제든 설정 가능"으로 둠. 변경 없음.
- `ISession` 등 인터페이스 시그니처 변경 — 가드는 구현체 setter 동작이므로 시그니처 불변. `StubSession` 무영향.

## 설계 결정

| 항목 | 채택 | 대안(미채택) | 사유 |
|------|------|------------|------|
| 안전 메커니즘 | setter throw-after-start 가드 | init-only/옵션 주입 | 소비자 코드 변경 0, 기존 IdleTimeout 패턴과 일관 |
| 구독 모델 | 단일(`=`) 유지 | 멀티(`event`) | hot path 비용·수요 없음 |
| 가시성 | 가드로 쓰기를 start 이전 한정 → start 배리어로 충분 | 모든 콜백 필드 volatile | 가드가 재할당을 막으므로 개별 volatile 불필요 |
| 인터페이스 | 시그니처 불변, 문서만 보강 | get;init; 등 | 구현체 동작 변경으로 충분, 비파괴 |

## 컴포넌트 구조

변경은 3개 구현체 + 인터페이스 문서에 국한:

```
ServerLib/Core/Transport/
├─ SocketPipelineListener.cs   콜백 4개 setter 가드 (IsRunning 기준)
├─ SocketPipelineClient.cs     콜백 3개 setter 가드 (_started 플래그)
└─ SocketPipelineSession.cs    콜백 2개 setter 가드 (_receiving 플래그)
ServerLib/Interface/
├─ IServerListener.cs          On* 4개 XML 문서에 "Start 전 설정" 명시
├─ IClientConnection.cs        On* 3개 XML 문서에 "ConnectAsync 전 설정" 명시
└─ ISession.cs                 On* 2개 XML 문서에 "StartReceiving 전 설정" 명시
```

## 핵심 동작

### SocketPipelineListener (이미 `IsRunning => _listenSocket != null` 존재)
`OnClientConnected`·`OnClientDisconnected`·`OnReceived`·`OnIdleTimeout`를 auto-property → 백킹필드 + 가드 setter:
```csharp
private Func<ISession, ValueTask>? _onClientConnected;
public Func<ISession, ValueTask>? OnClientConnected
{
    get => _onClientConnected;
    set
    {
        if (IsRunning) throw new InvalidOperationException(
            "OnClientConnected는 Start() 호출 전에만 설정할 수 있습니다.");
        _onClientConnected = value;
    }
}
```
(나머지 3개 동일 패턴, 각자 메시지.) `IdleTimeout`은 이미 동일 가드 보유 — 그대로.

### SocketPipelineClient
`ConnectAsync` 진입부에서 `_started = true` 설정(기존 `ObjectDisposedException.ThrowIf` 직후). 콜백 3개 setter:
```csharp
private bool _started;
private Func<ValueTask>? _onConnected;
public Func<ValueTask>? OnConnected
{
    get => _onConnected;
    set
    {
        if (_started) throw new InvalidOperationException(
            "OnConnected는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
        _onConnected = value;
    }
}
```
(`OnDisconnected`·`OnReceived` 동일.) `_started`는 단일 스레드(연결 셋업)에서 set/read되므로 단순 bool로 충분.

### SocketPipelineSession
`StartReceiving` 진입부에서 `_receiving = true` 설정. 콜백 2개 setter:
```csharp
private bool _receiving;
private Func<ReadOnlyMemory<byte>, ValueTask>? _onReceived;
public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived
{
    get => _onReceived;
    set
    {
        if (_receiving) throw new InvalidOperationException(
            "OnReceived는 StartReceiving() 호출 전에만 설정할 수 있습니다.");
        _onReceived = value;
    }
}
```
(`OnDisconnected` 동일.) **라이브러리(`SocketPipelineListener.AcceptLoopAsync`)는 세션 콜백을 `StartReceiving` 이전에 배선**하므로 가드에 걸리지 않는다. `DispatchPacketAsync`의 `var onReceived = OnReceived;`(P2)는 getter 경유 — 변경 없음.

### 가시성 근거
setter 가드로 콜백 쓰기는 모두 start 이전에 완료된다. `Start`/`ConnectAsync`/`StartReceiving`가 IO 태스크(`AcceptLoopAsync`/`FillPipeAsync`/`ReadPipeAsync`)를 기동하는 시점이 happens-before 배리어를 제공하므로, IO 스레드는 최종 콜백 값을 관찰한다. 별도 필드 volatile 불필요.

## 변경 파일 목록

| 파일 | 종류 | 내용 |
|------|------|------|
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | 콜백 4개 가드 setter |
| `ServerLib/Core/Transport/SocketPipelineClient.cs` | 수정 | `_started` 플래그 + 콜백 3개 가드 setter |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | `_receiving` 플래그 + 콜백 2개 가드 setter |
| `ServerLib/Interface/IServerListener.cs` | 수정 | On* 4개 XML 문서에 "Start 전 설정" 추가 |
| `ServerLib/Interface/IClientConnection.cs` | 수정 | On* 3개 XML 문서에 "ConnectAsync 전 설정" 추가 |
| `ServerLib/Interface/ISession.cs` | 수정 | On* 2개 XML 문서에 "StartReceiving 전 설정" 추가 |
| `ServerLib.Tests/CallbackGuardTests.cs` | 신규 | start 이후 콜백 설정 시 throw 검증 3종 |

비변경: 소비자 코드(`Server/Program.cs`·`Client/Program.cs` — 이미 start 전 설정), `StubSession`(인터페이스 시그니처 불변), 설정 프로퍼티(`SendTimeout`/`PingInterval`).

## 테스트 (`CallbackGuardTests`)

1. **Listener**: `listener.Start(port)` 후 `listener.OnReceived = …` → `InvalidOperationException`. (start 전 설정은 정상 — 기존 테스트가 커버.)
2. **Client**: 연결된 클라이언트에 `client.OnReceived = …` 설정 → `InvalidOperationException`. (loopback 리스너로 ConnectAsync 후 시도.)
3. **Session**: `session.StartReceiving()` 후 `session.OnReceived = …` → `InvalidOperationException`. (loopback 소켓으로 세션 생성·StartReceiving 후 시도.)
4. (회귀) 기존 49개 — start 전 콜백 설정 경로가 깨지지 않음.

## 빌드 검증

```
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release
dotnet test  E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release   # 49 + 신규 3 통과
```

## 향후 확장 포인트

- 다중 핸들러 합성이 실제 필요해지면(예: 미들웨어 파이프라인) hot path 밖 콜백(OnClientConnected 등)에 한해 이벤트/리스트 기반 구독을 별도 검토. OnReceived는 성능상 단일 핸들러 유지 권장.
- 콜백을 ConnectAsync/Start 인자(옵션 객체)로 받는 빌더 API는 별도 사이클로 검토 가능(현재는 비파괴 가드로 충분).
