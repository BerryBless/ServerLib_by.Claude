using System.Buffers;
using System.Threading.Channels;

namespace ServerLib.Core.Rudp;

public readonly struct RudpSegment
{
    public readonly uint SequenceNumber;
    public readonly byte[] Buffer;    // ArrayPool 대여 버퍼
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

    private readonly Channel<RudpSegment> _queue;
    private int _disposed;

    public RudpSendQueue(int capacity = DefaultCapacity)
    {
        _queue = Channel.CreateBounded<RudpSegment>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,  // 백프레셔: 가득 차면 대기
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out var segment))
            ArrayPool<byte>.Shared.Return(segment.Buffer);
    }
}
