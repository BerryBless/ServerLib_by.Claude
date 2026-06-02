using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

public sealed class SocketPipelineSession : ISession
{
    private static readonly int MinBufferSize = 4096;

    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }

    public SocketPipelineSession(Socket socket)
    {
        _socket = socket;
        RemoteEndPoint = socket.RemoteEndPoint;
        _pipe = new Pipe();
    }

    public void StartReceiving()
    {
        _ = FillPipeAsync(_cts.Token);
        _ = ReadPipeAsync(_cts.Token);
    }

    // Zero-copy: 소켓 → PipeWriter (중간 복사 없음)
    private async Task FillPipeAsync(CancellationToken ct)
    {
        var writer = _pipe.Writer;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(MinBufferSize);
                int bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None, ct);
                if (bytesRead == 0) break;

                writer.Advance(bytesRead);
                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    // Zero-copy: PipeReader → ReadOnlySequence<byte> → 콜백 슬라이스 (복사 없음)
    private async Task ReadPipeAsync(CancellationToken ct)
    {
        var reader = _pipe.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                await ProcessBufferAsync(buffer);

                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await reader.CompleteAsync();
            if (OnDisconnected != null)
                await OnDisconnected();
        }
    }

    private async ValueTask ProcessBufferAsync(ReadOnlySequence<byte> buffer)
    {
        if (OnReceived == null) return;

        // 연속 세그먼트는 복사 없이 First 슬라이스로 전달
        if (buffer.IsSingleSegment)
        {
            await OnReceived(buffer.First);
        }
        else
        {
            // 분산 세그먼트: ArrayPool 임시 버퍼로 병합 (최소 복사)
            var length = (int)buffer.Length;
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                buffer.CopyTo(rented);
                await OnReceived(rented.AsMemory(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        await _socket.SendAsync(data, SocketFlags.None, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync();
        _socket.Dispose();
        _cts.Dispose();
    }
}
