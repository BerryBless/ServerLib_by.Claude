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
        _socket.NoDelay = true;  // Nagle 알고리즘 비활성화 — 소량 실시간 패킷의 ~200ms 지연 방지 (클라이언트도 저지연 필수)

        // ConnectAsync: DNS 해석(호스트명일 때)+TCP 3-way 핸드셰이크를 비동기로 — 동기 Connect()의 스레드 블로킹 회피
        await _socket.ConnectAsync(host, port, cancellationToken);

        _pipe = new Pipe();
        _cts = new CancellationTokenSource();

        // fill/read 두 루프는 _cts로 자체 수명·취소를 관리하므로 await 없이 분리 구동(fire-and-forget)
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
        await _socket.SendAsync(data, SocketFlags.None, cancellationToken);
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
    }
}
