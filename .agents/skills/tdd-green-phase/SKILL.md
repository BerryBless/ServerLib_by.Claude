---
name: tdd-green-phase
description: "TDD Green 단계: tdd-analyst가 설계한 실패하는 테스트를 최소한의 코드로 통과시킨다. Fake It → Obvious Implementation → Triangulation 순서로 가장 단순한 구현을 작성하며 Gold Plating을 엄격히 금지한다. tdd-builder 에이전트 전용 스킬."
---

# TDD Green Phase Skill

## 입력 읽기

1. `_workspace/01_analyst/Tests/<FeatureName>Tests.cs` — 실패하는 테스트
2. `_workspace/01_analyst/Src/<FeatureName>.cs` — 스텁 파일

스텁의 인터페이스(클래스명, 메서드 서명)를 정확히 파악한다.

## 구현 전략 선택 (우선순위 순)

### 전략 1: Fake It (테스트 1~2개)
테스트가 특정 입력에 대해 특정 값을 기대하면 상수를 반환한다.

```csharp
// 테스트: Assert.Equal(5, calculator.Add(2, 3))
public int Add(int a, int b) => 5; // ← 즉시 Green
```

**언제 사용**: 단 하나의 테스트만 있을 때. 삼각측량으로 일반화를 유도할 때.

### 전략 2: Obvious Implementation (로직이 명확할 때)
구현이 너무 명확하면 바로 작성한다.

```csharp
// 테스트: Add(2,3)=5, Add(1,1)=2, Add(0,0)=0
public int Add(int a, int b) => a + b; // ← 명백한 구현
```

**언제 사용**: 알고리즘이 자명하고 오버엔지니어링 위험이 없을 때.

### 전략 3: Triangulation (테스트 추가로 일반화)
Fake It으로 시작했는데 더 많은 테스트가 생겨 상수 반환이 불가능해진 상황.

```csharp
// 테스트 1: Add(2,3)=5 → return 5 (Fake)
// 테스트 2: Add(1,1)=2 → 상수 불가 → a + b (일반화)
public int Add(int a, int b) => a + b;
```

## 구현 시 철칙

### 금지 사항 (현재 테스트에 없으면 작성 금지)
```csharp
// ❌ 테스트에 없는 null 체크
public int Add(int? a, int? b) => (a ?? 0) + (b ?? 0);

// ❌ 테스트에 없는 오버플로우 처리
public int Add(int a, int b) => checked(a + b);

// ❌ 테스트에 없는 캐싱
private readonly Dictionary<(int,int), int> _cache = new();
public int Add(int a, int b) => _cache.GetOrAdd((a,b), k => k.Item1 + k.Item2);

// ❌ 미래 확장을 위한 인터페이스
public interface ICalculator { int Add(int a, int b); }
```

### 허용 사항
```csharp
// ✅ 가장 단순한 구현
public int Add(int a, int b) => a + b;

// ✅ 예외 테스트가 있으면 예외 처리
public int Divide(int a, int b)
{
    if (b == 0) throw new DivideByZeroException();
    return a / b;
}
```

## 구현 파일 작성 규칙

```csharp
namespace TddSession.Src;

public class <FeatureName>
{
    // Green: 테스트를 통과하는 최소 구현
    public <ReturnType> <MethodName>(<Parameters>)
    {
        // 구현 (가장 단순한 방법으로)
    }
}
```

## build_notes.md 작성

```markdown
# Green Phase 구현 노트

## 선택한 전략
- [Fake It / Obvious Implementation / Triangulation] — 이유:

## 구현 결정
| 테스트 | 구현 선택 | 이유 |
|--------|---------|------|
| TestName | ... | ... |

## Gold Plating 거부 목록
- 거부한 것: [이유와 함께 기록]

## tdd-qa 검증 결과 반영 (재작업 시)
- 실패 테스트: ...
- 수정 내용: ...
```

## 출력 저장

1. 구현 파일 → `_workspace/02_builder/Src/<FeatureName>.cs`
2. 노트 → `_workspace/02_builder/build_notes.md`

저장 후 `tdd-qa`에게 SendMessage로 검증 요청을 전달한다.
