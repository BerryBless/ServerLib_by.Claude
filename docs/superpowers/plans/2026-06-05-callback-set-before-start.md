# 콜백 설정 안전성 강화 (E3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 세션/리스너/클라이언트의 콜백을 Start/Connect 이후 설정하면 `InvalidOperationException`을 던지도록 setter 가드를 추가한다(기존 `IdleTimeout` 가드 패턴 확장).

**Architecture:** 인터페이스 시그니처(`Func<…>? { get; set; }`)와 단일-구독·`=` 할당 모델을 유지하고, 3개 구현체의 콜백 setter를 백킹필드 + 가드로 바꾼다. 가드는 "쓰기를 start 이전으로 한정"하여 가시성 fragility도 제거한다.

**Tech Stack:** .NET 10, C# 13, xUnit. 소비자 코드(Program.cs)는 이미 start 전 설정이라 변경 없음.

**참고:** 이 저장소는 Stop 훅이 `.git/auto_commit_msg.txt`로 자동 커밋·푸시한다. 인라인 실행 시 각 Task의 `git commit`은 생략 가능(턴 종료 전 커밋 메시지 파일 작성으로 대체).

**스펙:** `docs/superpowers/specs/2026-06-05-callback-set-before-start-design.md`

---

## File Structure

- **Modify** `ServerLib/Core/Transport/SocketPipelineListener.cs` — 콜백 4개 가드 setter (`IsRunning` 기준).
- **Modify** `ServerLib/Core/Transport/SocketPipelineClient.cs` — `_started` 플래그 + 콜백 3개 가드 setter.
- **Modify** `ServerLib/Core/Transport/SocketPipelineSession.cs` — `_receiving` 플래그 + 콜백 2개 가드 setter.
- **Modify** `ServerLib/Interface/{IServerListener,IClientConnection,ISession}.cs` — On* XML 문서에 "start 전 설정" 명시.
- **Create** `ServerLib.Tests/CallbackGuardTests.cs` — start 이후 콜백 설정 시 throw 검증 3종.

비변경: `Server/Program.cs`·`Client/Program.cs`(이미 start 전 설정), `StubSession`, 설정 프로퍼티(`SendTimeout`/`PingInterval`/`IdleTimeout`).

---

## Task 1: Listener 콜백 가드

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineListener.cs`
- Test: `ServerLib.Tests/CallbackGuardTests.cs` (신규)

- [ ] **Step 1: 실패하는 테스트 작성**

Create `ServerLib.Tests/CallbackGuardTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Transport;
using Xunit;

namespace ServerLib.Tests;

/// <summary>E3: 콜백을 Start/Connect/StartReceiving 이후 설정하면 InvalidOperationException.</summary>
public sealed class CallbackGuardTests
{
    // 연결된 loopback 소켓 쌍(세션 테스트용)
    private static (Socket server, Socket client) CreateConnectedPair()
    {
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        var port = ((IPEndPoint)l.LocalEndPoint!).Port;
        var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        c.Connect(IPAddress.Loopback, port);
        var s = l.Accept();
        return (s, c);
    }

    [Fact]
    public void Listener_SetCallbackAfterStart_Throws()
    {
        var listener = new SocketPipelineListener();
        listener.Start(0); // 0 = OS가 임시 포트 할당, accept 루프 시작
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => listener.OnReceived = (_, _) => ValueTask.CompletedTask);
            Assert.Throws<InvalidOperationException>(
                () => listener.OnClientConnected = _ => ValueTask.CompletedTask);
        }
        finally { listener.Stop(); }
    }
}
```

- [ ] **Step 2: 테스트가 실패(Red)하는지 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~CallbackGuardTests.Listener_SetCallbackAfterStart_Throws"`
Expected: FAIL — `Assert.Throws() Failure: No exception was thrown` (현재 auto-property라 throw 안 함).

- [ ] **Step 3: Listener 콜백 가드 구현**

`SocketPipelineListener.cs`에서 콜백 4개 선언부를 아래로 교체한다.

기존:
```csharp
    public bool IsRunning => _listenSocket != null;
    public Func<ISession, ValueTask>? OnClientConnected { get; set; }
    public Func<ISession, ValueTask>? OnClientDisconnected { get; set; }
    public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
```

변경:
```csharp
    public bool IsRunning => _listenSocket != null;

    private Func<ISession, ValueTask>? _onClientConnected;
    public Func<ISession, ValueTask>? OnClientConnected
    {
        get => _onClientConnected;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnClientConnected는 Start() 호출 전에만 설정할 수 있습니다.");
            _onClientConnected = value;
        }
    }

    private Func<ISession, ValueTask>? _onClientDisconnected;
    public Func<ISession, ValueTask>? OnClientDisconnected
    {
        get => _onClientDisconnected;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnClientDisconnected는 Start() 호출 전에만 설정할 수 있습니다.");
            _onClientDisconnected = value;
        }
    }

    private Func<ISession, ReadOnlyMemory<byte>, ValueTask>? _onReceived;
    public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived
    {
        get => _onReceived;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnReceived는 Start() 호출 전에만 설정할 수 있습니다.");
            _onReceived = value;
        }
    }
```

그리고 `OnIdleTimeout` 선언부를 교체한다.

기존:
```csharp
    public Func<ISession, ValueTask>? OnIdleTimeout { get; set; }
```

변경:
```csharp
    private Func<ISession, ValueTask>? _onIdleTimeout;
    public Func<ISession, ValueTask>? OnIdleTimeout
    {
        get => _onIdleTimeout;
        set
        {
            if (IsRunning) throw new InvalidOperationException("OnIdleTimeout은 Start() 호출 전에만 설정할 수 있습니다.");
            _onIdleTimeout = value;
        }
    }
```

(`IdleTimeout`은 이미 동일 가드 보유 — 그대로 둔다.)

- [ ] **Step 4: 테스트 통과(Green) 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~CallbackGuardTests.Listener_SetCallbackAfterStart_Throws"`
Expected: PASS.

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineListener.cs ServerLib.Tests/CallbackGuardTests.cs
git commit -m "추가: Listener 콜백 Start 이후 설정 차단 가드 (E3)"
```

---

## Task 2: Client 콜백 가드

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineClient.cs`
- Test: `ServerLib.Tests/CallbackGuardTests.cs` (테스트 추가)

- [ ] **Step 1: 실패하는 테스트 추가**

`CallbackGuardTests` 클래스에 메서드를 추가한다:

```csharp
    [Fact]
    public async Task Client_SetCallbackAfterConnect_Throws()
    {
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        l.Listen(1);
        var port = ((IPEndPoint)l.LocalEndPoint!).Port;

        await using var client = new SocketPipelineClient();
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port);
        using var serverSide = l.Accept();

        Assert.Throws<InvalidOperationException>(
            () => client.OnReceived = _ => ValueTask.CompletedTask);
    }
```

- [ ] **Step 2: 테스트가 실패(Red)하는지 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~CallbackGuardTests.Client_SetCallbackAfterConnect_Throws"`
Expected: FAIL — `No exception was thrown`.

- [ ] **Step 3: Client 콜백 가드 구현**

(a) `SocketPipelineClient.cs` 필드 영역에 `_started` 추가. 기존:
```csharp
    private int _disposed;
```
변경:
```csharp
    private int _disposed;
    private bool _started; // ConnectAsync 진입 후 true — 콜백 재설정 차단용(단일 셋업 스레드에서만 접근)
```

(b) 콜백 3개 선언부 교체. 기존:
```csharp
    public Func<ValueTask>? OnConnected { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
```
변경:
```csharp
    private Func<ValueTask>? _onConnected;
    public Func<ValueTask>? OnConnected
    {
        get => _onConnected;
        set
        {
            if (_started) throw new InvalidOperationException("OnConnected는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
            _onConnected = value;
        }
    }

    private Func<ValueTask>? _onDisconnected;
    public Func<ValueTask>? OnDisconnected
    {
        get => _onDisconnected;
        set
        {
            if (_started) throw new InvalidOperationException("OnDisconnected는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
            _onDisconnected = value;
        }
    }

    private Func<ReadOnlyMemory<byte>, ValueTask>? _onReceived;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived
    {
        get => _onReceived;
        set
        {
            if (_started) throw new InvalidOperationException("OnReceived는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
            _onReceived = value;
        }
    }
```

(c) `ConnectAsync` 진입부에서 `_started` 설정. 기존:
```csharp
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
```
변경:
```csharp
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        _started = true; // 이후 콜백 재설정 차단

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
```

(주의: 코드 내부에서 `OnConnected`/`OnReceived`/`OnDisconnected`를 *읽는* 곳은 getter 경유이므로 변경 불필요. `ConnectAsync` 안에서 `if (OnConnected != null) await OnConnected();`는 getter를 읽어 정상 동작.)

- [ ] **Step 4: 테스트 통과(Green) 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~CallbackGuardTests.Client_SetCallbackAfterConnect_Throws"`
Expected: PASS.

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineClient.cs ServerLib.Tests/CallbackGuardTests.cs
git commit -m "추가: Client 콜백 ConnectAsync 이후 설정 차단 가드 (E3)"
```

---

## Task 3: Session 콜백 가드

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineSession.cs`
- Test: `ServerLib.Tests/CallbackGuardTests.cs` (테스트 추가)

- [ ] **Step 1: 실패하는 테스트 추가**

`CallbackGuardTests` 클래스에 메서드를 추가한다:

```csharp
    [Fact]
    public async Task Session_SetCallbackAfterStartReceiving_Throws()
    {
        var (server, client) = CreateConnectedPair();
        await using var session = new SocketPipelineSession(server);
        session.StartReceiving();

        Assert.Throws<InvalidOperationException>(
            () => session.OnReceived = _ => ValueTask.CompletedTask);

        client.Dispose();
    }
```

- [ ] **Step 2: 테스트가 실패(Red)하는지 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~CallbackGuardTests.Session_SetCallbackAfterStartReceiving_Throws"`
Expected: FAIL — `No exception was thrown`.

- [ ] **Step 3: Session 콜백 가드 구현**

(a) `SocketPipelineSession.cs` 필드 영역에 `_receiving` 추가. 기존:
```csharp
    private int _disposed;
```
변경:
```csharp
    private int _disposed;
    private bool _receiving; // StartReceiving 이후 true — 콜백 재설정 차단(라이브러리는 StartReceiving 전에 배선)
```

(b) 콜백 2개 선언부 교체. 기존:
```csharp
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
```
변경:
```csharp
    private Func<ReadOnlyMemory<byte>, ValueTask>? _onReceived;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived
    {
        get => _onReceived;
        set
        {
            if (_receiving) throw new InvalidOperationException("OnReceived는 StartReceiving() 호출 전에만 설정할 수 있습니다.");
            _onReceived = value;
        }
    }

    private Func<ValueTask>? _onDisconnected;
    public Func<ValueTask>? OnDisconnected
    {
        get => _onDisconnected;
        set
        {
            if (_receiving) throw new InvalidOperationException("OnDisconnected는 StartReceiving() 호출 전에만 설정할 수 있습니다.");
            _onDisconnected = value;
        }
    }
```

(c) `StartReceiving` 진입부에서 `_receiving` 설정. 기존:
```csharp
    public void StartReceiving()
    {
        // fill/read 두 루프는 각자 _cts로 수명·취소를 관리하므로 await 없이 분리 구동(fire-and-forget)해도 안전
        _ = FillPipeAsync(_cts.Token);
        _ = ReadPipeAsync(_cts.Token);
    }
```
변경:
```csharp
    public void StartReceiving()
    {
        _receiving = true; // 이후 콜백 재설정 차단(콜백은 StartReceiving 전에 배선되어야 함)
        // fill/read 두 루프는 각자 _cts로 수명·취소를 관리하므로 await 없이 분리 구동(fire-and-forget)해도 안전
        _ = FillPipeAsync(_cts.Token);
        _ = ReadPipeAsync(_cts.Token);
    }
```

(주의: `ReadPipeAsync`/`DispatchPacketAsync`의 `OnDisconnected`/`OnReceived` 읽기는 getter 경유이므로 정상. `SocketPipelineListener.AcceptLoopAsync`는 `session.OnReceived`/`OnDisconnected`를 `StartReceiving` *이전*에 배선하므로 가드에 걸리지 않는다.)

- [ ] **Step 4: 테스트 통과(Green) 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~CallbackGuardTests.Session_SetCallbackAfterStartReceiving_Throws"`
Expected: PASS.

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineSession.cs ServerLib.Tests/CallbackGuardTests.cs
git commit -m "추가: Session 콜백 StartReceiving 이후 설정 차단 가드 (E3)"
```

---

## Task 4: 인터페이스 문서 + 전체 회귀

**Files:**
- Modify: `ServerLib/Interface/IServerListener.cs`, `IClientConnection.cs`, `ISession.cs`

- [ ] **Step 1: 인터페이스 XML 문서에 "start 전 설정" 명시**

각 콜백 프로퍼티의 `<remarks>`에 한 줄(`<item>` 또는 `<br/>`)을 추가한다. 정확한 문구:

`IServerListener.cs`의 `OnClientConnected`·`OnClientDisconnected`·`OnReceived`·`OnIdleTimeout` 각 `<remarks>` 안:
```csharp
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="Start"/>() 호출 전에만 설정해야 합니다. 이후 설정 시 <see cref="InvalidOperationException"/>이 발생합니다.</description></item>
```
(목록 형태가 아닌 콜백은 `<remarks>` 말미에 `<br/><br/><b>[설정 시점]</b> Start() 전에만 설정 가능; 이후 InvalidOperationException.` 추가.)

`IClientConnection.cs`의 `OnConnected`·`OnDisconnected`·`OnReceived` 각 `<remarks>` 말미:
```csharp
    /// <br/><br/><b>[설정 시점]</b> <see cref="ConnectAsync"/>() 호출 전에만 설정 가능; 이후 <see cref="InvalidOperationException"/>.
```

`ISession.cs`의 `OnReceived`·`OnDisconnected` 각 `<remarks>` 말미:
```csharp
    /// <br/><br/><b>[설정 시점]</b> 세션 수신 시작 전에만 설정 가능; 이후 <see cref="InvalidOperationException"/>. (서버 라이브러리가 수신 시작 전에 배선합니다.)
```

- [ ] **Step 2: 전체 빌드·테스트로 회귀 확인**

Run: `dotnet test E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release`
Expected: PASS — 기존 49 + 신규 3 = 52개 통과, 실패 0. (기존 테스트·예제는 모두 start 전 콜백 설정이라 가드에 안 걸림.)

- [ ] **Step 3: 커밋**

```bash
git add ServerLib/Interface/IServerListener.cs ServerLib/Interface/IClientConnection.cs ServerLib/Interface/ISession.cs
git commit -m "문서: 콜백 설정 시점 제약(start 전) XML 문서 명시 (E3)"
```

---

## Self-Review

**Spec coverage:**
- Listener 콜백 4개 가드 → Task 1. ✓
- Client 콜백 3개 가드 + `_started` → Task 2. ✓
- Session 콜백 2개 가드 + `_receiving`(라이브러리 배선은 StartReceiving 전) → Task 3. ✓
- 인터페이스 문서 보강 → Task 4 Step 1. ✓
- 단일 구독·시그니처 유지, 소비자 코드 비변경 → 어느 Task도 인터페이스 시그니처/Program.cs를 바꾸지 않음. ✓
- 가시성: 가드로 쓰기를 start 전 한정 → 별도 volatile 없음(스펙 근거대로). ✓
- 테스트 3종 + 회귀 → Task 1~4. ✓
- 비목표(멀티구독·init-only·SendTimeout/PingInterval 가드) → 어느 Task도 해당 안 함. ✓

**Placeholder scan:** 모든 코드/명령 구체화됨. 플레이스홀더 없음.

**Type consistency:** 백킹필드명(`_onClientConnected` 등)·플래그(`_started`/`_receiving`)·예외 타입(`InvalidOperationException`)이 Task 전반 일관. getter는 모두 `=> _field` 형태로 통일. 테스트 헬퍼 `CreateConnectedPair`는 Task 1에서 정의되어 Task 3에서 재사용(동일 시그니처).
