---
name: deadlock-static-analysis
description: ".NET 10 async/await 코드에서 데드락 발생 가능한 모든 패턴(동기 블로킹, lock+await, SemaphoreSlim 누수, ConfigureAwait 누락, 락 순서 불일치 등)을 정적 분석으로 탐지하고 상세 보고서를 작성한다. deadlock-analyzer 에이전트가 사용하는 전용 스킬."
---

# Deadlock Static Analysis Skill

## 입력 읽기

1. `_workspace/00_input/source.txt` — 분석 대상 소스
2. `_workspace/02_lockfree_findings.json` — 락 위치 참조 (있으면 읽기)

## 8대 탐지 패턴

### Pattern 1: 동기 블로킹 (CRITICAL)

```csharp
// 탐지 대상
\.Result\b
\.Wait\(\)
\.Wait\(\d              // Wait(timeout) 포함
GetAwaiter\(\)\.GetResult\(\)
```

**분석 방법:**
- 해당 호출이 async 메서드 내부 또는 async 콜 체인에서 호출되는지 확인
- 순수 콘솔 앱/Worker Service의 `static void Main`에서 호출이면 SynchronizationContext 없으므로 conditional risk
- 라이브러리 코드(public API)에서 호출이면 unconditional risk (호출자가 GUI/ASP.NET일 수 있음)

```csharp
// CRITICAL 예시
public async Task<Data> GetDataAsync()
{
    return _http.GetStringAsync(url).Result; // 데드락 위험
}

// 수정
public async Task<Data> GetDataAsync()
{
    return await _http.GetStringAsync(url).ConfigureAwait(false);
}
```

### Pattern 2: lock 내부 await (CRITICAL)

```csharp
// 탐지: lock 블록 내 await
lock\s*\([^)]+\)\s*\{[^}]*\bawait\b
// 또는 Monitor + try + await 조합
Monitor\.Enter.*\n.*try.*\n.*await
```

C# 컴파일러가 `lock` 블록 안 `await`를 허용하지 않으므로, `Monitor.Enter` + `try/finally` + `await` 패턴으로 우회한 경우를 탐지한다.

```csharp
// CRITICAL: Monitor로 우회한 lock+await
Monitor.Enter(_syncRoot);
try
{
    var data = await FetchAsync(); // continuation이 다른 스레드 → Monitor 소유 스레드와 불일치
    _cache = data;
}
finally { Monitor.Exit(_syncRoot); }

// 수정: SemaphoreSlim(1,1)로 교체
await _semaphore.WaitAsync().ConfigureAwait(false);
try { var data = await FetchAsync().ConfigureAwait(false); _cache = data; }
finally { _semaphore.Release(); }
```

### Pattern 3: SemaphoreSlim 해제 경로 누락 (HIGH)

```csharp
// 탐지: WaitAsync 후 try/finally 없는 Release
await\s+\w+\.WaitAsync\(
// 위 패턴 다음에 try { ... } finally { ... Release() } 구조 없음
```

**분석:** WaitAsync 호출 후 5~20줄 내에 `try {` + `finally { ... .Release() }` 구조가 없으면 보고.

```csharp
// HIGH: finally 없음
await _semaphore.WaitAsync();
var result = await ProcessAsync(); // 예외 시 Release 미호출
_semaphore.Release();

// 수정
await _semaphore.WaitAsync().ConfigureAwait(false);
try
{
    var result = await ProcessAsync().ConfigureAwait(false);
}
finally
{
    _semaphore.Release();
}
```

### Pattern 4: 복수 SemaphoreSlim 취득 순서 불일치 (HIGH)

동일 메서드 또는 직접 호출 체인에서 두 개 이상의 SemaphoreSlim(또는 lock)을 취득하는 패턴을 찾는다.

**분석:**
- 메서드 A: `semA` → `semB` 순서로 취득
- 메서드 B: `semB` → `semA` 순서로 취득
- 두 패턴이 모두 존재하면 교착 가능 → HIGH

### Pattern 5: ConfigureAwait(false) 누락 (MEDIUM)

라이브러리 코드(public/internal 메서드)에서 `await` 뒤에 `.ConfigureAwait(false)` 없는 경우.

```csharp
// 탐지: await expr; (ConfigureAwait 없음)
await [^(ConfigureAwait][^\n]+;
```

예외: `await using`, `await foreach`, `Task.WhenAll/Any` 직접 사용 — 이 경우 ConfigureAwait 위치가 다름.

```csharp
// MEDIUM: 라이브러리 코드
public async Task<T> GetAsync<T>(string url)
{
    var response = await _http.GetAsync(url); // ConfigureAwait(false) 누락
    return await response.Content.ReadFromJsonAsync<T>();
}
```

### Pattern 6: async void (MEDIUM)

```csharp
async\s+void\s+\w+\s*\(
// 이벤트 핸들러 시그니처 제외: (object sender, EventArgs e) 또는 (object? sender, ...)
```

이벤트 핸들러(`EventHandler` 시그니처)의 `async void`는 허용. 나머지는 MEDIUM.

### Pattern 7: CancellationToken 미전파 (MEDIUM)

public async 메서드가 `CancellationToken` 파라미터를 받지 않거나, 받아도 내부 async 호출에 전달하지 않는 패턴.

```csharp
// MEDIUM: Token 누락
public async Task ProcessAsync()  // CancellationToken 파라미터 없음
{
    await Task.Delay(10000);  // 취소 불가
}

// 수정
public async Task ProcessAsync(CancellationToken cancellationToken = default)
{
    await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
}
```

### Pattern 8: Channel 완료 미처리 (LOW)

`Channel.CreateUnbounded/Bounded` 생성 후 `Writer.Complete()` 또는 `Writer.TryComplete()` 호출이 없는 경우.
`ReadAllAsync()` 사용 시 Complete 없으면 영구 대기.

## 위험도 점수 기준

| 위험도 | 패턴 |
|--------|------|
| **CRITICAL** | 즉시 데드락 재현 가능 (Pattern 1, 2) |
| **HIGH** | 특정 타이밍/예외 조건에서 교착 (Pattern 3, 4) |
| **MEDIUM** | 호출 환경에 따라 교착 가능 (Pattern 5, 6, 7) |
| **LOW** | 잠재적 무한 대기 (Pattern 8) |

## 재현 시나리오 작성 가이드

각 발견사항마다 아래를 포함한다:
1. **트리거 조건**: 어떤 상황(요청 패턴, 예외 발생 등)에서 데드락이 촉발되는가
2. **호출 스택**: 어디서 블로킹이 시작되고 어디서 기다리는가
3. **탈출 불가 이유**: 왜 두 스레드/작업이 서로를 영구히 기다리는가

## 출력 저장

완성된 JSON을 `_workspace/03_deadlock_analysis.json`에 Write한다.
`deadlock-reviewer`에게 SendMessage로 검증 요청을 전송한다.
