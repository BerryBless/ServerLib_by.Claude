# Session State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 세션에 생명주기 상태(`SessionState`)와 커스텀 컨텍스트 객체(`object? Context`)를 thread-safe하게 부착하여, 서버 애플리케이션이 per-session 상태·데이터를 관리할 수 있게 한다.

**Architecture:** `SessionState`를 확장 가능한 `readonly struct`로 신규 정의하고, `ISession`에 `State`/`TransitionTo`/`Context` 멤버를 추가한다. `SocketPipelineSession`이 `int _state` + `object? _context`를 `Volatile` read/write로 구현(기존 `_lastReceivedAtTicks` 패턴 일치). `SocketPipelineListener`가 연결/해제 시 `Connected`/`Disconnected`로 자동 전환한다.

**Tech Stack:** .NET 10, xUnit 2.9.0, `System.Threading.Volatile`, `readonly struct`

---

## File Map

| 경로 | 유형 | 역할 |
|------|------|------|
| `ServerLib/Interface/SessionState.cs` | 신규 | 5개 predefined 상태 + 사용자 확장 readonly struct |
| `ServerLib/Interface/ISession.cs` | 수정 | `State`, `TransitionTo`, `Context` 멤버 추가 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | `_state`/`_context` 필드 + Volatile 구현 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | Connected/Disconnected 자동 전환 |
| `ServerLib.Tests/Stubs/StubSession.cs` | 수정 | `State`/`TransitionTo`/`Context` stub 구현 |
| `ServerLib.Tests/SessionStateTests.cs` | 신규 | 단위 테스트 6개 |
| `Server/Program.cs` | 수정 | `GameContext` 정의 + 사용 예제 |

---

## Task 1: SessionState 구조체 + 테스트

**Files:**
- Create: `ServerLib/Interface/SessionState.cs`
- Create: `ServerLib.Tests/SessionStateTests.cs` (첫 1개 테스트)

- [ ] **Step 1: 실패하는 테스트 작성**

`ServerLib.Tests/SessionStateTests.cs` 신규 생성:

```csharp
using ServerLib.Interface;

namespace ServerLib.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public void SessionState_CustomValue_WorksCorrectly()
    {
        var custom = new SessionState(100);

        Assert.Equal(100, custom.Value);
        Assert.Equal("Custom(100)", custom.ToString());
        Assert.NotEqual(SessionState.Connected, custom);
    }

    [Fact]
    public void SessionState_PredefinedConstants_HaveExpectedValues()
    {
        Assert.Equal(0, SessionState.Connecting.Value);
        Assert.Equal(1, SessionState.Connected.Value);
        Assert.Equal(2, SessionState.Authenticated.Value);
        Assert.Equal(3, SessionState.Disconnecting.Value);
        Assert.Equal(4, SessionState.Disconnected.Value);
    }

    [Fact]
    public void SessionState_ToString_ReturnsName()
    {
        Assert.Equal("Connecting", SessionState.Connecting.ToString());
        Assert.Equal("Authenticated", SessionState.Authenticated.ToString());
    }

    [Fact]
    public void SessionState_Equality_ComparesByValue()
    {
        Assert.True(SessionState.Connected == new SessionState(1));
        Assert.True(SessionState.Connected != SessionState.Authenticated);
    }
}
```

- [ ] **Step 2: 빌드 오류 확인 (Red — SessionState 미존재)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib.Tests
```
Expected: `error CS0246: 'SessionState' 형식 또는 네임스페이스 이름을 찾을 수 없습니다.`

- [ ] **Step 3: SessionState 구조체 작성**

`ServerLib/Interface/SessionState.cs` 신규 생성:

```csharp
namespace ServerLib.Interface;

/// <summary>
/// 세션의 생명주기 상태를 나타내는 확장 가능한 값 타입입니다.
/// 5개의 predefined 상태를 제공하며, <c>new SessionState(int)</c>로 사용자 정의 상태를 만들 수 있습니다.
/// </summary>
/// <remarks>
/// <b>[설계 원칙]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> <c>readonly struct</c>이므로 Zero-allocation(스택)입니다.</description></item>
/// <item><description><b>Thread Safety:</b> 불변값이므로 모든 스레드에서 안전하게 비교 가능합니다.</description></item>
/// </list>
/// </remarks>
public readonly struct SessionState : IEquatable<SessionState>
{
    /// <summary>상태를 나타내는 정수값입니다.</summary>
    public int Value { get; }

    /// <summary>지정된 정수값으로 상태를 생성합니다. 사용자 정의 상태는 5 이상의 값을 권장합니다.</summary>
    /// <param name="value">상태 정수값</param>
    public SessionState(int value) => Value = value;

    /// <summary>연결 수립 중 (초기 상태).</summary>
    public static readonly SessionState Connecting = new(0);
    /// <summary>연결 완료.</summary>
    public static readonly SessionState Connected = new(1);
    /// <summary>인증 완료.</summary>
    public static readonly SessionState Authenticated = new(2);
    /// <summary>연결 해제 진행 중.</summary>
    public static readonly SessionState Disconnecting = new(3);
    /// <summary>연결 해제 완료.</summary>
    public static readonly SessionState Disconnected = new(4);

    /// <inheritdoc/>
    public bool Equals(SessionState other) => Value == other.Value;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SessionState s && Equals(s);
    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>두 상태가 같은지 비교합니다.</summary>
    public static bool operator ==(SessionState a, SessionState b) => a.Value == b.Value;
    /// <summary>두 상태가 다른지 비교합니다.</summary>
    public static bool operator !=(SessionState a, SessionState b) => a.Value != b.Value;

    /// <summary>상태의 이름을 반환합니다. 사용자 정의 상태는 <c>Custom(N)</c> 형식입니다.</summary>
    public override string ToString() => Value switch
    {
        0 => "Connecting",
        1 => "Connected",
        2 => "Authenticated",
        3 => "Disconnecting",
        4 => "Disconnected",
        _ => $"Custom({Value})"
    };
}
```

- [ ] **Step 4: 테스트 통과 확인 (Green)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --filter "SessionState" --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Interface/SessionState.cs ServerLib.Tests/SessionStateTests.cs
git commit -m "추가: SessionState 확장 가능한 값 타입 (5개 predefined 상태)"
```

---

## Task 2: ISession 인터페이스 확장 + StubSession 구현

**Files:**
- Modify: `ServerLib/Interface/ISession.cs`
- Modify: `ServerLib.Tests/Stubs/StubSession.cs`
- Modify: `ServerLib.Tests/SessionStateTests.cs` (stub 기반 테스트 2개 추가)

- [ ] **Step 1: 실패하는 테스트 작성**

`ServerLib.Tests/SessionStateTests.cs`의 클래스 끝에 추가:

```csharp
    [Fact]
    public void StubSession_Context_SetAndGet_ReturnsSameReference()
    {
        var stub = new StubSession();
        var payload = new object();

        stub.Context = payload;

        Assert.Same(payload, stub.Context);
    }

    [Fact]
    public void StubSession_Context_Default_IsNull()
    {
        var stub = new StubSession();
        Assert.Null(stub.Context);
    }
```

또한 파일 상단 using에 추가:
```csharp
using ServerLib.Tests.Stubs;
```

- [ ] **Step 2: 빌드 오류 확인 (Red — Context 미존재)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib.Tests
```
Expected: `error CS1061: 'StubSession' does not contain a definition for 'Context'`

- [ ] **Step 3: ISession에 멤버 추가**

`ServerLib/Interface/ISession.cs`의 `LastReceivedAt` 프로퍼티 정의 다음(닫는 `}` 뒤, `OnReceived` 앞)에 추가:

```csharp
    /// <summary>
    /// 현재 세션의 생명주기 상태입니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile read로 최신값을 반환합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation (값 타입).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    SessionState State { get; }

    /// <summary>
    /// 세션 상태를 새 상태로 전환합니다.
    /// </summary>
    /// <param name="newState">전환할 새 상태</param>
    /// <returns>전환이 적용되면 <see langword="true"/> (현재 구현은 항상 true).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile.Write로 원자적 갱신합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    bool TransitionTo(SessionState newState);

    /// <summary>
    /// 세션에 부착된 단일 사용자 컨텍스트 객체입니다. 사용자가 직접 클래스를 정의하여 할당하며, 읽을 때 캐스팅이 필요합니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Volatile read/write로 참조를 원자적으로 갱신합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation (참조만 저장, 박싱 없음).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    object? Context { get; set; }
```

- [ ] **Step 4: StubSession에 구현 추가**

`ServerLib.Tests/Stubs/StubSession.cs`의 `LastReceivedAt` 프로퍼티 다음 줄에 추가:

```csharp
    public SessionState State { get; private set; } = SessionState.Connecting;
    public object? Context { get; set; }

    public bool TransitionTo(SessionState newState)
    {
        State = newState;
        return true;
    }
```

- [ ] **Step 5: 테스트 통과 확인 (Green)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --filter "SessionState" --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 6: 전체 테스트 회귀 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln && dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Build succeeded.` + `Passed! - Failed: 0, Passed: 24`

- [ ] **Step 7: 커밋**

```bash
git add ServerLib/Interface/ISession.cs ServerLib.Tests/Stubs/StubSession.cs ServerLib.Tests/SessionStateTests.cs
git commit -m "추가: ISession에 State·TransitionTo·Context 멤버 및 StubSession 구현"
```

---

## Task 3: SocketPipelineSession 구현

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineSession.cs`

- [ ] **Step 1: 필드 추가**

`SocketPipelineSession.cs`의 `private long _lastReceivedAtTicks;` 다음 줄에 추가:

```csharp
    private int _state = SessionState.Connecting.Value; // Volatile 갱신
    private object? _context;                            // Volatile 갱신
```

- [ ] **Step 2: 프로퍼티 추가**

`public DateTimeOffset LastReceivedAt => ...` 줄 다음에 추가:

```csharp
    public SessionState State => new SessionState(Volatile.Read(ref _state));

    public object? Context
    {
        get => Volatile.Read(ref _context);
        set => Volatile.Write(ref _context, value);
    }
```

- [ ] **Step 3: TransitionTo 메서드 추가**

`public void StartReceiving()` 메서드 정의 바로 앞에 추가:

```csharp
    public bool TransitionTo(SessionState newState)
    {
        Volatile.Write(ref _state, newState.Value);
        return true;
    }

```

- [ ] **Step 4: 빌드 및 기존 테스트 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln && dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Build succeeded.` + `Passed! - Failed: 0, Passed: 24`

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineSession.cs
git commit -m "추가: SocketPipelineSession에 State·TransitionTo·Context 구현 (Volatile)"
```

---

## Task 4: SocketPipelineListener 자동 상태 전환

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineListener.cs`

- [ ] **Step 1: AcceptLoopAsync에 Connected 전환 추가**

`SocketPipelineListener.cs`의 `AcceptLoopAsync`에서 `_activeSessions[session.SessionId] = session;` 다음 줄에 추가:

```csharp
                session.TransitionTo(SessionState.Connected);
```

수정 후 해당 블록:
```csharp
                _registrar?.Register(session);
                _activeSessions[session.SessionId] = session;
                session.TransitionTo(SessionState.Connected);
                session.StartReceiving();
```

- [ ] **Step 2: OnDisconnected 람다에 Disconnected 전환 추가**

같은 파일 `AcceptLoopAsync`의 `session.OnDisconnected = async () =>` 람다에서 `_activeSessions.TryRemove(session.SessionId, out _);` 다음 줄에 추가:

```csharp
                    session.TransitionTo(SessionState.Disconnected);
```

수정 후 해당 람다:
```csharp
                session.OnDisconnected = async () =>
                {
                    _registrar?.Unregister(session.SessionId);
                    _activeSessions.TryRemove(session.SessionId, out _);
                    session.TransitionTo(SessionState.Disconnected);
                    if (OnClientDisconnected != null)
                        await OnClientDisconnected(session);
                    await session.DisposeAsync();
                };
```

- [ ] **Step 3: 빌드 및 테스트 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln && dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Build succeeded.` + `Passed! - Failed: 0, Passed: 24`

- [ ] **Step 4: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineListener.cs
git commit -m "수정: SocketPipelineListener 연결/해제 시 세션 상태 자동 전환"
```

---

## Task 5: Server/Program.cs 예제 업데이트

**Files:**
- Modify: `Server/Program.cs`

- [ ] **Step 1: using 추가 (필요 시)**

`SessionState`는 `ServerLib.Interface` 네임스페이스에 있다. `Server/Program.cs` 상단 `using` 블록에 다음이 없으면 추가한다 (이미 있으면 건너뛴다):

```csharp
using ServerLib.Interface;
```

- [ ] **Step 2: OnClientConnected 콜백 교체 (Context 할당 + State 출력)**

`Server/Program.cs`의 기존 `listener.OnClientConnected = session => { ... };` 블록 전체를 다음으로 교체한다:

```csharp
listener.OnClientConnected = session =>
{
    session.Context = new GameContext(PlayerId: 1001, Nickname: "홍길동");
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  state={session.State}  (sessions: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};
```

- [ ] **Step 3: GameContext record를 파일 최하단에 추가**

top-level statements에서는 모든 타입 선언이 실행문 뒤(파일 끝)에 와야 한다. `Server/Program.cs`의 **맨 마지막 줄**(마지막 `Console.WriteLine(...)` 다음)에 추가한다:

```csharp

// 세션에 부착할 커스텀 컨텍스트 예제
record GameContext(int PlayerId = 0, string Nickname = "Guest");
```

- [ ] **Step 4: 빌드 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\Server
```
Expected: `Build succeeded.`

- [ ] **Step 5: 커밋**

```bash
git add Server/Program.cs
git commit -m "수정: Server 예제에 GameContext 부착 및 세션 상태 출력 추가"
```

---

## Task 6: 최종 검증

- [ ] **Step 1: 전체 솔루션 빌드**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 2: 전체 테스트**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 24`

- [ ] **Step 3: 수동 동작 확인 (선택)**

터미널 1: `dotnet run --project Server`
터미널 2: `dotnet run --project Client -- 1 5`
Server 터미널에서 `[+] ... state=Connected` 로그 출력 확인.
