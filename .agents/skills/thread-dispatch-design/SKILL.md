---
name: thread-dispatch-design
description: ".NET 10 고성능 서버를 위해 Channel<T>/IThreadPoolWorkItem 기반 락-프리 스레드 디스패처를 설계하고 C# 코드를 작성한다. BoundedChannel 백프레셔, 클로저-프리 Work Item, 안전한 종료 처리를 포함한 완전한 구현을 _workspace/02_dispatcher/ThreadDispatcher.cs에 출력한다. thread-dispatcher-designer 에이전트 전용 스킬."
---

# Thread Dispatch Design Skill

## 입력 읽기

1. `_workspace/00_design_brief.md` — 예상 처리량, 워커 수, 우선순위 요구사항
2. `_workspace/02_interface_contract.cs` — IO 루프와의 메시지 타입 인터페이스

## 핵심 구조: Channel 기반 락-프리 디스패처

```csharp
public sealed class ThreadDispatcher : IAsyncDisposable
{
    private readonly Channel<ParsedMessage> _channel;
    private readonly Task[] _workerTasks;
    private readonly CancellationTokenSource _cts = new();

    public ThreadDispatcher(DispatcherOptions options)
    {
        // BoundedChannel: 채널이 꽉 차면 WriteAsync가 await — 자동 백프레셔
        _channel = Channel.CreateBounded<ParsedMessage>(
            new BoundedChannelOptions(options.ChannelCapacity)
            {
                // SingleWriter: IO 루프가 1개이면 true로 최적화
                SingleWriter = options.SingleWriter,
                // SingleReader: 워커가 1개이면 true
                SingleReader = options.WorkerCount == 1,
                // 꽉 찼을 때 쓰기 측을 await로 대기 (Drop이나 DropOldest 대안도 있음)
                FullMode = BoundedChannelFullMode.Wait
            });

        // 워커 Task 시작 (ThreadPool 스레드에서 실행)
        _workerTasks = Enumerable
            .Range(0, options.WorkerCount)
            .Select(_ => Task.Run(
                () => WorkerLoopAsync(_channel.Reader, _cts.Token),
                _cts.Token))
            .ToArray();
    }

    // IO 루프에서 호출 — await가 백프레셔 역할
    public ValueTask DispatchAsync(ParsedMessage message, CancellationToken ct) =>
        _channel.Writer.WriteAsync(message, ct);

    public async ValueTask DisposeAsync()
    {
        // ⚠ Complete 미호출 시 WorkerLoopAsync의 await foreach가 무한 대기
        _channel.Writer.Complete();

        // 모든 워커가 채널을 drain한 후 종료 대기
        await Task.WhenAll(_workerTasks).ConfigureAwait(false);
        _cts.Dispose();
    }
}
```

## IThreadPoolWorkItem 패턴 (클로저-프리 고급 최적화)

`Channel<T>`의 Worker 루프에서 각 메시지 처리를 별도 ThreadPool Work Item으로 오프로드할 때, 람다 클로저 대신 `struct IThreadPoolWorkItem`을 사용하면 힙 할당을 제거한다:

```csharp
// ❌ 람다 클로저 — 매 메시지마다 Func<> 힙 할당
ThreadPool.QueueUserWorkItem(_ => ProcessMessage(msg), null);

// ✅ struct Work Item — 힙 할당 0
private readonly struct MessageWorkItem : IThreadPoolWorkItem
{
    private readonly ParsedMessage _message;
    private readonly IMessageHandler _handler;

    public MessageWorkItem(ParsedMessage message, IMessageHandler handler)
    {
        _message = message;
        _handler = handler;
    }

    public void Execute() => _handler.Handle(_message);
}

// 호출
ThreadPool.UnsafeQueueUserWorkItem(
    new MessageWorkItem(message, _handler),
    preferLocal: true);  // 로컬 큐 우선 (캐시 지역성)
```

**`UnsafeQueueUserWorkItem` vs `QueueUserWorkItem`:**
- `Unsafe`: 보안 컨텍스트(ExecutionContext) 전파 생략 → 서버 코드에서 통상 안전하고 빠름
- `QueueUserWorkItem`: 보안 컨텍스트 복사 → 약간의 오버헤드, 필요한 경우에만 사용

## 워커 루프 구현

```csharp
private static async Task WorkerLoopAsync(
    ChannelReader<ParsedMessage> reader,
    CancellationToken ct)
{
    // await foreach는 Channel.Complete() 호출 시 자동 종료
    await foreach (ParsedMessage message in reader.ReadAllAsync(ct)
        .ConfigureAwait(false))
    {
        try
        {
            await ProcessMessageAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 메시지 처리 실패는 워커 루프를 종료하지 않음 (로깅 후 계속)
            LogError(ex, message);
        }
    }
}
```

## DispatcherOptions 설계

```csharp
public sealed record DispatcherOptions
{
    // 채널 용량 = 예상 처리량(msg/s) × 허용 지연(s)
    // 예: 100,000 msg/s × 0.01s = 1,000 (10ms 지연 허용)
    public int ChannelCapacity { get; init; } = 1_000;

    public int WorkerCount { get; init; } = Environment.ProcessorCount;

    // IO 루프가 1개인 경우 true로 Channel 내부 최적화
    public bool SingleWriter { get; init; } = true;
}
```

## 채널 용량 선택 기준

| 시나리오 | FullMode | 용량 | 이유 |
|---------|---------|------|------|
| 레이턴시 중요 | Wait | 작음 (100~500) | 빠르게 백프레셔 적용 |
| 처리량 중요 | Wait | 큼 (5000~10000) | 버스트 흡수 |
| 손실 허용 | DropOldest | 중간 | 최신 데이터 우선 |
| 실시간 스트림 | Drop | 큼 | 절대 블로킹 없음 |

## Channel 완료 흐름 (종료 순서 중요)

```
[IO 루프 종료]
    ↓ PipeWriter.CompleteAsync()
    ↓ PipeReader drain 완료
    ↓ ReadPipeAsync 종료
    ↓ dispatcher.DisposeAsync() 호출
    ↓ Channel.Writer.Complete()   ← ⚠ 반드시 이 순서
    ↓ WorkerLoopAsync들이 drain 후 종료
    ↓ Task.WhenAll 완료
```

## 출력 저장

완성된 C# 코드를 `_workspace/02_dispatcher/ThreadDispatcher.cs`에 Write한다.
감독자에게 완료 SendMessage를 전송한다.
