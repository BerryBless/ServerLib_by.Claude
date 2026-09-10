---
name: pooling-enforcement
description: ".NET 10 서버 라이브러리 hot path에서 ValueTask·ReadOnlySpan<T>·ArrayPool<T>.Shared 3대 GC 억제 기법의 올바른 적용을 강제한다. 잘못된 Task 반환, Substring 복사, 미풀링 버퍼, ArrayPool.Return 누락을 탐지하고 수정 코드 스니펫을 제시한다. pooling-enforcer 에이전트 전용 스킬."
---

# Pooling Enforcement Skill

## 입력 읽기

1. `_workspace/00_input/source.txt`를 Read로 읽는다
2. `heap-allocation-scanner`로부터 버퍼 할당 SendMessage가 있으면 해당 위치를 우선 분석한다

## 3대 기법 강제 체크리스트

### 기법 1: ValueTask 강제

**교체 대상 (`Task<T>` → `ValueTask<T>`):**

```csharp
// 교체 대상: 동기 완료 경로가 많은 메서드
public async Task<byte[]> ReadFromCacheAsync(string key)
{
    if (_cache.TryGetValue(key, out var cached))
        return cached;   // 캐시 히트 시 동기 반환 → Task 힙 할당 낭비
    return await FetchAsync(key);
}

// 수정: ValueTask<T>
public async ValueTask<byte[]> ReadFromCacheAsync(string key)
{
    if (_cache.TryGetValue(key, out var cached))
        return cached;   // 동기 반환 시 힙 할당 0
    return await FetchAsync(key);
}

// Task.FromResult 교체
return Task.FromResult(value);    // Task<T> 힙 할당
return ValueTask.FromResult(value);  // 또는 new ValueTask<T>(value)
return new ValueTask<T>(value);   // 완전히 스택 기반
```

**ValueTask 오용 탐지 (교체 역방향 - 오히려 경고):**
```csharp
// 오용 1: 다중 await (ValueTask는 1회 await만 안전)
var vt = SomeValueTaskMethod();
await vt;   // 첫 번째
await vt;   // 두 번째 → undefined behavior
// 수정: var task = vt.AsTask();

// 오용 2: 저장 후 조건부 await
ValueTask<int> stored = GetValueAsync();
if (condition) await stored;  // 저장 후 사용 → 위험
// 수정: 즉시 await하거나 AsTask()로 변환
```

**ValueTask가 부적절한 경우 (교체 금지):**
- 결과를 여러 소비자가 await할 때
- `Task.WhenAll`/`Task.WhenAny`에 전달할 때 (AsTask() 필요)
- 예외 전파가 복잡한 경우

### 기법 2: ReadOnlySpan<T> / Memory<T> 강제

**교체 대상:**

```csharp
// Substring → AsSpan 교체 (hot path)
string sub = input.Substring(offset, length);   // 새 string 힙 할당
// 수정:
ReadOnlySpan<char> sub = input.AsSpan(offset, length);  // 복사 없음

// 배열 슬라이스 → AsSpan 교체
byte[] slice = buffer.Skip(offset).Take(length).ToArray();  // 새 배열 힙 할당
// 수정:
ReadOnlySpan<byte> slice = buffer.AsSpan(offset, length);   // 복사 없음

// 문자열 파싱 → Span 기반
string part = line.Split(',')[0];               // 여러 string 할당
// 수정:
ReadOnlySpan<char> part = line.AsSpan(0, line.IndexOf(','));
```

**Span vs Memory 선택 기준:**

| 상황 | 사용 |
|------|------|
| 동기 메서드, async 경계 없음 | `Span<T>` / `ReadOnlySpan<T>` |
| async 메서드, await 있음 | `Memory<T>` / `ReadOnlyMemory<T>` |
| 힙 필드에 저장 | `Memory<T>` (Span은 ref struct, 필드 저장 불가) |
| 외부로 반환 | `Memory<T>` (Span은 스택 탈출 불가) |

**오용 탐지 (수정 필요):**
```csharp
// 오용 1: Span을 async 메서드에 직접 전달 → 컴파일 에러
async Task ProcessAsync(Span<byte> data)  // CS4012: Span은 async 파라미터 불가
// 수정: Memory<byte>로 교체

// 오용 2: Span을 인스턴스 필드에 저장
class Processor {
    Span<byte> _buffer;  // CS8345: Span은 ref struct, 필드 저장 불가
}
// 수정: Memory<byte> _buffer;
```

### 기법 3: ArrayPool<T>.Shared 강제

**교체 대상 (hot path의 `new byte[n]`):**

```csharp
// 교체 대상: hot path 버퍼 할당
byte[] buffer = new byte[4096];          // LOH 또는 SOH 힙 할당
// 수정: 완전한 ArrayPool 패턴
byte[]? buffer = null;
try
{
    buffer = ArrayPool<byte>.Shared.Rent(4096);
    // buffer.Length >= 4096 보장 (정확히 4096이 아닐 수 있음)
    int written = FillBuffer(buffer.AsSpan(0, 4096));
    ProcessBuffer(buffer.AsSpan(0, written));
}
finally
{
    if (buffer is not null)
        ArrayPool<byte>.Shared.Return(buffer);
}
```

**ArrayPool.Return 누락 탐지 (CRITICAL):**
```csharp
// CRITICAL: try/finally 없는 Rent
var buf = ArrayPool<byte>.Shared.Rent(size);
await ProcessAsync(buf);     // 예외 시 Return 미호출 → 메모리 누수
ArrayPool<byte>.Shared.Return(buf);  // 예외 시 실행 안 됨

// 탐지 방법:
// 1. .Rent( 패턴을 찾는다
// 2. 해당 변수명으로 .Return( 호출을 찾는다
// 3. Return이 finally 블록 안에 없으면 CRITICAL
```

**민감 데이터 버퍼 반환 (보안):**
```csharp
// 암호화 키, 비밀번호, 개인정보를 담은 버퍼
ArrayPool<byte>.Shared.Return(sensitiveBuffer, clearArray: true);
// clearArray: false (기본값) → 버퍼 내용이 풀에 잔류 → 정보 유출 위험
```

**렌트 크기 혼동 주의:**
```csharp
var buffer = ArrayPool<byte>.Shared.Rent(requestedSize);
// buffer.Length >= requestedSize (정확히 requestedSize가 아님!)
// 실제 사용 시 항상 슬라이스:
var span = buffer.AsSpan(0, requestedSize);   // 올바름
var span = buffer.AsSpan();                    // 잘못됨: 더 큰 범위 사용 가능
```

## 수정 코드 작성 원칙

모든 `fix_code`에는:
1. 완전한 try/finally 구조 (ArrayPool 사용 시)
2. 실제 사용 크기와 렌트 크기 구분 (`AsSpan(0, actualLength)`)
3. Span이 async 경계를 넘지 않는 구조
4. ValueTask 다중 await 방지 주석

## 출력 저장

완성된 JSON을 `_workspace/02_pooling_findings.json`에 Write한다.
리더에게 완료를 알린다.
