using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

public sealed class SocketPipelineClient : IClientConnection
{
    private static readonly int MinBufferSize = 4096;

    private Socket? _socket;
    private Pipe? _pipe;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public bool IsConnected => _socket?.Connected ?? false;
    public Func<ValueTask>? OnConnected { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.NoDelay = true;

        await _socket.ConnectAsync(host, port, cancellationToken);

        _pipe = new Pipe();
        _cts = new CancellationTokenSource();

        _ = FillPipeAsync(_cts.Token);
        _ = ReadPipeAsync(_cts.Token);

        if (OnConnected != null)
            await OnConnected();
    }

    // Zero-copy: 소켓 → PipeWriter
    private async Task FillPipeAsync(CancellationToken ct)
    {
        var writer = _pipe!.Writer;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(MinBufferSize);
                int bytesRead = await _socket!.ReceiveAsync(memory, SocketFlags.None, ct);
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

    // 패킷 프레이밍: 완전한 패킷 단위로 OnReceived 호출
    private async Task ReadPipeAsync(CancellationToken ct)
    {
        var reader = _pipe!.Reader;
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
                    if (OnReceived != null)
                    {
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
                    consumed = buffer.Start;
                }

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

    private static bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet)
    {
        if (buffer.Length < PacketPool.HeaderSize) { packet = default; return false; }

        Span<byte> headerBuf = stackalloc byte[PacketPool.HeaderSize];
        buffer.Slice(0, PacketPool.HeaderSize).CopyTo(headerBuf);

        if (!PacketPool.TryParseHeader(headerBuf, out _, out int bodyLength)) { packet = default; return false; }

        int totalLength = PacketPool.HeaderSize + bodyLength;
        if (buffer.Length < totalLength) { packet = default; return false; }

        packet = buffer.Slice(0, totalLength);
        buffer = buffer.Slice(totalLength);
        return true;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_socket == null) throw new InvalidOperationException("Not connected.");
        await _socket.SendAsync(data, SocketFlags.None, cancellationToken);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _socket?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_cts != null) await _cts.CancelAsync();
        _socket?.Dispose();
        _cts?.Dispose();
    }
}
