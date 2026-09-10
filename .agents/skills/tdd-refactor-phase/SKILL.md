---
name: tdd-refactor-phase
description: "TDD Refactor 단계: dotnet test를 실제 실행하여 Green 여부를 검증하고, 테스트 통과 시 리팩토링 포인트를 가이드하며 회귀 테스트를 수행한다. Review Gate: PASS 없이 다음 단계 진행 불가. tdd-qa 에이전트 전용 스킬."
---

# TDD Refactor Phase Skill

## 입력 읽기

1. `_workspace/01_analyst/Tests/<FeatureName>Tests.cs` — 테스트 파일
2. `_workspace/02_builder/Src/<FeatureName>.cs` — 구현 파일

## Step 1: 프로젝트 파일 확인 및 테스트 실행 준비

`_workspace/TddSession.csproj`가 존재하는지 확인한다.
없으면 오케스트레이터에게 알린다 (Phase 1에서 생성했어야 함).

## Step 2: dotnet test 실행

```bash
# 빌드 먼저
dotnet build E:/project/dotnet_study/_workspace/TddSession.csproj

# 테스트 실행 (상세 출력)
dotnet test E:/project/dotnet_study/_workspace/TddSession.csproj \
  --logger "console;verbosity=detailed" \
  --no-build \
  2>&1 | tee E:/project/dotnet_study/_workspace/03_qa/test_results.txt
```

## Step 3: 결과 분석 및 판정

### PASS 판정 기준
```
✅ Build: 성공
✅ Passed: N (전체 테스트 수)
✅ Failed: 0
✅ Skipped: 0 (스킵된 테스트 있으면 이유 기록)
```

### FAIL 판정 → builder 반환
```
❌ Build 실패 → 컴파일 오류 전체를 builder에게 전달
❌ Failed > 0 → 실패 테스트명 + 오류 메시지 + 기대값 vs 실제값을 builder에게 전달
```

**FAIL 피드백 형식:**
```
실패 테스트: Calculator_Tests.Add_TwoPositives_ReturnsSum
오류: Assert.Equal() Failure
  Expected: 5
  Actual:   0
원인 추정: Add 메서드가 항상 0을 반환함
```

## Step 4: Refactor 가이드 (PASS 후에만)

### 코드 냄새 체크리스트

```csharp
// ❌ 중복 코드 → 메서드 추출
public int AddAndDouble(int a, int b) => (a + b) * 2;
public int AddAndTriple(int a, int b) => (a + b) * 3;
// ✅ 리팩토링
public int AddAndMultiply(int a, int b, int factor) => (a + b) * factor;

// ❌ 매직 넘버
if (retryCount > 3) throw new Exception();
// ✅ 상수 추출
private const int MaxRetryCount = 3;

// ❌ Fake Implementation이 남아 있음
public int Add(int a, int b) => 5; // ← 여러 테스트를 통과시키는 하드코딩
// ✅ 실제 구현으로 대체 (테스트가 이미 이를 강제함)

// ❌ 긴 메서드 (30줄+)
public Result Process(Input input) { /* 50줄 */ }
// ✅ 책임별 메서드 분리

// ❌ 불명확한 변수명
var x = users.Where(u => u.a > 18).ToList();
// ✅
var adultUsers = users.Where(u => u.Age > 18).ToList();
```

### 리팩토링 우선순위
1. **Fake Implementation 제거** — 가장 먼저 (테스트가 이미 요구하면)
2. **중복 제거** — 같은 코드가 3회 이상 반복되면
3. **이름 개선** — 의미가 불명확한 식별자
4. **메서드 분리** — 단일 책임 원칙 위반
5. **상수 추출** — 매직 넘버/문자열

### 리팩토링 금지 사항
- 현재 테스트가 검증하지 않는 새 기능 추가 금지
- 인터페이스 변경 (메서드 서명 변경) 금지
- 성능 최적화 (아직 필요성 미증명) 금지

## Step 5: 회귀 테스트

리팩토링 코드를 `_workspace/03_qa/Src/`에 저장한 후:

```bash
dotnet test E:/project/dotnet_study/_workspace/TddSession.csproj \
  --logger "console;verbosity=detailed"
```

리팩토링 후에도 동일한 테스트가 모두 통과해야 한다.
회귀 발생 시 리팩토링을 롤백하고 안전한 수준으로 재시도한다.

## refactor_guide.md 작성

```markdown
# Refactor Phase 결과

## 테스트 실행 결과
- 빌드: 성공/실패
- 전체: N개
- 통과: N개
- 실패: N개

## 판정: PASS / FAIL

## Refactor 제안 사항
| # | 위치 | 냄새 유형 | 제안 | 우선순위 |
|---|------|---------|------|---------|
| 1 | ClassName:라인 | 중복 코드 | ... | High |

## 회귀 테스트 결과
리팩토링 후 전체 N개 테스트 통과 확인.
```

## 출력 저장

1. 테스트 실행 결과 → `_workspace/03_qa/test_results.txt`
2. 리팩토링 가이드 → `_workspace/03_qa/refactor_guide.md`
3. 리팩토링 적용 코드 → `_workspace/03_qa/Src/` (선택)

PASS 판정 후 오케스트레이터에게 완료 SendMessage를 전송한다.
