---
name: tdd-red-phase
description: "TDD Red 단계: 사용자 요구사항을 분석하여 구현 없이 실패하는 xUnit 테스트 케이스를 설계한다. Happy path·Edge case·Error case를 망라하고 컴파일 가능한 스텁과 함께 테스트를 작성한다. tdd-analyst 에이전트 전용 스킬."
---

# TDD Red Phase Skill

## 입력 읽기

`_workspace/00_requirements.md`를 Read로 읽는다.

## 요구사항 분해 (동작 단위 추출)

요구사항을 다음 질문으로 분해한다:
- "이 기능은 무엇을 **해야** 하는가?" → Happy Path
- "입력이 비정상이면 어떻게 **해야** 하는가?" → Error Case
- "경계값(0, null, 최대값)에서 어떻게 **해야** 하는가?" → Edge Case
- "동시에 여러 번 호출되면 어떻게 **해야** 하는가?" → Concurrency (필요 시)

## 테스트 케이스 설계 템플릿

```csharp
using Xunit;

namespace TddSession.Tests;

public class <FeatureName>Tests
{
    // ─── Happy Path ───────────────────────────────────────────
    [Fact]
    public void <MethodName>_<NormalCondition>_<ExpectedResult>()
    {
        // Arrange
        var sut = new <ClassName>();

        // Act
        var result = sut.<MethodName>(<validInput>);

        // Assert
        Assert.Equal(<expected>, result);
    }

    // ─── Edge Cases ───────────────────────────────────────────
    [Theory]
    [InlineData(<boundary1>, <expected1>)]
    [InlineData(<boundary2>, <expected2>)]
    public void <MethodName>_<BoundaryCondition>_<ExpectedResult>(
        <inputType> input, <resultType> expected)
    {
        var sut = new <ClassName>();
        var result = sut.<MethodName>(input);
        Assert.Equal(expected, result);
    }

    // ─── Error Cases ──────────────────────────────────────────
    [Fact]
    public void <MethodName>_<InvalidCondition>_Throws<ExceptionType>()
    {
        var sut = new <ClassName>();
        Assert.Throws<<ExceptionType>>(() => sut.<MethodName>(<invalidInput>));
    }
}
```

## 스텁 파일 생성 (컴파일 통과용)

```csharp
namespace TddSession.Src;

/// <summary>
/// [STUB] 테스트 컴파일용 스텁 — 구현 없음
/// </summary>
public class <FeatureName>
{
    public <ReturnType> <MethodName>(<Parameters>)
        => throw new NotImplementedException();
}
```

**스텁 없이는 테스트 파일이 컴파일 실패 → Red 단계 자체가 불가능.**

## 테스트 설계 근거 기록 (`test_design.md`)

```markdown
# 테스트 설계 근거

## 분석된 동작 목록
| # | 동작 | 테스트 케이스 | 우선순위 |
|---|------|-------------|---------|
| 1 | ... | ... | High |

## 설계 결정
- 선택한 인터페이스: `ClassName.MethodName(params)`
- 제외한 테스트: [이유와 함께 기록]
- 불명확한 요구사항: [추가 확인 필요 항목]

## 커버리지 목표
- Happy Path: N개
- Edge Case: N개
- Error Case: N개
```

## 좋은 테스트 vs 나쁜 테스트

| 좋은 테스트 | 나쁜 테스트 |
|-----------|-----------|
| `Add_TwoPositives_ReturnsSum` | `Test1`, `TestAdd` |
| 하나의 동작만 검증 | 여러 동작을 한 테스트에서 검증 |
| `Assert.Equal(5, result)` | `Assert.True(result > 0)` (모호) |
| `Assert.Throws<ArgumentNullException>` | `try-catch`로 예외 검증 |
| Fact/Theory 적절 사용 | 모든 테스트를 Fact로 작성 |

## 최소 테스트 집합 원칙

기능당 최소 3개, 최대 7개의 테스트로 시작한다:
- Happy path 1~2개
- Edge case 1~2개
- Error case 1~2개
- 추가 요구사항 발견 시 점진적 추가

## 출력 저장

1. 테스트 파일 → `_workspace/01_analyst/Tests/<FeatureName>Tests.cs`
2. 스텁 파일 → `_workspace/01_analyst/Src/<FeatureName>.cs`
3. 설계 근거 → `_workspace/01_analyst/test_design.md`

저장 후 `tdd-builder`에게 SendMessage로 구현 시작 신호를 전달한다.
