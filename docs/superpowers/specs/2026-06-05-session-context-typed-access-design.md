# 설계: ISession.Context 타입 안전 접근 (E2)

**날짜:** 2026-06-05
**출처:** 성능 우선 코드 리뷰(`plan/perf_review_0604.md`) 권장 항목 **E2**
**상태:** 설계 승인됨 → 구현 계획 대기

## 배경 및 목적

`ISession.Context`는 사용자 정의 세션 상태(예: 플레이어 정보)를 세션에 부착하는 슬롯으로, 현재 `object?` 타입이다. 이로 인해 읽을 때 `(GameContext)session.Context` 캐스팅이 필요하다.

리뷰에서 E2(LOW)로 지적되었고, 탐색으로 다음을 확인했다:
- **읽는 프로덕션 코드는 현재 없음** — `Server/Program.cs`는 `session.Context = new GameContext(...)`로 *설정만* 하고 되읽지 않는다. 캐스팅 고통은 잠재적.
- **박싱도 현재 없음** — `GameContext`가 `record`(참조 타입). 박싱은 값 타입 컨텍스트를 넣을 때만 발생.
- **`ISession`은 11개 파일·42회 사용** — 리스너 콜백·레지스트리·브로드캐스트가 모두 비제네릭 `ISession`에 의존하며, 세션은 `Guid` 키 레지스트리에 *균일하게* 담긴다.

**목표:** 캐스팅 없이 타입 안전하게 컨텍스트를 *읽는* 편의를 제공하되, 세션 인프라(인터페이스·레지스트리·콜백) 파급은 0으로 한다.

**비목표(YAGNI):**
- `ISession<TContext>` 전면 제네릭화 — 레지스트리·브로드캐스트의 "이종 세션 균일 보관" 모델을 깨므로 LOW 항목에 비해 과도. 배제.
- 값 타입 박싱 제거 — 제네릭 저장이 필요해 세션 타입에 타입 파라미터가 생김. 현재 박싱이 실제로 없으므로 배제.
- `SetContext<T>` 헬퍼 — `session.Context = …`가 이미 자연스럽고 타입 추론됨. 배제.

## 설계 결정

| 항목 | 채택 | 대안(미채택) | 사유 |
|------|------|------------|------|
| 제공 형태 | ISession 확장 메서드 | 기본 인터페이스 메서드(DIM) | 직전 `PacketSendExtensions` 패턴과 일관, 구현체 미변경, DIM 버저닝 특성 회피 |
| 불일치/미설정 시맨틱 | 관대(default 반환, 예외 없음) | 엄격(throw) | "아직 미설정"이 흔한 정상 케이스 — 예외는 비편의 |
| 저장 모델 | 기존 `object?` 유지 | 제네릭 저장 | 인프라 파급 0, 박싱은 현재 비이슈 |
| 위치 | `ServerLib.Core` | `Core.Transport` | `Server/Program.cs`가 이미 `using ServerLib.Core;` → 예제에 자동 노출 |

## 컴포넌트 구조

```
ServerLib/
└─ Core/
   └─ SessionContextExtensions.cs   (신규) — static class, ISession 확장 2개
```
의존: `ServerLib.Interface`(ISession)만 참조. 의존성 방향 Core→Interface 준수. 레지스트리·리스너·직렬화와 무관.

## 핵심 API

```csharp
namespace ServerLib.Core;

public static class SessionContextExtensions
{
    // 미설정·타입 불일치 시 default 반환(예외 없음). 기존 Context(Volatile read) 위 캐스팅 → 스레드 안전.
    public static T? GetContext<T>(this ISession session)
        => session.Context is T t ? t : default;

    // 컨텍스트가 T로 존재하면 true. "미설정"과 "기본값(default)"을 구분해야 할 때 사용.
    public static bool TryGetContext<T>(this ISession session, out T value)
    {
        if (session.Context is T t) { value = t; return true; }
        value = default!;
        return false;
    }
}
```

사용 예:
```csharp
session.Context = new GameContext(PlayerId: 1001, Nickname: "홍길동"); // set은 기존 그대로
var nick = session.GetContext<GameContext>()?.Nickname;                // 캐스팅 없이 타입 안전 읽기
if (session.TryGetContext<GameContext>(out var ctx)) { /* ctx 사용 */ }
```

### 동작/엣지
- `Context == null` → `GetContext` = `default`, `TryGetContext` = `false`.
- 다른 타입 저장 → `GetContext` = `default`, `TryGetContext` = `false`(예외 없음).
- 값 타입 `T`도 `is` 패턴으로 동작.
- 스레드 안전: 기존 `Context` 프로퍼티의 `Volatile.Read`를 그대로 통과.

## 범위 (변경/비변경)

**변경:**
- 신규: `ServerLib/Core/SessionContextExtensions.cs`
- 신규 테스트: `ServerLib.Tests/SessionContextExtensionsTests.cs`
- 예제: `Server/Program.cs` — 설정한 `GameContext`를 한 곳(`OnClientDisconnected` 로그 등)에서 `GetContext<GameContext>()`로 되읽어 시연
- 문서: `README.md` `ISession` 절에 헬퍼 한 줄 노트

**비변경(중요):** `ISession`/`IServerListener`/`ISessionRegistry`/`ISessionRegistrar` 인터페이스, `SocketPipelineSession`, `StubSession`, 모든 콜백 시그니처 — `object? Context` 그대로.

**범위 외:** `IClientConnection`은 `Context` 프로퍼티가 없으므로 대상 아님.

## 테스트 (`SessionContextExtensionsTests`, `StubSession` 사용)

1. set 후 `GetContext<GameContext>()`가 동일 인스턴스를 타입값으로 반환.
2. `Context` 미설정 시 `GetContext<T>()` == `default`.
3. 다른 타입 저장 시 `GetContext<T>()` == `default`.
4. `TryGetContext<T>()` — 일치 시 `true`+값, 불일치/미설정 시 `false`+`default`.

(테스트용 컨텍스트 타입은 테스트 내 간단한 `record` 또는 기존 패턴 사용.)

## 빌드 검증

```
dotnet build E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release
dotnet test  E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln -c Release   # 기존 44 + 신규 통과
```

## 향후 확장 포인트

- 박싱이 실제 문제로 측정되면(값 타입 컨텍스트 다수) 그때 제네릭 저장/세션 타입 파라미터화를 별도 사이클로 검토.
- 다수 컨텍스트 슬롯이 필요해지면 키 기반 `IDictionary` 슬롯 대신 전용 컨텍스트 타입에 필드 추가 권장(YAGNI 유지).
