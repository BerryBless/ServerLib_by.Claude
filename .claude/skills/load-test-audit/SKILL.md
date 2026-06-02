---
name: load-test-audit
description: "System.IO.Pipelines + Channel<T> 기반 고성능 서버 코드를 부하 테스트 관점에서 감사한다. PipeReader/Writer 미완료(메모리 누수), AdvanceTo 미호출(버퍼 영구 보유), Channel.Writer.Complete 누락, Zero-copy 위반(.ToArray()), 핫 패스 락 병목, 백프레셔 오작동을 탐지하고 APPROVE/BLOCK 판정을 내린다. load-test-auditor 에이전트 전용 스킬."
---

# Load Test Audit Skill

## 입력 읽기

1. `_workspace/02_io_loop/IoLoop.cs`
2. `_workspace/02_dispatcher/ThreadDispatcher.cs`
3. `_workspace/02_interface_contract.cs` (있으면)

## 감사 체크리스트 (7개 영역)

### 영역 1: PipeWriter 완료 누락 (CRITICAL)

```csharp
// ✅ 안전: finally 블록에서 항상 호출
try { /* FillPipeAsync 루프 */ }
finally { await writer.CompleteAsync(); }

// ❌ 위험: 예외 시 Complete 미호출 → ReadPipeAsync 무한 대기 → 연결 메모리 영구 보유
await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
// ... (finally 없음)
await writer.CompleteAsync();
```

**탐지:** `writer.CompleteAsync()` 또는 `writer.Complete()` 호출을 찾는다. `try/finally` 블록 안에 있는지 확인한다. 없으면 CRITICAL.

### 영역 2: PipeReader 완료 누락 (CRITICAL)

```csharp
// ✅ 안전
try { /* ReadPipeAsync 루프 */ }
finally { reader.Complete(); }

// ❌ 위험: FillPipe 측에 백프레셔 신호 전달 불가 → writer가 영원히 FlushAsync 대기
```

**탐지:** `reader.Complete()` 호출의 `try/finally` 위치 확인.

### 영역 3: AdvanceTo 미호출 또는 잘못된 호출 (CRITICAL)

```csharp
// ✅ 정상: consumed 이전 버퍼 해제
reader.AdvanceTo(consumed, examined);

// ❌ 미호출: 버퍼가 파이프에 영구 보유 → 부하 테스트 시 메모리 폭증
ReadResult result = await reader.ReadAsync(ct);
// ... AdvanceTo 없이 다음 ReadAsync 호출

// ❌ 잘못된 위치: 항상 Start 전달 → 진행 없이 무한 루프
reader.AdvanceTo(buffer.Start, buffer.Start);
```

**탐지:**
1. `reader.ReadAsync` 또는 `reader.TryRead` 이후에 반드시 `reader.AdvanceTo` 호출이 있는지 확인
2. consumed와 examined 인수가 실제로 전진하는지 코드 흐름 추적

### 영역 4: Zero-copy 위반 (HIGH)

```csharp
// ❌ Zero-copy 위반: ReadOnlySequence를 배열로 복사
byte[] data = buffer.ToArray();
byte[] data = buffer.First.ToArray();
byte[] data = buffer.Slice(0, 10).ToArray();

// ❌ 불필요한 메모리 복사
Span<byte> span = stackalloc byte[buffer.Length];
buffer.CopyTo(span);  // 필요한 경우가 아니면 위반

// ✅ Zero-copy: SequenceReader로 직접 파싱
var reader = new SequenceReader<byte>(buffer);
reader.TryReadLittleEndian(out int value);
```

**탐지:** `buffer.ToArray()`, `buffer.First.ToArray()`, `.Slice(...).ToArray()` 패턴 검색.

### 영역 5: Channel.Writer.Complete 누락 (CRITICAL)

```csharp
// ✅ 안전
public async ValueTask DisposeAsync()
{
    _channel.Writer.Complete();
    await Task.WhenAll(_workers);
}

// ❌ 위험: 워커의 await foreach / ReadAllAsync가 무한 대기
public void Dispose()
{
    _cts.Cancel();
    // Channel.Writer.Complete() 미호출
}
```

**탐지:** `Dispose`/`DisposeAsync`/종료 경로에서 `_channel.Writer.Complete()` 확인.

### 영역 6: 핫 패스 락 병목 (HIGH)

```csharp
// ❌ IO 루프 핫 패스에 lock
lock (_syncRoot) { /* ReadPipeAsync 루프 내부 */ }

// ❌ 디스패처 핫 패스에 lock  
lock (_handlers) { _handlers[key](message); }

// ✅ Channel 자체가 lock-free — 외부 lock 불필요
await _channel.Writer.WriteAsync(message, ct);
```

**탐지:** `FillPipeAsync`, `ReadPipeAsync`, `WorkerLoopAsync`, `DispatchAsync` 등 루프 내부에 `lock(`, `Monitor.Enter(`, `SemaphoreSlim.Wait(` (동기 호출) 존재 여부.

### 영역 7: 취소 누수 (MEDIUM)

```csharp
// ❌ CancellationToken이 ReceiveAsync에만 전달되고 FlushAsync, ReadAsync에는 누락
await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
FlushResult result = await writer.FlushAsync();  // ct 누락!
ReadResult readResult = await reader.ReadAsync();  // ct 누락!

// ✅ 모든 async 호출에 ct 전달
await writer.FlushAsync(ct);
await reader.ReadAsync(ct);
```

**탐지:** `FlushAsync()`, `ReadAsync()`, `WriteAsync()`, `ReceiveAsync()` 호출에 ct 파라미터 누락 여부.

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **CRITICAL** | 부하 테스트 중 메모리 폭증, 무한 대기, 프로세스 행(hang) 가능 |
| **HIGH** | 처리량 저하, GC 압력, 잠재적 타임아웃 |
| **MEDIUM** | 취소 지연, 종료 지연, 이론적 위험 |

## APPROVE / BLOCK 판정

- **BLOCK**: CRITICAL 발견 1건 이상
- **REQUEST CHANGES**: HIGH 발견 2건 이상
- **APPROVE**: CRITICAL/HIGH 없음

## 출력 저장

감사 결과를 `_workspace/03_load_test_audit.md`에 Write한다.
감독자에게 `{"status": "done", "verdict": "APPROVE|BLOCK", "critical_count": N}` SendMessage를 전송한다.
