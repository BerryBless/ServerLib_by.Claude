using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

// 구현 은닉(internal): 외부 소비자는 콜백으로 전달되는 ISession으로만 사용한다.
internal sealed class SocketPipelineSession : ISession
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
    private bool _receiving; // StartReceiving 이후 true — 콜백 재설정 차단(콜백은 수신 시작 전에 배선되어야 함)
    private long _lastReceivedAtTicks;
    private long _lastProgressAtTicks; // B3: 마지막 "완전한 패킷" 프레이밍 시각 — slowloris 회피 방지용 유휴 기준
    private int _state = SessionState.Connecting.Value;
    private object? _context;
    // SemaphoreSlim: 송신 경로 직렬화 — 자동 PONG 회신과 앱 SendAsync가 동일 소켓에 동시 기록하는 것을 막아 Thread-safe 계약을 보장한다.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    // CancellationTokenSource(재사용): 송신 시한용. _sendGate가 세션당 동시 송신 1건을 보장하므로 단일 인스턴스를
    // TryReset()으로 매 송신 재사용한다 → 송신마다 new CTS + 내부 Timer를 할당하던 정상부하 GC(실측 패킷당 160B)를 제거.
    // caller 토큰과 링크하지 않음(A-max): 링크 CTS·Timer 신규 할당을 피해 송신당 Zero-allocation.
    private CancellationTokenSource? _sendTimeoutCts;

    public Guid SessionId { get; } = Guid.NewGuid();
    public EndPoint? RemoteEndPoint { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    // Interlocked.Read: IdleTimeout 스윕 등 다른 스레드가 읽으므로 acquire 배리어로 최신 타임스탬프 가시성 보장 (FillPipeAsync 쓰기와 경합)
    public DateTimeOffset LastReceivedAt => new DateTimeOffset(Interlocked.Read(ref _lastReceivedAtTicks), TimeSpan.Zero);

    // Interlocked.Read: 스윕 스레드가 읽고 수신 스레드(ReadPipeAsync)가 쓰므로 원자적 가시성 보장.
    public DateTimeOffset LastProgressAt => new DateTimeOffset(Interlocked.Read(ref _lastProgressAtTicks), TimeSpan.Zero);

    // Volatile.Read: IO 스레드 쓰기·앱 스레드 읽기 간 재정렬 방지로 최신 상태값 가시성 보장
    public SessionState State => new SessionState(Volatile.Read(ref _state));

    public object? Context
    {
        // Volatile read/write: 참조를 원자적으로 교체하고 모든 스레드가 최신 컨텍스트를 관찰하도록 보장
        get => Volatile.Read(ref _context);
        set => Volatile.Write(ref _context, value);
    }

    // E3: 콜백은 StartReceiving() 전에만 설정 — 라이브러리(Listener)가 수신 시작 전에 배선하며, 이후 재할당은 차단된다.
    private Func<ReadOnlyMemory<byte>, ValueTask>? _onReceived;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived
    {
        get => _onReceived;
        set
        {
            if (_receiving) throw new InvalidOperationException("OnReceived는 StartReceiving() 호출 전에만 설정할 수 있습니다.");
            _onReceived = value;
        }
    }

    private Func<ValueTask>? _onDisconnected;
    public Func<ValueTask>? OnDisconnected
    {
        get => _onDisconnected;
        set
        {
            if (_receiving) throw new InvalidOperationException("OnDisconnected는 StartReceiving() 호출 전에만 설정할 수 있습니다.");
            _onDisconnected = value;
        }
    }

    private Func<Exception, ValueTask>? _onReceiveError;
    public Func<Exception, ValueTask>? OnReceiveError
    {
        get => _onReceiveError;
        set
        {
            if (_receiving) throw new InvalidOperationException("OnReceiveError는 StartReceiving() 호출 전에만 설정할 수 있습니다.");
            _onReceiveError = value;
        }
    }

    /// <summary>송신 1건의 최대 허용 시간입니다. <see langword="null"/>(기본값)이면 비활성화됩니다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>목적:</b> 수신 버퍼를 비우지 않는(죽은/응답불능) 피어로 인해 <see cref="SendAsync"/>가
    /// 무한 블록되어 송신 게이트를 영구 점유하고 <see cref="SessionRegistry.BroadcastAsync"/> 전체를 정지시키는 것을 방지합니다.</description></item>
    /// <item><description><b>동작:</b> 시한 초과 시 <see cref="System.Net.Sockets.SocketException"/>(<see cref="System.Net.Sockets.SocketError.TimedOut"/>)을
    /// throw합니다. 호출자의 명시적 취소(<see cref="OperationCanceledException"/>)와 구분되며, BroadcastAsync 등 호출부의 SocketException 처리와 일관됩니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 설정 여부와 무관하게 송신당 Zero-allocation입니다.
    /// 설정 시 시한용 <see cref="CancellationTokenSource"/>를 세션 수명 동안 1개만 두고 <see cref="CancellationTokenSource.TryReset"/>로 매 송신 재사용합니다
    /// (송신 게이트가 세션당 동시 송신 1건을 보장하므로 안전). 직전 시한이 발화한 직후 송신에서만 드물게 1개를 재생성합니다.</description></item>
    /// <item><description><b>Cancellation 계약:</b> 설정 시 <see cref="SendAsync"/>의 <c>cancellationToken</c>은 송신 게이트 대기를 취소합니다.
    /// 단, 이미 시작된(in-flight) 소켓 쓰기는 caller 토큰이 아니라 이 시한으로 bound됩니다(즉시 끊기지 않고 시한 내로 종료). 시한 초과 시 <see cref="System.Net.Sockets.SocketException"/>(TimedOut).</description></item>
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
        _lastProgressAtTicks = now.UtcTicks; // 진척 기준 초기값 = ConnectedAt (아직 패킷 없음 → 침묵 연결도 타임아웃 대상)
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
        _receiving = true; // 이후 콜백 재설정 차단(콜백은 StartReceiving 전에 배선되어야 함)
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

                while (TryReadPacket(ref buffer, out var packet, out var packetId))
                {
                    // B3: 완전한 패킷이 프레이밍될 때만 진척 시각을 갱신한다(PING 포함). 바이트만 흘리고 패킷을
                    // 완성하지 않는 trickle 공격은 이 값을 갱신하지 못해 유휴 스윕에 정리된다(byte 기준 LastReceivedAt과 구분).
                    Volatile.Write(ref _lastProgressAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                    try
                    {
                        await DispatchPacketAsync(packet, packetId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A3: 손상/악성 본문 디코드 실패(SpanReader EndOfStreamException 등)나 앱 OnReceived 핸들러 예외를
                        // 패킷 단위로 격리한다. 이 catch가 없으면 예외가 fire-and-forget인 이 루프 밖으로 새어 나가
                        // 미관측 Task 예외가 되고, 수신이 조용히 멈춘 좀비 세션이 남는다.
                        // 프로토콜 위반/핸들러 실패는 해당 세션만 정상 종료한다(아래 finally → OnDisconnected → DisposeAsync로
                        // 소켓·FillPipe 루프까지 정리). 종료 원인을 정상/유휴 종료와 구분하도록 OnReceiveError로 통지한다.
                        var onError = OnReceiveError;
                        if (onError != null)
                        {
                            // 통지 콜백 자체의 예외가 정리 경로를 막지 않도록 격리한다.
                            try { await onError(ex).ConfigureAwait(false); }
                            catch { /* 통지 실패는 무시 — 세션 정리는 계속 진행 */ }
                        }
                        return; // 메서드 종료 → finally가 reader.CompleteAsync + OnDisconnected 수행
                    }
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

    // 헤더를 파싱하여 완전한 패킷 1개를 buffer에서 분리한다. 파싱한 packetId를 함께 반환해
    // 호출부가 재파싱 없이 PING 여부를 분기하도록 한다(P8: 매 패킷 PONG 프로브 제거).
    private static bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet, out ushort packetId)
    {
        packetId = 0;
        if (buffer.Length < PacketPool.HeaderSize)
        {
            packet = default;
            return false;
        }

        // 헤더를 스택 버퍼로 복사 (최대 4바이트, Zero-allocation 수준)
        Span<byte> headerBuf = stackalloc byte[PacketPool.HeaderSize];
        buffer.Slice(0, PacketPool.HeaderSize).CopyTo(headerBuf);

        if (!PacketPool.TryParseHeader(headerBuf, out packetId, out int bodyLength))
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

    // P2: 자신을 async로 만들지 않고(=패킷당 상태머신 박싱 제거) 동기 완료 가능 경로에서는 inner ValueTask를 그대로 반환한다.
    // 단일 세그먼트 + 동기 핸들러면 전 경로 무할당. PONG·멀티세그먼트(드묾)만 풀 버퍼 반납 때문에 async 헬퍼로 위임한다.
    private ValueTask DispatchPacketAsync(ReadOnlySequence<byte> packet, ushort packetId)
    {
        // 예약 ID 가로채기: PING일 때만 PONG을 회신한다(앱 OnReceived 미호출).
        // P8: TryReadPacket이 이미 파싱한 packetId로 직접 분기 → PING이 아닌 패킷은 PONG 프로브(stackalloc·CopyTo·헤더 재파싱)를 건너뛴다.
        if (HeartbeatProtocol.IsPing(packetId))
        {
            var pongBuf = TryBuildPongBuffer(packet, out int pongLen);
            if (pongBuf != null)
                return SendPongAsync(pongBuf, pongLen);
            // PING ID지만 본문이 손상되어 PONG 빌드 실패 시: 기존 동작과 동일하게 아래 일반 경로로 떨어진다.
        }

        // 콜백을 지역 변수로 1회만 읽어 IO 스레드 도중 재할당/null 경합을 배제(이전엔 null 검사·호출에서 2회 읽음).
        var onReceived = OnReceived;
        if (onReceived == null) return ValueTask.CompletedTask;

        // Fast-path: 단일 세그먼트면 ArrayPool 없이 First 슬라이스를 콜백에 넘기고 그 ValueTask를 그대로 반환한다.
        // 콜백이 동기 완료하면 무할당, 비동기면 그건 사용자 핸들러의 상태머신(우리가 추가하는 박싱은 없음).
        if (packet.IsSingleSegment)
            return onReceived(packet.First);

        // 세그먼트 경계에 걸친 드문 경우만 연속 버퍼로 병합 필요 → async 헬퍼에서 ArrayPool 임대/반납.
        return DispatchMultiSegmentAsync(onReceived, packet);
    }

    // PING 가로채기 응답: stackalloc이 빌드한 PONG 풀 버퍼를 송신 후 반드시 반납한다.
    private async ValueTask SendPongAsync(byte[] pongBuf, int pongLen)
    {
        try { await SendAsync(pongBuf.AsMemory(0, pongLen)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally { ArrayPool<byte>.Shared.Return(pongBuf); } // 영구 할당 대신 풀 임대였으므로 반드시 반납(미반납 시 풀 고갈)
    }

    // 멀티세그먼트 병합: 영구 배열 할당 대신 ArrayPool 임대로 GC 압력 억제. CopyTo는 await 이전(동기)이라 packet 수명과 무관하게 안전.
    private static async ValueTask DispatchMultiSegmentAsync(Func<ReadOnlyMemory<byte>, ValueTask> onReceived, ReadOnlySequence<byte> packet)
    {
        var length = (int)packet.Length;
        // ArrayPool<byte>.Shared.Rent: 드문 멀티세그먼트 경로의 연속 버퍼 — 콜백 반환 후 즉시 반납해 재사용(GC 무압력).
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            packet.CopyTo(rented);
            await onReceived(rented.AsMemory(0, length)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented); // 풀에 반납하여 다음 멀티세그먼트 패킷이 재사용
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
                // 시한 미설정: CTS 없이 caller 토큰 직접 사용(무할당).
                await _socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 재사용 CTS 확보: TryReset() 성공 시 기존 CTS+내부 Timer를 재사용 → 송신당 무할당.
                // 실패(직전 시한이 발화해 이미 취소됨)는 죽은 피어 teardown 경로라 드묾 → 그때만 새로 생성.
                var cts = _sendTimeoutCts;
                if (cts is null || !cts.TryReset())
                {
                    cts?.Dispose();
                    cts = _sendTimeoutCts = new CancellationTokenSource();
                }
                cts.CancelAfter(timeout.Value); // 내부 Timer를 Change로 재무장 — 새 Timer 할당 없음
                try
                {
                    // A-max: in-flight 소켓 쓰기는 cts(시한)만 관찰. caller 취소는 위 _sendGate.WaitAsync에서 존중하고,
                    // 진행 중 송신은 시한으로 bound한다(caller 토큰이 in-flight 송신을 즉시 끊지 않음).
                    await _socket.SendAsync(data, SocketFlags.None, cts.Token).ConfigureAwait(false);
                }
                // cts.Token만 넘기므로 OCE=시한 만료 → SocketException(TimedOut)으로 변환. BroadcastAsync 등 호출부의
                // SocketException 처리와 일관되게 하여 죽은 피어만 끊고 브로드캐스트 전체는 계속 진행되도록 한다.
                catch (OperationCanceledException)
                { throw new SocketException((int)SocketError.TimedOut); }
                // 타이머 무장 해제: 송신 간 유휴 구간에 타이머가 발화해 다음 TryReset을 실패(→재할당)시키는 것을 막는다.
                finally { cts.CancelAfter(Timeout.InfiniteTimeSpan); }
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
        _sendTimeoutCts?.Dispose(); // 재사용 송신 시한 CTS 해제(진행 중 송신과의 경합은 _sendGate.Dispose와 동일 저위험 race)
    }
}
