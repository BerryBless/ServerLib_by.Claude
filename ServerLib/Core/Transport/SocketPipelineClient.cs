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
    private long _rttTicks; // 마지막 RTT(ticks) — Volatile로 갱신/읽기
    // SemaphoreSlim: 송신 경로 직렬화 — 커널 전환 없이 스핀 후 관리형 대기로 전환하는 경량 게이트.
    // PING 루프와 앱 SendAsync가 동일 소켓에 동시 기록하는 것을 막아 Thread-safe 계약을 보장한다.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public bool IsConnected => _socket?.Connected ?? false;
    public TimeSpan? PingInterval { get; set; }
    // Volatile.Read: 수신 루프(writer)와 앱(reader) 간 최신 RTT 가시성 보장
    public TimeSpan Rtt => new TimeSpan(Volatile.Read(ref _rttTicks));
    public Func<ValueTask>? OnConnected { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.NoDelay = true;  // Nagle 알고리즘 비활성화 — 소량 실시간 패킷의 ~200ms 지연 방지 (클라이언트도 저지연 필수)

        // ConnectAsync: DNS 해석(호스트명일 때)+TCP 3-way 핸드셰이크를 비동기로 — 동기 Connect()의 스레드 블로킹 회피
        await _socket.ConnectAsync(host, port, cancellationToken);

        _pipe = new Pipe();
        _cts = new CancellationTokenSource();

        // fill/read 두 루프는 _cts로 자체 수명·취소를 관리하므로 await 없이 분리 구동(fire-and-forget)
        _ = FillPipeAsync(_cts.Token);
        _ = ReadPipeAsync(_cts.Token);

        // PingInterval이 설정된 경우에만 하트비트 루프 시작
        if (PingInterval.HasValue)
            _ = PingLoopAsync(PingInterval.Value, _cts.Token);

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
                // GetMemory + ReceiveAsync(Memory): PipeWriter 풀 버퍼에 커널이 직접 수신 — byte[] 오버로드와 달리 수신마다 무할당(zero-copy)
                var memory = writer.GetMemory(MinBufferSize);
                int bytesRead = await _socket!.ReceiveAsync(memory, SocketFlags.None, ct);
                if (bytesRead == 0) break; // 0바이트 = 서버의 정상 종료

                writer.Advance(bytesRead); // 쓰기 위치만 커밋
                // FlushAsync: reader를 깨우고 백프레셔 적용 — reader가 느리면 수신을 멈춰 Pipe 무한 증가 방지. IsCompleted로 종료 감지
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

    // 주기적으로 PING을 송신한다. 송신 버퍼는 1회 대여해 재사용(steady-state 무할당).
    private async Task PingLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        var buf = ArrayPool<byte>.Shared.Rent(HeartbeatProtocol.MaxPacketSize);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                int written = HeartbeatProtocol.BuildPing(DateTimeOffset.UtcNow.UtcTicks, buf);
                try { await SendAsync(buf.AsMemory(0, written), ct); }
                catch (ObjectDisposedException) { break; }
                catch (System.Net.Sockets.SocketException) { }
            }
        }
        catch (OperationCanceledException) { }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // 동기 헬퍼: packet이 PONG이면 RTT를 계산해 _rttTicks를 갱신하고 true. 아니면 false.
    private bool TryHandlePong(ReadOnlySequence<byte> packet)
    {
        if (packet.Length > HeartbeatProtocol.MaxPacketSize) return false;
        Span<byte> tmp = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int len = (int)packet.Length;
        packet.CopyTo(tmp);
        if (HeartbeatProtocol.TryComputeRtt(tmp[..len], DateTimeOffset.UtcNow.UtcTicks, out long rtt))
        {
            Volatile.Write(ref _rttTicks, rtt);
            return true;
        }
        return false;
    }

    // 패킷 프레이밍: 완전한 패킷 단위로 OnReceived 호출
    private async Task ReadPipeAsync(CancellationToken ct)
    {
        var reader = _pipe!.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ReadAsync가 돌려주는 ReadOnlySequence는 Pipe 세그먼트를 그대로 참조(zero-copy)
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                while (TryReadPacket(ref buffer, out var packet))
                {
                    // 예약 ID 가로채기: PONG이면 RTT만 갱신하고 앱 OnReceived는 호출하지 않는다.
                    if (TryHandlePong(packet))
                    {
                        consumed = buffer.Start;
                        continue;
                    }
                    if (OnReceived != null)
                    {
                        // Fast-path: 대부분 패킷은 단일 세그먼트(연속 메모리) → ArrayPool 대여 없이 그대로 콜백(무할당)
                        if (packet.IsSingleSegment)
                        {
                            await OnReceived(packet.First);
                        }
                        else
                        {
                            // 세그먼트 경계에 걸친 드문 경우만 연속 버퍼로 병합 → 영구 할당 대신 ArrayPool 임대로 GC 압력 억제
                            var length = (int)packet.Length;
                            var rented = ArrayPool<byte>.Shared.Rent(length);
                            try
                            {
                                packet.CopyTo(rented);
                                await OnReceived(rented.AsMemory(0, length));
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(rented); // 풀에 반납하여 재사용
                            }
                        }
                    }
                    consumed = buffer.Start;
                }

                // AdvanceTo(consumed, examined): examined까지 "봤으나 미완성"인 부분 패킷을 Pipe가 보존 → 다음 ReadAsync에서 이어붙음
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

        // stackalloc: 헤더(최대 4바이트)를 스택 버퍼로 복사 — 패킷마다 힙 할당 없이 처리
        Span<byte> headerBuf = stackalloc byte[PacketPool.HeaderSize];
        buffer.Slice(0, PacketPool.HeaderSize).CopyTo(headerBuf); // 멀티세그먼트여도 중간 버퍼 없이 세그먼트별 직접 복사

        if (!PacketPool.TryParseHeader(headerBuf, out _, out int bodyLength)) { packet = default; return false; }

        int totalLength = PacketPool.HeaderSize + bodyLength;
        if (buffer.Length < totalLength) { packet = default; return false; }

        packet = buffer.Slice(0, totalLength);
        buffer = buffer.Slice(totalLength);
        return true;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // Volatile.Read: DisposeAsync와 동시 호출 경합 시 해제 플래그의 최신값을 관찰
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_socket == null) throw new InvalidOperationException("Not connected.");
        // 게이트로 송신을 직렬화: 동일 소켓에 대한 중첩 SendAsync(겹침)는 미지원이므로 한 번에 하나만 기록
        await _sendGate.WaitAsync(cancellationToken);
        try { await _socket.SendAsync(data, SocketFlags.None, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _socket?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        // Interlocked.Exchange: 이전 값을 원자적으로 반환 → 첫 호출자만 진행, 이후 호출은 즉시 반환(멱등 Dispose)
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_cts != null) await _cts.CancelAsync();
        _socket?.Dispose();
        _cts?.Dispose();
        _sendGate.Dispose();
    }
}
