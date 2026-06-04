# ISession.Context 타입 안전 접근 (E2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ISession.Context`(object?)를 캐스팅 없이 타입 안전하게 읽는 확장 메서드 `GetContext<T>()`/`TryGetContext<T>()`를 추가한다.

**Architecture:** 인터페이스·레지스트리·콜백·구현체를 일절 바꾸지 않고, 기존 `object? Context`(Volatile read/write) 위에 얇은 캐스팅 확장 메서드를 `ServerLib.Core`에 추가한다. 직전 `PacketSendExtensions`와 동일한 확장-메서드 패턴.

**Tech Stack:** .NET 10, C# 13, xUnit. 의존성 방향 Core→Interface 준수.

**참고:** 이 저장소는 Stop 훅(`auto-commit.ps1`)이 `.git/auto_commit_msg.txt`를 읽어 자동 커밋·푸시한다. 인라인 실행 시 각 Task의 `git commit` 단계는 생략 가능하며, 대신 턴 종료 전 커밋 메시지 파일을 작성하면 된다. 아래 커밋 단계는 수동 실행 기준으로 명시한다.

**스펙:** `docs/superpowers/specs/2026-06-05-session-context-typed-access-design.md`

---

## File Structure

- **Create** `ServerLib/Core/SessionContextExtensions.cs` — `ISession` 확장 메서드 2개(`GetContext<T>`, `TryGetContext<T>`). 책임: 타입 안전 컨텍스트 읽기 사탕(sugar).
- **Create** `ServerLib.Tests/SessionContextExtensionsTests.cs` — 위 확장의 동작 테스트.
- **Modify** `Server/Program.cs` — 설정한 `GameContext`를 `GetContext<GameContext>()`로 되읽어 새 API 시연.
- **Modify** `README.md` — `ISession` 절에 헬퍼 한 줄 노트.

비변경: `ISession`/`IServerListener`/`ISessionRegistry`/`ISessionRegistrar`, `SocketPipelineSession`, `StubSession`, 모든 콜백.

---

## Task 1: SessionContextExtensions (확장 메서드 + 테스트)

**Files:**
- Create: `ServerLib/Core/SessionContextExtensions.cs`
- Test: `ServerLib.Tests/SessionContextExtensionsTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

Create `ServerLib.Tests/SessionContextExtensionsTests.cs`:

```csharp
using ServerLib.Core;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

/// <summary>E2: ISession.Context 타입 안전 접근 확장 메서드 검증.</summary>
public sealed class SessionContextExtensionsTests
{
    private sealed record TestContext(int Id, string Name);

    [Fact]
    public void GetContext_WhenSetToMatchingType_ReturnsSameInstance()
    {
        var session = new StubSession();
        var ctx = new TestContext(7, "neo");
        session.Context = ctx;

        var result = session.GetContext<TestContext>();

        Assert.Same(ctx, result);
    }

    [Fact]
    public void GetContext_WhenContextNull_ReturnsDefault()
    {
        var session = new StubSession(); // Context 미설정(null)
        Assert.Null(session.GetContext<TestContext>());
    }

    [Fact]
    public void GetContext_WhenContextIsDifferentType_ReturnsDefault()
    {
        var session = new StubSession { Context = "a string" };
        Assert.Null(session.GetContext<TestContext>());
    }

    [Fact]
    public void TryGetContext_WhenMatchingType_ReturnsTrueAndValue()
    {
        var session = new StubSession();
        var ctx = new TestContext(1, "a");
        session.Context = ctx;

        bool ok = session.TryGetContext<TestContext>(out var value);

        Assert.True(ok);
        Assert.Same(ctx, value);
    }

    [Fact]
    public void TryGetContext_WhenNullOrMismatch_ReturnsFalseAndDefault()
    {
        var session = new StubSession(); // null context
        bool ok = session.TryGetContext<TestContext>(out var value);

        Assert.False(ok);
        Assert.Null(value);
    }
}
```

- [ ] **Step 2: 테스트가 컴파일 실패(Red)하는지 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~SessionContextExtensionsTests"`
Expected: 빌드 실패 — `'ISession'에는 'GetContext'/'TryGetContext'에 대한 정의가 없음`(확장 메서드 미존재).

- [ ] **Step 3: 확장 메서드 구현**

Create `ServerLib/Core/SessionContextExtensions.cs`:

```csharp
using ServerLib.Interface;

namespace ServerLib.Core;

/// <summary>
/// <see cref="ISession.Context"/>(object?)를 캐스팅 없이 타입 안전하게 읽는 확장 메서드입니다.
/// </summary>
/// <remarks>
/// <b>[레이어]</b> 확장 메서드라 <see cref="ISession"/> 구현체를 바꾸지 않으며, 의존성 방향(Core→Interface)을 지킨다.
/// <b>[Thread Safety]</b> 기반 <see cref="ISession.Context"/>의 Volatile read를 그대로 통과(Thread-safe).
/// <b>[Memory Allocation]</b> 캐스팅만 수행(참조 타입 Zero-allocation). 값 타입 컨텍스트는 기존 object? 저장 모델상 박싱이 유지된다.
/// <b>[Blocking]</b> Non-blocking.
/// </remarks>
public static class SessionContextExtensions
{
    /// <summary>세션 컨텍스트를 <typeparamref name="T"/>로 읽습니다. 미설정이거나 타입이 다르면 <c>default</c>를 반환합니다(예외 없음).</summary>
    /// <typeparam name="T">기대하는 컨텍스트 타입.</typeparam>
    /// <param name="session">대상 세션.</param>
    /// <returns><see cref="ISession.Context"/>가 <typeparamref name="T"/>이면 그 값, 아니면 <c>default</c>.</returns>
    public static T? GetContext<T>(this ISession session)
        => session.Context is T t ? t : default;

    /// <summary>세션 컨텍스트가 <typeparamref name="T"/>로 존재하는지 확인합니다. "미설정"과 "기본값(default)"을 구분해야 할 때 사용합니다.</summary>
    /// <typeparam name="T">기대하는 컨텍스트 타입.</typeparam>
    /// <param name="session">대상 세션.</param>
    /// <param name="value">존재하면 컨텍스트 값, 아니면 <c>default</c>.</param>
    /// <returns>컨텍스트가 <typeparamref name="T"/>이면 <see langword="true"/>, 아니면 <see langword="false"/>.</returns>
    public static bool TryGetContext<T>(this ISession session, out T value)
    {
        if (session.Context is T t) { value = t; return true; }
        value = default!;
        return false;
    }
}
```

- [ ] **Step 4: 테스트 통과(Green) 확인**

Run: `dotnet test ServerLib.Tests --filter "FullyQualifiedName~SessionContextExtensionsTests"`
Expected: PASS — 5개 통과.

- [ ] **Step 5: 커밋**

```bash
git add ServerLib/Core/SessionContextExtensions.cs ServerLib.Tests/SessionContextExtensionsTests.cs
git commit -m "추가: ISession.Context 타입 안전 접근 확장(GetContext/TryGetContext) (E2)"
```

---

## Task 2: 예제·문서에 새 API 시연

**Files:**
- Modify: `Server/Program.cs` (OnClientDisconnected 콜백)
- Modify: `README.md` (`ISession` 절)

- [ ] **Step 1: Server 예제에서 GetContext로 되읽기**

`Server/Program.cs`의 `OnClientDisconnected` 콜백을 아래로 교체한다. (현재는 `metrics?.OnClientDisconnected();` + `Console.WriteLine($"[-] {session.RemoteEndPoint} ...")` 형태.)

기존:
```csharp
listener.OnClientDisconnected = session =>
{
    metrics?.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (sessions: {metrics?.ConnectedCount ?? 0})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};
```

변경:
```csharp
listener.OnClientDisconnected = session =>
{
    metrics?.OnClientDisconnected();
    // E2: 부착해 둔 컨텍스트를 캐스팅 없이 타입 안전하게 되읽는다.
    var nick = session.GetContext<GameContext>()?.Nickname ?? "?";
    Console.WriteLine($"[-] {session.RemoteEndPoint}  nick={nick}  (sessions: {metrics?.ConnectedCount ?? 0})  test={Volatile.Read(ref test)}");
    return ValueTask.CompletedTask;
};
```
(`Server/Program.cs`는 이미 `using ServerLib.Core;`라 `GetContext`가 보인다. `GameContext` record는 파일 하단에 이미 정의되어 있다.)

- [ ] **Step 2: README에 헬퍼 노트 추가**

`README.md`의 `### \`ISession\` — 클라이언트 세션` 표 아래(현재 `SendAsync` 편의 오버로드 노트와 같은 위치)에 한 줄 추가:

```markdown
> 컨텍스트 접근: `session.GetContext<T>()` / `session.TryGetContext<T>(out var ctx)` (`SessionContextExtensions`, `ServerLib.Core`) — `object? Context`를 캐스팅 없이 타입 안전하게 읽는다(미설정·불일치 시 default).
```

- [ ] **Step 3: 전체 빌드·테스트로 회귀 확인**

Run: `dotnet test E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release`
Expected: PASS — 기존 44 + 신규 5 = 49개 통과, 실패 0.

- [ ] **Step 4: 커밋**

```bash
git add Server/Program.cs README.md
git commit -m "문서: Context 타입 안전 접근(E2) 예제·README 반영"
```

---

## Self-Review

**Spec coverage:**
- 핵심 API(GetContext/TryGetContext) → Task 1. ✓
- 관대한 시맨틱(default/예외 없음) → Step 1 테스트 + Step 3 구현. ✓
- 파급 0(인터페이스·레지스트리·구현체 비변경) → 어느 Task도 해당 파일을 건드리지 않음. ✓
- 위치 `ServerLib.Core` → Task 1 Step 3. ✓
- 테스트 4종(스펙) → Task 1에 5개(미설정/불일치/일치 + TryGet 2종)로 충족·초과. ✓
- 예제 시연 + README → Task 2. ✓
- 범위 외 `IClientConnection`(Context 없음) → 어느 Task도 손대지 않음. ✓

**Placeholder scan:** 모든 코드/명령 구체화됨. 플레이스홀더 없음.

**Type consistency:** `GetContext<T>() : T?`, `TryGetContext<T>(out T) : bool` — Task 1과 Task 2 사용처(`GetContext<GameContext>()?.Nickname`) 일치. `TestContext`는 테스트 로컬 record로 정의·사용 일관.
