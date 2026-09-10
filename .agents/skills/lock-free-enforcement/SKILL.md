---
name: lock-free-enforcement
description: ".NET 10 고성능 서버 코드에서 전통적 락(lock/Monitor/Mutex/ReaderWriterLockSlim)을 탐지하고, Interlocked·System.Threading.Channels 등 현업 검증된 Lock-Free 대안으로 교체 가능한지 판정한다. lock-free-enforcer 에이전트가 사용하는 전용 스킬."
---

# Lock-Free Enforcement Skill

## 입력 읽기

`_workspace/00_input/source.txt`를 Read로 읽는다.
diff 형식이면 `+` 줄(추가된 코드)에 집중한다.

## 탐지 패턴

다음 패턴을 모두 탐색한다 (정규식 기준):

```
lock\s*\(
Monitor\.Enter|Monitor\.TryEnter|Monitor\.Exit
new\s+Mutex|new\s+SemaphoreSlim|new\s+ReaderWriterLockSlim
SpinLock\s
Interlocked\. (허용이지만 오용 여부 확인)
```

## 판정 기준: 교체 가능 vs. 필요

### 교체 가능 (replaceable) — Lock-Free 전환 권고

| 기존 패턴 | Lock-Free 대안 |
|----------|--------------|
| `lock` + 단일 int/long 증감 | `Interlocked.Increment/Decrement` |
| `lock` + 단일 참조 교체 | `Interlocked.Exchange/CompareExchange` |
| `lock` + bool 플래그 | `Interlocked.CompareExchange(ref _flag, 1, 0)` |
| 생산자-소비자 큐 `lock` 보호 | `Channel<T>.CreateUnbounded/Bounded()` |
| `ConcurrentQueue` + 별도 `lock` | `ConcurrentQueue<T>` 단독 사용 |
| `Dictionary<K,V>` + `lock` | `ConcurrentDictionary<K,V>` |
| `lock` + 단일 long 읽기 (64-bit) | `Interlocked.Read` (32-bit OS 필수) 또는 `Volatile.Read` |

**Interlocked CAS 교체 코드 예시:**
```csharp
// Before: lock 사용
private int _counter;
lock (_sync) { _counter++; }

// After: Lock-Free
private int _counter;
Interlocked.Increment(ref _counter);
```

```csharp
// Before: 참조 교체 lock
private MyState _state;
lock (_sync) { _state = newState; }

// After: Lock-Free
private MyState _state;
Interlocked.Exchange(ref _state, newState);
```

```csharp
// Before: 생산자-소비자 lock
private readonly Queue<Work> _queue = new();
lock (_sync) { _queue.Enqueue(work); }

// After: Channel
private readonly Channel<Work> _channel =
    Channel.CreateUnbounded<Work>(new() { SingleWriter = false, SingleReader = true });
await _channel.Writer.WriteAsync(work);
```

### 필요 (necessary) — 교체 불가 판정 기준

다음 조건 중 하나라도 해당하면 "necessary"로 분류한다:

1. **복합 원자 연산**: 연산 중 2개 이상의 독립 필드를 일관된 상태로 동시 업데이트해야 함
   - 예: `balance -= amount; log.Add(transaction);` — 두 변경이 항상 같이 일어나야 함
2. **조건부 분기 포함 원자 연산**: Read → Conditional Check → Write가 원자적이어야 하고, CAS 루프로 구현 시 ABA 문제나 기아(starvation) 위험이 있음
3. **외부 리소스 보호**: 파일 핸들, DB 커넥션, 소켓 등 락 없이는 동시 접근이 물리적으로 불안전한 리소스
4. **순서 보장 필요**: 단순 원자성이 아닌 "A 작업 완전 종료 후 B 작업 시작" 같은 전체 순서 보장 (Channel의 순서로는 부족)

## Interlocked 오용 탐지

Lock-Free가 맞지 않는 Interlocked 사용:

```csharp
// 위험: CAS 루프에서 ABA 문제 가능성
// 객체를 교체할 때 포인터 값 재사용으로 잘못된 CAS 성공
while (Interlocked.CompareExchange(ref _node, newNode, expected) != expected)
{
    expected = _node; // ABA: _node가 A→B→A로 변했는데 A로 인식
}
```

## 출력 저장

완성된 JSON을 `_workspace/02_lockfree_findings.json`에 Write한다.
`necessary_locks` 목록을 `lock-justification-auditor`에게 SendMessage로 전달한다.
