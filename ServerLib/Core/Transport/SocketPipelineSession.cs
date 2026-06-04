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

    // PipeOptions(useSynchronizationContext:false): FlushAsync/ReadAsync의 continuation을 캡처된 SynchronizationContext가 아닌
    // ThreadPool에서 실행한다. 기본값(true)이면 호출자가 SyncContext 있는 스레드에서 구동 시 IO continuation이 그 스레드에 고정되어
    // 데드락 위험이 생긴다(ConfigureAwait로는 못 막음 — 스케줄링은 Pipe가 제어). 전 세션 공유라 static readonly 1회 생성.
    private static readonly PipeOptions s_pipeOptions = new(useSynchronizationContext: false);

    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private long _lastReceivedAtTicks;
    private int _state = SessionState.Connecting.Value;
    private object? _context;
    // SemaphoreSlim: 송신 경로 직렬화 — 자동 PONG 회신과 앱 SendAsync가 동일 소켓에 동시 기록하는 것을 막아 Thread-safe 계약을 보장한다.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    // Interlocked.Read: IdleTimeout 스윕 등 다른 스레드가 읽으므로 acquire 배리어로 최신 타임스탬프 가시성 보장 (FillPipeAsync 쓰기와 경합)
    public DateTimeOffset LastReceivedAt => new DateTimeOffset(Interlocked.Read(ref _lastReceivedAtTicks), TimeSpan.Zero);

    // Volatile.Read: IO 스레드 쓰기·앱 스레드 읽기 간 재정렬 방지로 최신 상태값 가시성 보장
    public SessionState State => new SessionState(Volatile.Read(ref _state));

    public object? Context
    {
        // Volatile read/write: 참조를 원자적으로 교체하고 모든 스레드가 최신 컨텍스트를 관찰하도록 보장
        get => Volatile.Read(ref _context);
        set => Volatile.Write(ref _context, value);
    }

    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
    public Func<ValueTask>? OnDisconnected { get; set; }

    /// <summary>송신 1건의 최대 허용 시간입니다. <see langword="null"/>(기본값)이면 비활성화됩니다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>목적:</b> 수신 버퍼를 비우지 않는(죽은/응답불능) 피어로 인해 <see cref="SendAsync"/>가
    /// 무한 블록되어 송신 게이트를 영구 점유하고 <see cref="SessionRegistry.BroadcastAsync"/> 전체를 정지시키는 것을 방지합니다.</description></item>
    /// <item><description><b>동작:</b> 시한 초과 시 <see cref="System.Net.Sockets.SocketException"/>(<see cref="System.Net.Sockets.SocketError.TimedOut"/>)을
    /// throw합니다. 호출자의 명시적 취소(<see cref="OperationCanceledException"/>)와 구분되며, BroadcastAsync 등 호출부의 SocketException 처리와 일관됩니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see langword="null"/>(기본)일 때 송신 경로 Zero-allocation 유지.
    /// 설정 시에만 송신당 <see cref="CancellationTokenSource"/> 1개를 할당합니다(항상-무할당이 필요하면 세션별 송신 큐로 후속 최적화 가능).</description></item>
    /// <item><description><b>Thread Safety:</b> Thread-safe(단순 참조 읽기/쓰기). <see cref="StartReceiving"/> 전후 어느 시점에든 설정 가능합니다.</description></item>
    /// </list>
    /// </remarks>
    public TimeSpan? SendTimeout { get; set; }

    public SocketPipelineSession(Socket socket)
    {
        _socket = socket;
        RemoteEndPoint = socket.RemoteEndPoint;
        _pipe = new Pipe(s_pipeOptions);
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
        // fill/read 두 루프는 각자 _cts로 수명·취소를 관리하므로 await 없이 분리 구동(fire-and-forget)해도 안전
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
                // GetMemory + ReceiveAsync(Memory): PipeWriter 내부 풀 버퍼에 커널이 직접 수신.
                // byte[] 오버로드와 달리 수신마다 힙 할당이 없다(zero-copy).
                var memory = writer.GetMemory(MinBufferSize);
                int bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None, ct);
                if (bytesRead == 0) break; // 0바이트 = 상대의 정상 종료(graceful close)
                // 단일 writer(FillPipeAsync)이므로 Volatile.Write로 충분 (64-bit aligned long)
                Volatile.Write(ref _lastReceivedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

                writer.Advance(bytesRead); // 쓰기 위치만 커밋 (아직 reader에 신호 안 함)
                // FlushAsync: reader를 깨우고 백프레셔 적용 — reader가 느리면 수신을 멈춰 Pipe 무한 증가 방지.
                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted) break; // reader 측이 Pipe를 완료(종료)함
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
                // ReadAsync가 돌려주는 ReadOnlySequence는 Pipe 세그먼트를 그대로 참조(zero-copy) — 누적 데이터를 복사 없이 노출
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                while (TryReadPacket(ref buffer, out var packet))
                {
                    await DispatchPacketAsync(packet);
                    consumed = buffer.Start;
                }

                // AdvanceTo(consumed, examined): consumed까지는 버려도 되지만 examined까지는 "봤으나 미완성"이라
                // Pipe가 보존하게 한다 → 패킷이 세그먼트 경계에 걸쳐 분할 도착해도 다음 ReadAsync에서 이어붙는다(부분 패킷 프레이밍 핵심).
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
        // 예약 ID 가로채기: PING이면 PONG을 회신하고 앱 OnReceived는 호출하지 않는다.
        // (stackalloc이 await를 넘지 못하므로 동기 헬퍼에서 풀 버퍼로 빌드 후 여기서 송신)
        var pongBuf = TryBuildPongBuffer(packet, out int pongLen);
        if (pongBuf != null)
        {
            try { await SendAsync(pongBuf.AsMemory(0, pongLen)); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            finally { ArrayPool<byte>.Shared.Return(pongBuf); }
            return;
        }

        if (OnReceived == null) return;

        // Fast-path: 대부분의 패킷은 단일 세그먼트(Pipe 버퍼 내 연속 메모리)이므로
        // ArrayPool 대여 없이 First 슬라이스를 그대로 콜백에 넘긴다(무할당).
        if (packet.IsSingleSegment)
        {
            await OnReceived(packet.First);
        }
        else
        {
            // 세그먼트 경계에 걸친 드문 경우만 연속 버퍼로 병합 필요 → 영구 배열 할당 대신 ArrayPool 임대로 GC 압력 억제.
            var length = (int)packet.Length;
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                packet.CopyTo(rented);
                await OnReceived(rented.AsMemory(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented); // 풀에 반납하여 다음 멀티세그먼트 패킷이 재사용
            }
        }
    }

    // 동기 헬퍼: packet이 PING이면 PONG을 풀 버퍼에 빌드해 반환(written>0), 아니면 null.
    // stackalloc을 async 메서드(await 경계)에서 분리하기 위해 동기로 둔다.
    private static byte[]? TryBuildPongBuffer(ReadOnlySequence<byte> packet, out int written)
    {
        written = 0;
        if (packet.Length > HeartbeatProtocol.MaxPacketSize) return null; // 하트비트는 12B 고정, 더 크면 일반 패킷
        Span<byte> tmp = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int len = (int)packet.Length;
        packet.CopyTo(tmp);
        Span<byte> pong = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int w = HeartbeatProtocol.TryBuildPong(tmp[..len], pong);
        if (w == 0) return null;
        var buf = ArrayPool<byte>.Shared.Rent(w);
        pong[..w].CopyTo(buf);
        written = w;
        return buf;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // Volatile.Read: DisposeAsync와 동시 호출 경합 시 해제 플래그의 최신값을 관찰
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        // 게이트로 송신을 직렬화: 자동 PONG 회신과 앱 송신이 동일 소켓에 겹쳐 기록되지 않도록 한 번에 하나만
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeout = SendTimeout;
            if (timeout is null)
            {
                await _socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // CancellationTokenSource: 송신당 시한. 기본 경로(timeout=null)는 CTS를 만들지 않아 무할당 유지.
                // caller 토큰이 취소 가능할 때만 링크(드문 경로) — 죽은 피어가 게이트를 영구 점유하는 것을 시한으로 차단.
                using var cts = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : new CancellationTokenSource();
                cts.CancelAfter(timeout.Value);
                try { await _socket.SendAsync(data, SocketFlags.None, cts.Token).ConfigureAwait(false); }
                // 내부 시한 만료(caller 취소가 아님)는 SocketException(TimedOut)으로 변환 → BroadcastAsync 등 호출부의
                // SocketException 처리와 일관되게 하여 죽은 피어만 끊고 브로드캐스트 전체는 계속 진행되도록 한다.
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new SocketException((int)SocketError.TimedOut); }
            }
        }
        finally { _sendGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Interlocked.Exchange: 이전 값을 원자적으로 반환 → 첫 호출자만 진행, 이후 호출은 즉시 반환(멱등 Dispose)
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // ConfigureAwait(false): Stop()이 sync-over-async(GetAwaiter().GetResult())로 이 메서드를 블록하므로,
        // continuation을 캡처된 SyncContext로 되돌리면 이미 블록된 스레드와 데드락한다 → ThreadPool 재개로 회피.
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        _cts.Dispose();
        Volatile.Write(ref _context, null); // 민감 데이터 잔류 방지 (CWE-212/459) — 사용자 컨텍스트 참조 해제
        _sendGate.Dispose();
    }
}
