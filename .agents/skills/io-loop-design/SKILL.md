---
name: io-loop-design
description: ".NET 10 고성능 서버를 위해 System.IO.Pipelines 기반 비동기 IO 루프를 설계하고 C# 코드를 작성한다. PipeReader/PipeWriter, SocketAsyncEventArgs, 백프레셔, Zero-copy 파싱을 포함한 완전한 구현을 _workspace/02_io_loop/IoLoop.cs에 출력한다. io-loop-designer 에이전트 전용 스킬."
---

# IO Loop Design Skill

## 입력 읽기

1. `_workspace/00_design_brief.md` — 서버 요구사항, 프로토콜 타입, 예상 처리량
2. `_workspace/02_interface_contract.cs` — 디스패처와의 인터페이스 (있으면 읽기)

## 핵심 구조: Fill-Read 분리 패턴

System.IO.Pipelines의 핵심 패턴은 **FillPipeAsync**(소켓 → Pipe 쓰기)와 **ReadPipeAsync**(Pipe → 파싱) 분리다.

```csharp
public sealed class IoLoop : IAsyncDisposable
{
    private readonly PipeOptions _pipeOptions;

    public IoLoop()
    {
        _pipeOptions = new PipeOptions(
            // 백프레셔: writer가 16KB 이상 쌓이면 reader가 읽을 때까지 FlushAsync 대기
            pauseWriterThreshold: 16 * 1024,
            resumeWriterThreshold: 8 * 1024,
            minimumSegmentSize: 4096,
            useSynchronizationContext: false  // 고성능 서버에서 SynchronizationContext 불필요
        );
    }

    public async Task ProcessConnectionAsync(Socket socket, CancellationToken ct)
    {
        var pipe = new Pipe(_pipeOptions);

        // FillPipe와 ReadPipe를 병렬 실행 — 한쪽이 완료되면 다른 쪽도 종료
        await Task.WhenAll(
            FillPipeAsync(socket, pipe.Writer, ct),
            ReadPipeAsync(pipe.Reader, ct)
        );
    }
}
```

## FillPipeAsync 구현 패턴

```csharp
private static async Task FillPipeAsync(
    Socket socket,
    PipeWriter writer,
    CancellationToken ct)
{
    const int MinimumBufferSize = 4096;

    try
    {
        while (!ct.IsCancellationRequested)
        {
            // Zero-copy: 파이프 내부 버퍼를 직접 요청 (새 배열 할당 없음)
            Memory<byte> buffer = writer.GetMemory(MinimumBufferSize);

            int bytesRead = await socket
                .ReceiveAsync(buffer, SocketFlags.None, ct)
                .ConfigureAwait(false);

            if (bytesRead == 0) break; // 연결 정상 종료

            // 실제 수신된 바이트만큼 파이프 커서 전진
            writer.Advance(bytesRead);

            // 리더에게 데이터 통보 + 백프레셔 확인
            FlushResult result = await writer.FlushAsync(ct).ConfigureAwait(false);

            // 리더가 파이프를 닫았으면 (예: 애플리케이션 종료) 즉시 탈출
            if (result.IsCompleted) break;
        }
    }
    catch (OperationCanceledException) { /* 정상 취소 */ }
    catch (SocketException ex) when (IsConnectionReset(ex)) { /* 클라이언트 강제 종료 */ }
    finally
    {
        // ⚠ 모든 종료 경로에서 반드시 호출 — 미호출 시 ReadPipeAsync 무한 대기
        await writer.CompleteAsync().ConfigureAwait(false);
    }
}

private static bool IsConnectionReset(SocketException ex) =>
    ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
```

## ReadPipeAsync 구현 패턴 (Zero-copy 파싱)

```csharp
private async Task ReadPipeAsync(
    PipeReader reader,
    CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try
            {
                // SequenceReader로 Zero-copy 파싱 — buffer.ToArray() 절대 금지
                var seqReader = new SequenceReader<byte>(buffer);

                while (TryParseMessage(ref seqReader, out ParsedMessage? message))
                {
                    // 파싱된 메시지를 디스패처로 전달
                    await _dispatcher.DispatchAsync(message, ct).ConfigureAwait(false);
                    consumed = seqReader.Position;
                }

                examined = result.IsCompleted ? buffer.End : seqReader.Position;
            }
            finally
            {
                // ⚠ 반드시 호출 — consumed까지 버퍼 해제, examined까지 다음에 재제공
                reader.AdvanceTo(consumed, examined);
            }

            if (result.IsCompleted) break; // FillPipeAsync가 종료됐고 버퍼도 비었으면 종료
        }
    }
    finally
    {
        // ⚠ 파이프 반대쪽(writer)에게 읽기 완료 알림
        reader.Complete();
    }
}
```

## 백프레셔 설계 기준

| 서버 유형 | PauseWriterThreshold | ResumeWriterThreshold | MinimumSegmentSize |
|---------|---------------------|----------------------|-------------------|
| 고처리량 (>100k rps) | 64KB | 32KB | 8KB |
| 균형 (기본) | 16KB | 8KB | 4KB |
| 저지연 | 4KB | 2KB | 1KB |
| 파일 IO | 256KB | 128KB | 16KB |

`useSynchronizationContext: false` — 고성능 서버에서는 항상 설정.

## SocketAsyncEventArgs 재사용 패턴 (고급)

소켓당 SocketAsyncEventArgs를 미리 생성하고 재사용하면 per-receive 할당을 제거한다:

```csharp
// PipeWriter.GetMemory로 받은 버퍼를 SAEA에 직접 연결
Memory<byte> buffer = writer.GetMemory(minimumSize);
MemoryHandle handle = buffer.Pin();
saea.SetBuffer(
    (byte*)handle.Pointer,
    buffer.Length);  // unsafe 컨텍스트 필요 시
```

표준 사용 패턴에서는 `Socket.ReceiveAsync(Memory<byte>)` 오버로드가 내부적으로 SAEA를 풀링하므로 일반적으로 직접 관리 불필요.

## AdvanceTo 올바른 사용

```csharp
// ✅ 완전한 메시지 1개 파싱 완료
reader.AdvanceTo(consumed: afterMessage, examined: afterMessage);

// ✅ 불완전한 메시지 — consumed는 이전, examined는 현재 끝
reader.AdvanceTo(consumed: buffer.Start, examined: buffer.End);

// ❌ 항상 같은 위치 전달 — 파이프 진행 없음 → 무한 루프
reader.AdvanceTo(buffer.Start, buffer.Start);
```

## 출력 저장

완성된 C# 코드를 `_workspace/02_io_loop/IoLoop.cs`에 Write한다.
감독자에게 완료 SendMessage를 전송한다.
