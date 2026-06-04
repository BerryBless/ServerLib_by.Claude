using System.Buffers;
using System.Threading.Channels;

namespace ServerLib.Core.Rudp;

// readonly struct: 값 타입 + 불변 — 큐에 넣고 꺼낼 때 박싱 없이 인라인 복사되며, Buffer는 참조 필드라 얕은복사(버퍼 포인터만 전달).
// 재전송 메타데이터를 묶은 작은 값 객체이므로 class로 만들면 세그먼트마다 Gen0 할당이 발생 → struct로 회피.
public readonly struct RudpSegment
{
    public readonly uint SequenceNumber;
    public readonly byte[] Buffer;    // ArrayPool 대여 버퍼 — 이 struct는 소유권이 아닌 참조만 보유(얕은복사), 실제 반납은 큐 소비자가 담당
    public readonly int Length;
    public readonly DateTimeOffset SentAt;
    public readonly int RetryCount;

    public RudpSegment(uint seq, byte[] buffer, int length, int retryCount = 0)
    {
        SequenceNumber = seq;
        Buffer = buffer;
        Length = length;
        SentAt = DateTimeOffset.UtcNow;
        RetryCount = retryCount;
    }

    public RudpSegment WithRetry() => new(SequenceNumber, Buffer, Length, RetryCount + 1);
}

// Channel<T> 기반 락-프리 RUDP 송신 큐 (백프레셔 포함)
public sealed class RudpSendQueue : IDisposable
{
    public const int DefaultCapacity = 1024;
    public const int MaxRetries = 5;

    // Channel<T>: 락-프리 큐로 구현 — 다수 송신 스레드(SingleWriter=false) → 단일 소비자(SingleReader=true) 경로에서
    // Monitor 락 없이 Interlocked/대기 큐로 전달. RudpSegment가 struct라 큐 내부 저장도 박싱 없이 인라인된다.
    private readonly Channel<RudpSegment> _queue;
    private int _disposed;

    public RudpSendQueue(int capacity = DefaultCapacity)
    {
        // CreateBounded: 고정 용량 링버퍼를 미리 할당 → 운영 중 큐 증가에 따른 추가 힙 할당이 없다(예측 가능한 메모리).
        _queue = Channel.CreateBounded<RudpSegment>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,  // 백프레셔: 가득 차면 WriteAsync가 대기 → 무한 큐 증가 방지
            SingleReader = true,                     // 소비자 1개 보장 시 채널이 더 가벼운 무락 경로 사용
            SingleWriter = false,
            AllowSynchronousContinuations = false    // 완료 콜백을 다른 스레드로 분리 → 생산자 스레드가 소비자 로직에 점유되지 않음
        });
    }

    public ValueTask EnqueueAsync(RudpSegment segment, CancellationToken ct = default) =>
        _queue.Writer.WriteAsync(segment, ct);

    public ValueTask<RudpSegment> DequeueAsync(CancellationToken ct = default) =>
        _queue.Reader.ReadAsync(ct);

    public bool TryDequeue(out RudpSegment segment) =>
        _queue.Reader.TryRead(out segment);

    public int Count => _queue.Reader.Count;

    public void Dispose()
    {
        // Interlocked.Exchange: 이전 값을 원자적으로 반환 → 첫 호출자만 진행(멱등 Dispose)
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        // 큐에 남은 세그먼트의 풀 버퍼를 모두 반납 — 미반납 시 ArrayPool이 고갈되어 이후 Rent가 새 할당으로 퇴화(누수성 GC 압력)
        while (_queue.Reader.TryRead(out var segment))
            ArrayPool<byte>.Shared.Return(segment.Buffer);
    }
}
