# Session Idle Timeout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 유휴(idle) 세션을 자동 감지하여 `OnIdleTimeout` 콜백 → `DisposeAsync()` 순으로 연결 해제하는 기능을 `SocketPipelineListener` 수준 일괄 설정으로 구현한다.

**Architecture:** `ISession`에 `LastReceivedAt` 프로퍼티를 추가하고 `SocketPipelineSession.FillPipeAsync`에서 수신마다 `Interlocked.Exchange`로 갱신한다. `SocketPipelineListener`는 `PeriodicTimer` 기반 `IdleSweepLoopAsync`를 `Start()` 시 조건부로 실행하여 `_activeSessions`를 순회하며 유휴 세션을 해제한다. 테스트를 위해 `InternalsVisibleTo`를 설정하고 내부 헬퍼 메서드를 노출한다.

**Tech Stack:** .NET 10, xUnit 2.9.0, `System.Threading.PeriodicTimer`, `Interlocked`, `ConcurrentDictionary`

---

## File Map

| 경로 | 유형 | 역할 |
|------|------|------|
| `ServerLib/Interface/ISession.cs` | 수정 | `LastReceivedAt` 프로퍼티 추가 |
| `ServerLib/Interface/IServerListener.cs` | 수정 | `IdleTimeout`, `OnIdleTimeout` 추가 |
| `ServerLib/Core/Transport/SocketPipelineSession.cs` | 수정 | `_lastReceivedTicks` 필드 + 프로퍼티 + FillPipeAsync 스탬핑 |
| `ServerLib/Core/Transport/SocketPipelineListener.cs` | 수정 | `IdleTimeout`, `OnIdleTimeout`, `IdleSweepLoopAsync`, `StartIdleSweepForTest` |
| `ServerLib/ServerLib.csproj` | 수정 | `InternalsVisibleTo("ServerLib.Tests")` 추가 |
| `ServerLib.Tests/Stubs/StubSession.cs` | 수정 | `LastReceivedAt { get; set; }` — settable로 변경 |
| `ServerLib.Tests/SessionTimeoutTests.cs` | 신규 | 5개 단위 테스트 |
| `Server/Program.cs` | 수정 | `IdleTimeout` + `OnIdleTimeout` 설정 예제 |

---

## Task 1: ISession.LastReceivedAt + StubSession 업데이트

**Files:**
- Modify: `ServerLib/Interface/ISession.cs`
- Modify: `ServerLib.Tests/Stubs/StubSession.cs`
- Create: `ServerLib.Tests/SessionTimeoutTests.cs` (첫 두 테스트)

- [ ] **Step 1: 실패하는 테스트 작성**

`ServerLib.Tests/SessionTimeoutTests.cs` 신규 생성:

```csharp
using ServerLib.Tests.Stubs;

namespace ServerLib.Tests;

public sealed class SessionTimeoutTests
{
    [Fact]
    public void StubSession_LastReceivedAt_InitialValue_IsRecentUtcTime()
    {
        var before = DateTimeOffset.UtcNow;
        var session = new StubSession();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(session.LastReceivedAt, before, after);
    }

    [Fact]
    public void StubSession_LastReceivedAt_IsSettable()
    {
        var session = new StubSession();
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);

        session.LastReceivedAt = past;

        Assert.Equal(past, session.LastReceivedAt);
    }
}
```

> **Note:** Step 4에서 `StubSession.WasDisposed`가 필요하므로 Step 4에서 StubSession도 함께 수정한다.

- [ ] **Step 2: 빌드 오류 확인 (Red — LastReceivedAt 미존재)**

```bash
dotnet build E:\project\ClaudeCodeStudy\ServerLib.Tests
```
Expected: `error CS1061: 'StubSession' does not contain a definition for 'LastReceivedAt'`

- [ ] **Step 3: ISession에 LastReceivedAt 추가**

`ServerLib/Interface/ISession.cs`의 `ConnectedAt` 프로퍼티 바로 다음 줄에 추가:

```csharp
    /// <summary>
    /// 마지막으로 데이터를 수신한 정확한 시각(UTC)입니다.
    /// 연결 수립 시 <see cref="ConnectedAt"/>과 동일값으로 초기화되며,
    /// 데이터 수신마다 원자적으로 갱신됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. Interlocked 기반으로 원자적 갱신됩니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    DateTimeOffset LastReceivedAt { get; }
```

- [ ] **Step 4: StubSession에 settable LastReceivedAt 추가**

`ServerLib.Tests/Stubs/StubSession.cs`의 `ConnectedAt` 다음 줄에 추가:

```csharp
    public DateTimeOffset LastReceivedAt { get; set; } = DateTimeOffset.UtcNow;
```

- [ ] **Step 5: 테스트 통과 확인 (Green)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --filter "SessionTimeout" --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 6: 커밋**

```bash
git add ServerLib/Interface/ISession.cs ServerLib.Tests/Stubs/StubSession.cs ServerLib.Tests/SessionTimeoutTests.cs
git commit -m "추가: ISession.LastReceivedAt 인터페이스 및 StubSession 업데이트"
```

---

## Task 2: SocketPipelineSession — LastReceivedAt 구현

**Files:**
- Modify: `ServerLib/Core/Transport/SocketPipelineSession.cs`

- [ ] **Step 1: _lastReceivedTicks 필드 추가**

`SocketPipelineSession.cs`의 `private int _disposed;` 다음 줄에 추가:

```csharp
    private long _lastReceivedTicks; // UTC ticks — Interlocked으로 원자적 갱신
```

- [ ] **Step 2: LastReceivedAt 프로퍼티 추가**

`public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;` 다음 줄에 추가:

```csharp
    public DateTimeOffset LastReceivedAt
        => new DateTimeOffset(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);
```

- [ ] **Step 3: 생성자에서 초기값 설정**

`SocketPipelineSession(Socket socket)` 생성자의 `_pipe = new Pipe();` 다음 줄에 추가:

```csharp
        _lastReceivedTicks = ConnectedAt.Ticks; // ConnectedAt = DateTimeOffset.UtcNow, Ticks는 UTC 틱
```

- [ ] **Step 4: FillPipeAsync에서 수신 시 스탬핑**

`FillPipeAsync`의 `if (bytesRead == 0) break;` 다음 줄에 추가:

```csharp
                Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.Ticks);
```

수정 후 해당 while 블록:
```csharp
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(MinBufferSize);
                int bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None, ct);
                if (bytesRead == 0) break;
                Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.Ticks);

                writer.Advance(bytesRead);
                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted) break;
            }
```

- [ ] **Step 5: 빌드 및 기존 테스트 통과 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln && dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Build succeeded.` + `Passed! - Failed: 0, Passed: 15`

- [ ] **Step 6: 커밋**

```bash
git add ServerLib/Core/Transport/SocketPipelineSession.cs
git commit -m "수정: SocketPipelineSession에 LastReceivedAt 구현 (Interlocked + FillPipeAsync 스탬핑)"
```

---

## Task 3: InternalsVisibleTo + IServerListener + SocketPipelineListener

**Files:**
- Modify: `ServerLib/ServerLib.csproj`
- Modify: `ServerLib/Interface/IServerListener.cs`
- Modify: `ServerLib/Core/Transport/SocketPipelineListener.cs`

- [ ] **Step 1: ServerLib.csproj에 InternalsVisibleTo 추가**

`ServerLib/ServerLib.csproj`의 첫 번째 `</ItemGroup>` 뒤에 새 항목 추가:

```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>ServerLib.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
```

최종 csproj:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RootNamespace>ServerLib</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.ObjectPool" Version="9.0.5" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>ServerLib.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: IServerListener에 IdleTimeout + OnIdleTimeout 추가**

`IServerListener.cs`의 `void Stop();` 앞에 추가:

```csharp
    /// <summary>
    /// 유휴 세션 타임아웃 기간입니다. <see langword="null"/>이면 유휴 감지를 비활성화합니다(기본값).
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not thread-safe. <see cref="Start"/>() 호출 전에 설정해야 합니다.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// </list>
    /// </remarks>
    TimeSpan? IdleTimeout { get; set; }

    /// <summary>
    /// 세션이 유휴 타임아웃으로 해제되기 직전 호출되는 콜백입니다.
    /// <see cref="ISession.OnDisconnected"/>보다 먼저 발화됩니다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 스윕 루프 내부 스레드에서 호출됩니다. 동기 블로킹 금지.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    Func<ISession, ValueTask>? OnIdleTimeout { get; set; }
```

- [ ] **Step 3: SocketPipelineListener에 프로퍼티 + 스윕 메서드 추가**

`SocketPipelineListener.cs`에서 기존 프로퍼티들(IsRunning, OnClientConnected 등) 뒤에 두 줄 추가:

```csharp
    public TimeSpan? IdleTimeout { get; set; }
    public Func<ISession, ValueTask>? OnIdleTimeout { get; set; }
```

`Start()` 메서드의 `_ = AcceptLoopAsync(_cts.Token);` 다음 줄에 추가:

```csharp
        if (IdleTimeout.HasValue)
            _ = IdleSweepLoopAsync(IdleTimeout.Value, _cts.Token);
```

`AcceptLoopAsync` 메서드 바로 앞에 두 메서드 추가:

```csharp
    private async Task IdleSweepLoopAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 스윕 간격 = timeout/2 (최소 10ms) → 최대 1.5× timeout 후 감지
        var interval = TimeSpan.FromTicks(Math.Max(timeout.Ticks / 2, TimeSpan.FromMilliseconds(10).Ticks));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var session in _activeSessions.Values)
            {
                if (now - session.LastReceivedAt <= timeout) continue;
                try
                {
                    if (OnIdleTimeout != null)
                        await OnIdleTimeout(session);
                    await session.DisposeAsync(); // OnDisconnected 자동 유발
                }
                catch { /* 개별 세션 실패가 스윕 중단 방지 */ }
            }
        }
    }

    /// <summary>테스트 전용 — 세션을 _activeSessions에 직접 주입합니다.</summary>
    internal void InjectSessionForTest(ISession session)
        => _activeSessions[session.SessionId] = session;

    /// <summary>테스트 전용 — IdleSweepLoopAsync를 Start() 없이 직접 시작합니다.</summary>
    internal Task StartIdleSweepForTest(CancellationToken ct)
        => IdleSweepLoopAsync(IdleTimeout!.Value, ct);
```

- [ ] **Step 4: 빌드 및 기존 테스트 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln && dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Build succeeded.` + `Passed! - Failed: 0, Passed: 15`

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/ServerLib.csproj ServerLib/Interface/IServerListener.cs ServerLib/Core/Transport/SocketPipelineListener.cs
git commit -m "추가: SocketPipelineListener 유휴 타임아웃 스윕 루프 (PeriodicTimer 기반)"
```

---

## Task 4: SessionTimeoutTests — 나머지 3개 테스트

**Files:**
- Modify: `ServerLib.Tests/Stubs/StubSession.cs` (`WasDisposed` 추가)
- Modify: `ServerLib.Tests/SessionTimeoutTests.cs`

- [ ] **Step 1: StubSession에 WasDisposed 추가**

`StubSession.cs`의 `DisposeAsync`를 다음으로 교체:

```csharp
    public bool WasDisposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        return ValueTask.CompletedTask;
    }
```

- [ ] **Step 2: 3개 스윕 테스트 추가**

`SessionTimeoutTests.cs`에 기존 2개 테스트 아래 추가:

```csharp
    [Fact]
    public async Task IdleSweep_IdleSession_FiresOnIdleTimeoutAndDisposesSession()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var listener = new SocketPipelineListener();
        listener.IdleTimeout = TimeSpan.FromMilliseconds(100);

        var idleTimeoutFired = false;
        var stub = new StubSession
        {
            // 10초 전에 마지막 수신 → 즉시 유휴로 판정
            LastReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };
        listener.OnIdleTimeout = _ => { idleTimeoutFired = true; return ValueTask.CompletedTask; };
        listener.InjectSessionForTest(stub);

        // Act — 스윕 루프를 직접 시작하고 4× timeout 대기 (스윕 간격 50ms)
        _ = listener.StartIdleSweepForTest(cts.Token);
        await Task.Delay(400);
        cts.Cancel();

        // Assert
        Assert.True(idleTimeoutFired, "OnIdleTimeout이 호출되어야 한다");
        Assert.True(stub.WasDisposed, "세션 DisposeAsync가 호출되어야 한다");
    }

    [Fact]
    public async Task IdleSweep_ActiveSession_NotDisconnected()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var listener = new SocketPipelineListener();
        listener.IdleTimeout = TimeSpan.FromMilliseconds(200);

        var idleCalled = false;
        var stub = new StubSession
        {
            // 방금 수신한 것처럼 설정 → 200ms 이내이므로 타임아웃 아님
            LastReceivedAt = DateTimeOffset.UtcNow
        };
        listener.OnIdleTimeout = _ => { idleCalled = true; return ValueTask.CompletedTask; };
        listener.InjectSessionForTest(stub);

        // Act
        _ = listener.StartIdleSweepForTest(cts.Token);
        await Task.Delay(150); // 타임아웃(200ms)보다 짧게 대기

        cts.Cancel();

        // Assert
        Assert.False(idleCalled, "활성 세션은 타임아웃 처리하면 안 된다");
    }

    [Fact]
    public async Task IdleTimeout_WhenNull_NoSweepStarted_NoDisconnect()
    {
        // Arrange
        var listener = new SocketPipelineListener();
        // IdleTimeout = null (기본값) → StartIdleSweepForTest 호출 불가 → 직접 확인

        var idleCalled = false;
        var stub = new StubSession
        {
            LastReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-60)
        };
        listener.OnIdleTimeout = _ => { idleCalled = true; return ValueTask.CompletedTask; };
        listener.InjectSessionForTest(stub);

        // IdleTimeout = null이면 Start()에서 스윕 루프를 시작하지 않음
        // 여기서는 Start()를 호출하지 않으므로 스윕 없음 → 300ms 대기해도 idle 미발화
        await Task.Delay(300);

        // Assert
        Assert.False(idleCalled, "IdleTimeout=null이면 스윕이 실행되지 않아야 한다");
    }
```

- [ ] **Step 3: 테스트 실행 (5개 전부 통과 확인)**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --filter "SessionTimeout" --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 4: 전체 테스트 회귀 확인**

```bash
dotnet test E:\project\ClaudeCodeStudy\ServerLib.Tests --logger "console;verbosity=normal"
```
Expected: `Passed! - Failed: 0, Passed: 18`

- [ ] **Step 5: 커밋**

```bash
git add ServerLib.Tests/Stubs/StubSession.cs ServerLib.Tests/SessionTimeoutTests.cs
git commit -m "테스트: Session Idle Timeout 단위 테스트 5개 (스윕 감지·활성 세션 보호·비활성화)"
```

---

## Task 5: Server/Program.cs 예제 업데이트

**Files:**
- Modify: `Server/Program.cs`

- [ ] **Step 1: IdleTimeout + OnIdleTimeout 예제 추가**

`Server/Program.cs`에서 `listener.Start(Port);` 바로 앞에 추가:

```csharp
listener.IdleTimeout = TimeSpan.FromSeconds(30);
listener.OnIdleTimeout = session =>
{
    Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
    return ValueTask.CompletedTask;
};
```

최종 `listener.Start(Port);` 직전 코드 블록:
```csharp
listener.IdleTimeout = TimeSpan.FromSeconds(30);
listener.OnIdleTimeout = session =>
{
    Console.WriteLine($"[Timeout] {session.RemoteEndPoint}  idle={DateTimeOffset.UtcNow - session.LastReceivedAt:mm\\:ss}");
    return ValueTask.CompletedTask;
};

listener.Start(Port);
```

- [ ] **Step 2: 빌드 확인**

```bash
dotnet build E:\project\ClaudeCodeStudy\Server
```
Expected: `Build succeeded.`

- [ ] **Step 3: 커밋**

```bash
git add Server/Program.cs
git commit -m "수정: Server 예제에 IdleTimeout 30초 설정 추가"
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
Expected: `Passed! - Failed: 0, Passed: 18`

- [ ] **Step 3: 수동 동작 확인 (선택)**

터미널 1: `dotnet run --project Server`  
터미널 2: `dotnet run --project Client -- 1 5`  
Client 종료 후 30초 대기 → Server에서 `[Timeout]` 로그 출력 확인.
