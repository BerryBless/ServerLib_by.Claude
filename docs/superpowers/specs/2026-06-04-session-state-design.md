# Session State 설계 문서

**날짜:** 2026-06-04
**상태:** 승인됨

---

## 1. 배경 및 목적

현재 `ISession`은 타임스탬프(`ConnectedAt`, `LastReceivedAt`)와 식별자(`SessionId`, `RemoteEndPoint`)만 노출한다. 서버 애플리케이션이 per-session 상태(인증 여부, 게임 단계 등)나 사용자 데이터(플레이어 정보)를 관리할 방법이 없어 전역 변수나 외부 딕셔너리에 의존해야 한다.

**해결 목표:**
- 세션 생명주기 상태(`Connecting → Connected → Authenticated → Disconnecting → Disconnected`)를 thread-safe하게 추적
- 커스텀 컨텍스트 객체를 세션에 직접 부착 (`session.Context = new GameContext()`)
- `SessionState`를 사용자가 확장 가능하도록 `readonly struct` + `int` 기반 설계

---

## 2. 설계 결정

| 항목 | 채택안 | 비고 |
|------|--------|------|
| 상태 타입 | `readonly struct SessionState` + `int` 기반 | `enum`보다 확장성 높음 |
| 상태 전환 | `TransitionTo(SessionState)` — `Volatile.Write` | 단순 덮어쓰기 (CAS 불필요) |
| 데이터 저장소 | `object? Context { get; set; }` — 단일 컨텍스트 | 키-값 딕셔너리 미채택 (GC 압력) |
| Thread-safety | `_state: int + Volatile`, `_context: object? + Volatile` | 기존 `_lastReceivedAtTicks` 패턴 일치 |
| 리스너 자동 전환 | `AcceptLoopAsync` → `Connected`, `OnDisconnected` → `Disconnected` | 라이브러리가 기본 전환 담당 |

---

## 3. 컴포넌트 구조

```
ServerLib/
├── Interface/
│   ├── ISession.cs              ← MOD: State, TransitionTo, Context 추가
│   └── SessionState.cs          ← NEW: readonly struct (predefined 5개 + 사용자 확장)
└── Core/
    └── Transport/
        └── SocketPipelineSession.cs  ← MOD: _state(int), _context(object?) + Volatile

ServerLib.Tests/
├── Stubs/StubSession.cs          ← MOD: State, TransitionTo, Context 구현 추가
└── SessionStateTests.cs          ← NEW: 단위 테스트 (6개)

Server/Program.cs                 ← MOD: GameContext 정의 + 사용 예제
```

---

## 4. 핵심 API

### SessionState

```csharp
// ServerLib/Interface/SessionState.cs
public readonly struct SessionState : IEquatable<SessionState>
{
    public int Value { get; }
    public SessionState(int value) => Value = value;

    public static readonly SessionState Connecting    = new(0);
    public static readonly SessionState Connected     = new(1);
    public static readonly SessionState Authenticated = new(2);
    public static readonly SessionState Disconnecting = new(3);
    public static readonly SessionState Disconnected  = new(4);

    // 사용자 정의: new SessionState(100)
    public bool Equals(SessionState other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SessionState s && Equals(s);
    public override int GetHashCode() => Value;
    public static bool operator ==(SessionState a, SessionState b) => a.Value == b.Value;
    public static bool operator !=(SessionState a, SessionState b) => a.Value != b.Value;
    public override string ToString() => Value switch
    {
        0 => "Connecting", 1 => "Connected", 2 => "Authenticated",
        3 => "Disconnecting", 4 => "Disconnected", _ => $"Custom({Value})"
    };
}
```

### ISession 추가 멤버

```csharp
/// <summary>현재 세션 생명주기 상태입니다.</summary>
/// Thread-safe, Volatile read, Non-blocking
SessionState State { get; }

/// <summary>세션 상태를 전환합니다.</summary>
/// Thread-safe (Volatile.Write), Non-blocking
/// <returns>항상 true (단순 덮어쓰기)</returns>
bool TransitionTo(SessionState newState);

/// <summary>세션에 부착된 단일 사용자 컨텍스트 객체입니다.</summary>
/// Thread-safe (Volatile read/write), Zero-allocation (참조 저장)
/// 사용자가 직접 클래스 정의 후 할당. 읽을 때 캐스팅 필요.
object? Context { get; set; }
```

### SocketPipelineSession 내부 구현

```csharp
// 필드
private int _state = SessionState.Connecting.Value; // Volatile 갱신
private object? _context;                            // Volatile 갱신

// 프로퍼티
public SessionState State => new SessionState(Volatile.Read(ref _state));

public bool TransitionTo(SessionState newState)
{
    Volatile.Write(ref _state, newState.Value);
    return true;
}

public object? Context
{
    get => Volatile.Read(ref _context);
    set => Volatile.Write(ref _context, value);
}
```

### SocketPipelineListener 자동 전환

```csharp
// AcceptLoopAsync — 세션 생성 후:
session.TransitionTo(SessionState.Connected);

// OnDisconnected 람다 내부 — DisposeAsync 직전:
session.TransitionTo(SessionState.Disconnected);
```

### Server/Program.cs 예제

```csharp
// 커스텀 컨텍스트 클래스 정의
record GameContext(int PlayerId = 0, string Nickname = "Guest");

listener.OnClientConnected = session =>
{
    session.Context = new GameContext(PlayerId: 1001, Nickname: "홍길동");
    Console.WriteLine($"[+] {session.RemoteEndPoint}  state={session.State}");
    return ValueTask.CompletedTask;
};

listener.OnReceived = (session, data) =>
{
    if (session.Context is GameContext ctx)
    {
        // ctx.PlayerId, ctx.Nickname 사용
    }
    return ValueTask.CompletedTask;
};

// RPC 핸들러 예시: 인증 완료 시
session.TransitionTo(SessionState.Authenticated);
```

---

## 5. 테스트 케이스

| 테스트명 | 검증 내용 |
|---------|----------|
| `State_Initial_IsConnecting` | 초기 State = Connecting |
| `TransitionTo_NewState_ReturnsTrue` | 상태 전환 후 State 변경 확인 |
| `TransitionTo_SameState_ReturnsTrueAndKeepsState` | 동일 상태 재전환 정상 처리 |
| `Context_SetAndGet_ReturnsSameReference` | 컨텍스트 객체 참조 동일성 |
| `Context_Null_Default` | 초기 Context = null |
| `SessionState_CustomValue_WorksCorrectly` | new SessionState(100) 사용자 정의 상태 |

---

## 6. 변경 파일 목록

| 파일 | 유형 | 핵심 변경 |
|------|------|----------|
| `ServerLib/Interface/SessionState.cs` | 신규 | 5개 predefined 상수 + ToString |
| `ServerLib/Interface/ISession.cs` | 수정 | State, TransitionTo, Context 추가 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | _state, _context 필드 + Volatile 구현 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | Connected/Disconnected 자동 전환 |
| `ServerLib.Tests/Stubs/StubSession.cs` | 수정 | State, TransitionTo, Context stub 구현 |
| `ServerLib.Tests/SessionStateTests.cs` | 신규 | 6개 단위 테스트 |
| `Server/Program.cs` | 수정 | GameContext + 사용 예제 |

---

## 7. 빌드 검증

```bash
dotnet build ClaudeCodeStudy.sln
dotnet test ServerLib.Tests --logger "console;verbosity=normal"
```

---

## 8. 향후 확장 포인트

- **상태 전환 검증** — 허용된 전환만 통과하는 `TransitionTo` 오버로드 (예: `Connecting → Connected`만 허용)
- **상태 변경 이벤트** — `OnStateChanged` 콜백 추가
- **typed Context** — `GetContext<T>()` 확장 메서드로 캐스팅 편의 제공
