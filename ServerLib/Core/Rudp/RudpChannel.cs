using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ServerLib.Core.Rudp;

// RUDP 채널: UDP 위에 시퀀스 번호 + ACK + 재전송 타이머를 구현
// 헤더 레이아웃 (8바이트): [Seq(4)] [AckSeq(4)]
public sealed class RudpChannel : IAsyncDisposable
{
    private const int HeaderSize = 8;
    private static readonly TimeSpan RetransmitInterval = TimeSpan.FromMilliseconds(100);

    private readonly UdpClient _udp;
    private readonly RudpSendQueue _sendQueue = new();
    private readonly RudpRecvWindow _recvWindow = new();
    private readonly CancellationTokenSource _cts = new();
    private int _sendSeqRaw;  // Interlocked용 int, uint로 캐스팅하여 사용
    private int _disposed;

    public IPEndPoint RemoteEndPoint { get; }

    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    public RudpChannel(UdpClient udp, IPEndPoint remoteEndPoint)
    {
        _udp = udp;
        RemoteEndPoint = remoteEndPoint;
    }

    public void Start()
    {
        _ = SendLoopAsync(_cts.Token);
        _ = ReceiveLoopAsync(_cts.Token);
    }

    // 신뢰 전송: 시퀀스 번호 부여 후 재전송 큐에 등록
    public async ValueTask SendReliableAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var seq = (uint)Interlocked.Increment(ref _sendSeqRaw) - 1;
        var totalSize = HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);

        WriteHeader(buffer, seq, _recvWindow.ExpectedSeq);
        payload.Span.CopyTo(buffer.AsSpan(HeaderSize));

        await _sendQueue.EnqueueAsync(new RudpSegment(seq, buffer, totalSize), ct);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RudpSegment segment = default;
            bool hasSegment = false;
            try
            {
                segment = await _sendQueue.DequeueAsync(ct);
                hasSegment = true;
                await _udp.SendAsync(segment.Buffer.AsMemory(0, segment.Length), RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { }
            finally
            {
                if (hasSegment && segment.Buffer is not null)
                    ArrayPool<byte>.Shared.Return(segment.Buffer);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _udp.Client;
        var recvBuffer = ArrayPool<byte>.Shared.Rent(65536);
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveFromAsync(
                        recvBuffer.AsMemory(), SocketFlags.None, remote, ct);
                    int received = result.ReceivedBytes;
                    if (received < HeaderSize) continue;

                    var seq = ReadUint32(recvBuffer, 0);
                    if (_recvWindow.OnReceive(seq, out _) && OnReceived != null)
                    {
                        // 페이로드만 복사하여 소유권 이전 (recvBuffer 재사용 보장)
                        var payload = new byte[received - HeaderSize];
                        recvBuffer.AsSpan(HeaderSize, received - HeaderSize).CopyTo(payload);
                        await OnReceived(payload.AsMemory());
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(recvBuffer);
        }
    }

    private static void WriteHeader(byte[] buf, uint seq, uint ackSeq)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), ackSeq);
    }

    private static uint ReadUint32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync();
        _sendQueue.Dispose();
        _cts.Dispose();
    }
}
