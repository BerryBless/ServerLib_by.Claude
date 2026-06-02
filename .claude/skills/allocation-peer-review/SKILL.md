---
name: allocation-peer-review
description: "heap-allocation-scanner와 pooling-enforcer의 GC 억제 분석 보고서 2개를 독립 교차 검증한다. False positive 제거, False negative 보완, 수정 코드 안전성 검증, ArrayPool.Return 경로 독립 추적을 수행하고 최종 GC 가드 보고서를 확정한다. allocation-peer-reviewer 에이전트 전용 스킬."
---

# Allocation Peer Review Skill

## 입력 읽기

3개 파일을 모두 Read로 읽는다:
1. `_workspace/02_allocation_findings.json` (heap-allocation-scanner 보고서)
2. `_workspace/02_pooling_findings.json` (pooling-enforcer 보고서)
3. `_workspace/00_input/source.txt` (독립 검증·FN 탐지용)

## 검증 프로세스

### Step 1: 발견사항별 소스 코드 대조

각 발견사항의 `file` 필드에서 파일명:라인을 추출하고, 소스 코드의 해당 위치를 직접 확인한다.

- 코드가 존재하는가?
- 실제로 해당 패턴인가?
- Hot path 맥락이 맞는가?

### Step 2: FP 판정 기준 적용

**기각(Rejected) 가능 조건:**

```
heap-allocation-scanner 발견의 FP:
□ new가 루프 안에 있지만 struct(값 타입) → 힙 할당 없음
□ 클로저 캡처이지만 static lambda로 컴파일러 최적화 가능
  (외부 변수를 캡처하지 않는 람다 → static lambda, 힙 할당 없음)
□ LINQ가 있지만 해당 메서드 호출 빈도가 낮음
  (초기화 시 1회, 설정 변경 시 등)
□ string 연산이 있지만 StringBuilder 이미 사용 중
□ params 배열이지만 실제 호출 시 인수가 고정 (컴파일러 최적화)

pooling-enforcer 발견의 FP:
□ Task<T> → ValueTask<T> 제안이지만 여러 await 소비자 존재
□ Substring → AsSpan 제안이지만 async 경계 통과 (Memory<T> 필요)
□ new byte[] → ArrayPool 제안이지만 배열 크기 ≤ 16 → stackalloc 더 적합
□ ArrayPool.Return 누락 제안이지만 Return이 다른 코드 경로에 존재
```

### Step 3: FN 독립 탐지 (7개 패턴)

소스 코드를 독립적으로 읽고 두 에이전트가 놓친 패턴을 탐색한다:

```csharp
// FN-1: params object[] 박스화
void Trace(string fmt, params object[] args)  // 호출마다 object[] + 박스화

// FN-2: 델리게이트 캐싱 미적용 (루프 내 람다)
for (int i = 0; i < n; i++)
    list.Sort((a, b) => a.Id.CompareTo(b.Id));  // 매번 new Comparison<T>

// FN-3: string.Format 값 타입 (.NET 5 이하에서 박스화)
string msg = string.Format("count={0}", count);  // count 박스화

// FN-4: Nullable<T>.Value 불필요한 추출
int? nullable = GetValue();
int val = (int)nullable;  // Nullable 언래핑 (박스화는 아니나 불필요한 null 체크 비용)

// FN-5: yield return in hot path (상태 머신 힙 할당)
IEnumerable<Packet> ParsePackets(byte[] data)
{
    yield return new Packet(...);  // IEnumerable 상태 머신 객체 힙 할당
}
// 수정: List<Packet>을 반환하거나 ImmutableArray 사용

// FN-6: Exception 생성 hot path
if (!valid)
    throw new InvalidOperationException(message);  // hot path에서 Exception 힙 할당
// 수정: Result<T> 패턴 또는 Exception 인스턴스 재사용

// FN-7: LINQ GroupBy/ToDictionary in hot path
var groups = items.GroupBy(x => x.Type).ToDictionary(...);  // 여러 컬렉션 생성
```

### Step 4: 수정 코드(fix_code) 안전성 검증

pooling-enforcer의 각 `fix_code`에 대해 다음을 확인한다:

```
ArrayPool 패턴 완전성 체크리스트:
□ Rent → try { ... } finally { Return(...) } 구조인가
□ buffer.Length가 아닌 실제 필요 크기로 AsSpan 슬라이스인가
□ 민감 데이터라면 Return(buffer, clearArray: true)인가
□ await 이후에도 buffer 접근이 있는가 (await 이후 Span 접근 금지)

ValueTask 패턴 완전성 체크리스트:
□ 반환된 ValueTask를 1회만 await하는가
□ 저장 후 조건부 await가 없는가
□ Task.WhenAll에 직접 전달하지 않는가 (AsTask() 필요)

Span/Memory 패턴 완전성 체크리스트:
□ Span<T>이 async 메서드에 직접 전달되지 않는가
□ Span<T>이 힙 필드에 저장되지 않는가
□ Memory<T>가 async 경계에 올바르게 사용되는가
```

### Step 5: 교차 일관성 검사

두 에이전트가 동일 위치를 다르게 보고했을 때:
1. 두 발견 모두 소스 코드와 대조
2. 더 정확한 발견을 Confirmed, 나머지를 Rejected 또는 Modified로 처리
3. 상충 근거와 최종 판정을 명시

## 최종 점수 계산

```
confirmed = Confirmed 발견 수 (allocation_verdicts)
additional = additional_findings 수

critical_count = confirmed + additional 중 critical 수
high_count     = high 수
medium_count   = medium 수
low_count      = low 수

penalty = critical_count * 25 + high_count * 12 + medium_count * 5 + low_count * 2
final_score = max(0, 100 - penalty)
```

## 출력 저장

완성된 JSON을 `_workspace/03_peer_review.json`에 Write한다.
리더에게 `{"status": "done", "final_score": N}` SendMessage를 전송한다.
