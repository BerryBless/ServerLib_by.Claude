# TransitionTo 상태 소유권 문서화 (E5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `TransitionTo`/`SessionState` 문서에 "transport 생명주기 상태는 라이브러리 소유, 소비자는 `Authenticated`/`Custom`만 설정"이라는 소유권 규약을 명시한다.

**Architecture:** 순수 문서 변경. 코드·API·동작·CAS 가드 불변. `ISession.TransitionTo`와 `SessionState` predefined 필드 XML 문서, README 노트만 보강한다.

**Tech Stack:** .NET 10, C# 13 XML doc, xUnit(회귀용). 신규 테스트 없음(동작 변화 없음).

**참고:** 이 저장소는 Stop 훅이 `.git/auto_commit_msg.txt`로 자동 커밋·푸시한다. 인라인 실행 시 각 Task의 `git commit`은 생략 가능.

**스펙:** `docs/superpowers/specs/2026-06-05-transitionto-ownership-docs-design.md`

---

## File Structure

- **Modify** `ServerLib/Interface/ISession.cs` — `TransitionTo` `<remarks>`에 "[상태 소유권]" 절.
- **Modify** `ServerLib/Interface/SessionState.cs` — predefined 5개 상태 `<summary>`에 소유권 라벨.
- **Modify** `README.md` — `ISession` 절에 상태 소유권 노트.

비변경: 구현체(`SocketPipelineSession`/`StubSession`), 테스트, 동작, 시그니처, CAS 가드.

---

## Task 1: 인터페이스 XML 문서 보강

**Files:**
- Modify: `ServerLib/Interface/ISession.cs`
- Modify: `ServerLib/Interface/SessionState.cs`

- [ ] **Step 1: `ISession.TransitionTo`에 상태 소유권 절 추가**

`ISession.cs`에서 `TransitionTo`의 `<remarks>` 끝부분을 교체한다.

기존:
```csharp
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// </remarks>
    bool TransitionTo(SessionState newState);
```

변경:
```csharp
    /// <item><description><b>Blocking:</b> Non-blocking. 즉시 반환합니다.</description></item>
    /// </list>
    /// <b>[상태 소유권:]</b> transport 생명주기 상태(<see cref="SessionState.Connecting"/>·<see cref="SessionState.Connected"/>·
    /// <see cref="SessionState.Disconnecting"/>·<see cref="SessionState.Disconnected"/>)는 서버 라이브러리가 소유·구동합니다.
    /// 소비자가 이 상태로 직접 전환하면 보고 상태와 실제 소켓 상태가 어긋날 수 있습니다.
    /// 소비자는 <see cref="SessionState.Authenticated"/> 또는 <see cref="SessionState.Custom(int)"/>(앱 레벨) 상태만 설정하십시오.
    /// 하드 강제는 <see cref="SessionState.Disconnected"/> 부활 차단(CAS)뿐이며, 그 외는 규약입니다.
    /// </remarks>
    bool TransitionTo(SessionState newState);
```

- [ ] **Step 2: `SessionState` predefined 필드에 소유권 라벨 추가**

`SessionState.cs`에서 predefined 5개 필드 블록을 교체한다.

기존:
```csharp
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
```

변경:
```csharp
    /// <summary>연결 수립 중 (초기 상태). (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Connecting = new(0);
    /// <summary>연결 완료. (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Connected = new(1);
    /// <summary>인증 완료. (앱 레벨 — 소비자 설정 가능)</summary>
    public static readonly SessionState Authenticated = new(2);
    /// <summary>연결 해제 진행 중. (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Disconnecting = new(3);
    /// <summary>연결 해제 완료. (transport — 라이브러리 소유)</summary>
    public static readonly SessionState Disconnected = new(4);
```

- [ ] **Step 3: 빌드로 문서 cref 유효성 확인**

Run: `dotnet build E:\project\ClaudeCodeStudy\ServerLib\ServerLib.csproj -c Release`
Expected: 빌드 성공(오류 0). (`<see cref="SessionState.Custom(int)"/>` 등 cref이 유효 — 미해결 cref은 CS1574 경고이며 빌드는 통과하나, 경고도 없도록 멤버명을 정확히 맞춤.)

- [ ] **Step 4: 커밋**

```bash
git add ServerLib/Interface/ISession.cs ServerLib/Interface/SessionState.cs
git commit -m "문서: TransitionTo 상태 소유권 규약 명시 (E5)"
```

---

## Task 2: README 노트 + 회귀

**Files:**
- Modify: `README.md`

- [ ] **Step 1: README ISession 절에 소유권 노트 추가**

`README.md`의 `ISession` 절에 직전 사이클(E2)에서 추가한 컨텍스트 노트가 있다. 그 노트 바로 다음에 한 줄을 추가한다.

기존:
```markdown
> 컨텍스트 접근: `session.GetContext<T>()` / `session.TryGetContext<T>(out var ctx)` (`SessionContextExtensions`, `ServerLib.Core`) — `object? Context`를 캐스팅 없이 타입 안전하게 읽는다(미설정·불일치 시 default).
```

변경:
```markdown
> 컨텍스트 접근: `session.GetContext<T>()` / `session.TryGetContext<T>(out var ctx)` (`SessionContextExtensions`, `ServerLib.Core`) — `object? Context`를 캐스팅 없이 타입 안전하게 읽는다(미설정·불일치 시 default).

> 상태 소유권(`TransitionTo`): transport 생명주기 상태(`Connecting`/`Connected`/`Disconnecting`/`Disconnected`)는 라이브러리가 소유·구동한다. 소비자는 `Authenticated`/`Custom(≥5)` 앱 레벨 상태만 설정할 것(직접 transport 전환 시 보고 상태와 실제 소켓 상태 불일치). 하드 강제는 Disconnected 부활 차단(CAS)뿐.
```

- [ ] **Step 2: 전체 빌드·테스트로 회귀 확인**

Run: `dotnet test E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release`
Expected: PASS — 기존 52개 그대로 통과, 실패 0(동작 변화 없음). 빌드 오류·문서 cref 오류 0.

- [ ] **Step 3: 커밋**

```bash
git add README.md
git commit -m "문서: 세션 상태 소유권 README 노트 (E5)"
```

---

## Self-Review

**Spec coverage:**
- `ISession.TransitionTo` 상태 소유권 절 → Task 1 Step 1. ✓
- `SessionState` predefined 소유권 라벨 → Task 1 Step 2. ✓
- README 노트 → Task 2 Step 1. ✓
- 동작·시그니처·CAS 불변 → 어느 Step도 코드/구현체를 바꾸지 않음. ✓
- 검증: 빌드 cref + 기존 52 회귀 → Task 1 Step 3, Task 2 Step 2. ✓
- 비목표(transport 거부 코드·읽기전용화) → 어느 Step도 해당 안 함. ✓

**Placeholder scan:** 모든 문구·명령 구체화됨. 플레이스홀더 없음.

**Type consistency:** cref 대상(`SessionState.Connecting`/`Connected`/`Authenticated`/`Disconnecting`/`Disconnected`/`Custom(int)`)은 `SessionState.cs`의 실제 멤버명과 일치. README 라벨 문구와 XML 문구가 동일 의미로 일관.
