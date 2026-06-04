using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

public sealed class SocketPipelineSession : ISession
{
    private static readonly int MinBufferSize = 4096;

    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private long _lastReceivedAtTicks;
    private int _state = SessionState.Connecting.Value;
    private object? _context;

    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastReceivedAt => new DateTimeOffset(Interlocked.Read(ref _lastReceivedAtTicks), TimeSpan.Zero);

    public SessionState State => new SessionState(Volatile.Read(ref _state));

    public object? Context
    {
        get => Volatile.Read(ref _context);
        set => Volatile.Write(ref _context, value);
    }

    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }

    public SocketPipelineSession(Socket socket)
    {
        _socket = socket;
        RemoteEndPoint = socket.RemoteEndPoint;
        _pipe = new Pipe();
        var now = DateTimeOffset.UtcNow;
        _lastReceivedAtTicks = now.UtcTicks;
    }

    public bool TransitionTo(SessionState newState)
    {
        // Disconnected는 종착 상태다. 이미 해제된 세션의 부활(예: Disconnected→Authenticated)을 막는다.
        // CAS로 원자적 전환하여 IO 스레드(Disconnected)와 사용자 스레드(Authenticated 등)의 경쟁에서
        // 종착 상태가 덮어써지지 않도록 보존한다.
        int target = newState.Value;
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if (current == SessionState.Disconnected.Value)
                return false; // 종착 상태 — 전환 거부
            if (Interlocked.CompareExchange(ref _state, target, current) == current)
                return true;
        }
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
                // 단일 writer(FillPipeAsync)이므로 Volatile.Write로 충분 (64-bit aligned long)
                Volatile.Write(ref _lastReceivedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

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

    // 패킷 프레이밍: 완전한 패킷 단위로 OnReceived 호출
    private async Task ReadPipeAsync(CancellationToken ct)
    {
        var reader = _pipe.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                while (TryReadPacket(ref buffer, out var packet))
                {
                    await DispatchPacketAsync(packet);
                    consumed = buffer.Start;
                }

                // consumed: 처리 완료된 위치, examined: 검사한 끝까지 (더 많은 데이터 대기)
                reader.AdvanceTo(consumed, examined);

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

    // 헤더를 파싱하여 완전한 패킷 1개를 buffer에서 분리한다.
    private static bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet)
    {
        if (buffer.Length < PacketPool.HeaderSize)
        {
            packet = default;
            return false;
        }

        // 헤더를 스택 버퍼로 복사 (최대 4바이트, Zero-allocation 수준)
        Span<byte> headerBuf = stackalloc byte[PacketPool.HeaderSize];
        buffer.Slice(0, PacketPool.HeaderSize).CopyTo(headerBuf);

        if (!PacketPool.TryParseHeader(headerBuf, out _, out int bodyLength))
        {
            packet = default;
            return false;
        }

        int totalLength = PacketPool.HeaderSize + bodyLength;
        if (buffer.Length < totalLength)
        {
            packet = default;
            return false;
        }

        packet = buffer.Slice(0, totalLength);
        buffer = buffer.Slice(totalLength);
        return true;
    }

    private async ValueTask DispatchPacketAsync(ReadOnlySequence<byte> packet)
    {
        if (OnReceived == null) return;

        if (packet.IsSingleSegment)
        {
            await OnReceived(packet.First);
        }
        else
        {
            var length = (int)packet.Length;
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                packet.CopyTo(rented);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _socket.SendAsync(data, SocketFlags.None, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync();
        _socket.Dispose();
        _cts.Dispose();
        Volatile.Write(ref _context, null); // 민감 데이터 잔류 방지 (CWE-212/459) — 사용자 컨텍스트 참조 해제
    }
}
