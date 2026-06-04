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
        // Interlocked.Increment: 다수 송신 스레드가 경쟁해도 시퀀스 번호를 원자적으로 유일하게 발급(락 없이 CAS 기반 증가).
        var seq = (uint)Interlocked.Increment(ref _sendSeqRaw) - 1;
        var totalSize = HeaderSize + payload.Length;
        // ArrayPool.Rent: 헤더+페이로드용 버퍼를 풀에서 대여 — 송신마다 new byte[]를 피해 GC 압력 억제(SendLoop에서 Return으로 반납).
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);

        WriteHeader(buffer, seq, _recvWindow.ExpectedSeq);
        // CopyTo = 깊은복사: 호출자 payload를 풀 버퍼로 복제. 재전송 큐는 payload 수명을 넘어 버퍼를 보유해야 하므로
        // 얕은 참조로는 안 되고 독립 소유 버퍼에 실제 내용을 담아야 한다.
        payload.Span.CopyTo(buffer.AsSpan(HeaderSize));

        // RudpSegment는 struct(얕은복사로 큐에 인라인 저장), buffer 참조만 전달 — 반납 책임은 SendLoop으로 이전된다.
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
                // AsMemory = 얕은복사: 풀 버퍼의 [0,Length) 구간 뷰만 만들어 소켓에 전달(복제 없음, zero-copy 송신).
                await _udp.SendAsync(segment.Buffer.AsMemory(0, segment.Length), RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { }
            finally
            {
                // 송신 완료(또는 실패) 후 풀 버퍼 반납 — SendReliableAsync가 Rent한 버퍼의 소유권이 여기서 종료된다.
                if (hasSegment && segment.Buffer is not null)
                    ArrayPool<byte>.Shared.Return(segment.Buffer);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _udp.Client;
        // ArrayPool.Rent(64KB): 수신 버퍼를 루프 시작 시 1회만 대여하고 모든 수신에 재사용 → 패킷마다 할당하지 않음(GC 무압력).
        var recvBuffer = ArrayPool<byte>.Shared.Rent(65536);
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // AsMemory = 얕은복사: 재사용 수신 버퍼 전체를 가리키는 뷰만 소켓에 넘긴다(복제 없음).
                    var result = await socket.ReceiveFromAsync(
                        recvBuffer.AsMemory(), SocketFlags.None, remote, ct);
                    int received = result.ReceivedBytes;
                    if (received < HeaderSize) continue;

                    var seq = ReadUint32(recvBuffer, 0);
                    if (_recvWindow.OnReceive(seq, out _) && OnReceived != null)
                    {
                        // new byte[] = 깊은복사로 소유권 분리: recvBuffer는 다음 루프에서 즉시 덮어쓰이므로,
                        // 콜백이 보관·비동기 처리할 데이터는 독립 소유의 새 배열이어야 한다(얕은 뷰를 넘기면 다음 수신에 훼손됨).
                        var payload = new byte[received - HeaderSize];
                        recvBuffer.AsSpan(HeaderSize, received - HeaderSize).CopyTo(payload); // span 슬라이스에서 새 배열로 실제 복제
                        await OnReceived(payload.AsMemory()); // AsMemory는 새 배열의 얕은 뷰 — 이미 독립 소유라 안전
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(recvBuffer); // 루프 종료 시 64KB 수신 버퍼를 풀에 반납(미반납 시 풀 고갈)
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
