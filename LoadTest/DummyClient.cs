using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;

namespace LoadTest;

public sealed class DummyClient : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly LoadMonitor _monitor;
    private int _disposed;

    public DummyClient(LoadMonitor monitor) => _monitor = monitor;

    public async Task RunAsync(string host, int port, CancellationToken ct)
    {
        await _socket.ConnectAsync(IPAddress.Parse(host), port, ct);
        _monitor.OnClientConnected();

        var buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + 32);
        try
        {
            // 고정 페이로드: 32바이트 더미 데이터
            new Random().NextBytes(buffer.AsSpan(PacketPool.HeaderSize, 32));
            PacketPool.WriteHeader(buffer, packetId: 1, bodyLength: 32);
            var sendMemory = buffer.AsMemory(0, PacketPool.HeaderSize + 32);

            var recvBuffer = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _socket.SendAsync(sendMemory, SocketFlags.None, ct);
                    _monitor.OnPacketSent();

                    // Echo 수신 (서버가 에코 서버인 경우)
                    var received = await _socket.ReceiveAsync(recvBuffer.AsMemory(), SocketFlags.None, ct);
                    if (received == 0) break;
                    _monitor.OnPacketReceived();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recvBuffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _monitor.OnClientDisconnected();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _socket.Dispose();
    }
}
