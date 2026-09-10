---
name: deadlock-review
description: "deadlock-analyzer가 생성한 .NET 10 데드락 정적 분석 보고서를 독립 검증한다. False positive 제거, False negative 추가, 최종 확정 보고서를 생성한다. deadlock-reviewer 에이전트가 사용하는 전용 스킬."
---

# Deadlock Review Skill

## 입력 읽기

1. `_workspace/03_deadlock_analysis.json` — 분석기 보고서
2. `_workspace/00_input/source.txt` — 독립 검증용 소스 코드

## 검증 프로세스

### Step 1: 발견사항별 독립 재확인

분석기 보고서의 각 발견사항에 대해:
1. 소스 코드에서 해당 위치(파일:라인)를 직접 확인한다
2. 탐지된 패턴이 실제로 존재하는지 검증한다
3. 위험도가 올바르게 분류됐는지 평가한다

### Step 2: False Positive 판정 기준

다음 조건 충족 시 **Rejected** 처리:

**Pattern 1 (동기 블로킹) False Positive 조건:**
- 코드가 `SynchronizationContext.Current == null`이 보장되는 컨텍스트에서만 실행됨을 코드 구조로 확인 가능한 경우
- 예: `static async Task Main` → 기본적으로 SynchronizationContext 없음 (단, 라이브러리라면 항상 risk)
- **주의**: "라이브러리 코드"는 절대 기각 불가

**Pattern 2 (lock+await) False Positive 조건:**
- await 이후 continuation이 동일 스레드에서 실행됨을 `TaskScheduler`나 `SynchronizationContext` 구성으로 보장하는 경우 (매우 드문 경우)

**Pattern 4 (취득 순서 불일치) False Positive 조건:**
- 두 취득 경로가 런타임에 동시에 실행될 수 없음을 코드 흐름으로 증명 가능한 경우
- 예: 경로 A가 Init 단계에서만, 경로 B가 Runtime 단계에서만 실행되고 Init 완료 후 B 실행

### Step 3: False Negative 탐지 체크리스트

분석기가 놓치기 쉬운 패턴을 직접 탐지:

```csharp
// FN-1: Task.Run 내부 블로킹 (외형상 async처럼 보이지만 내부는 동기)
Task.Run(() => someAsyncMethod().Result)

// FN-2: 간접 async void (람다)
button.Click += async (s, e) => { await DoWorkAsync(); };
// → 이벤트 핸들러지만 예외 처리 없으면 프로세스 크래시

// FN-3: Channel Writer.Complete 미호출
private readonly Channel<T> _ch = Channel.CreateUnbounded<T>();
// Dispose 또는 종료 경로에서 _ch.Writer.Complete() 없음

// FN-4: WhenAll + 단일 CancellationTokenSource 조기 Cancel
var cts = new CancellationTokenSource();
var t1 = DoWork1Async(cts.Token);
var t2 = DoWork2Async(cts.Token);
cts.Cancel(); // t1, t2가 CancellationToken을 무시하면 WhenAll 무한 대기

// FN-5: Recursive async with shared SemaphoreSlim (자기 교착)
async Task RecursiveAsync()
{
    await _sem.WaitAsync();
    try { await RecursiveAsync(); } // 재진입 불가 → 자기 교착
    finally { _sem.Release(); }
}
```

### Step 4: 심각도 재조정 (Modified)

다음 상황에서 심각도를 올리거나 내린다:

**상향 (Medium → High):**
- 해당 코드가 hot path (요청마다 실행)에 있음이 코드에서 확인될 때

**하향 (High → Medium):**
- 취득 순서 불일치가 이론적으로만 가능하고 실제 동시 실행 가능성이 코드상 매우 낮음을 확인할 때

### Step 5: 재분석 요청 결정

다음 경우 `deadlock-analyzer`에게 재분석 요청:
- 분석기가 명백히 놓친 중요 패턴(FN-1~5)이 2개 이상 발견될 때
- 보고서의 50% 이상이 기각(Rejected) 처리될 때 → 분석 방법론에 문제 가능성

재분석은 **최대 1회**만 요청한다.

## 최종 점수 계산

```
confirmed_critical = Confirmed 발견 중 critical 수
confirmed_high = Confirmed 발견 중 high 수
additional = additional_findings 수

penalty = confirmed_critical * 30 + confirmed_high * 15 + (medium수 * 5) + (low수 * 2)
final_score = max(0, 100 - penalty)
```

## 출력 저장

완성된 JSON을 `_workspace/03_deadlock_review.json`에 Write한다.
리더에게 완료 SendMessage를 전송한다.
