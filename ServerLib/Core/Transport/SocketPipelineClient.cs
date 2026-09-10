using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Memory;
using ServerLib.Interface;

namespace ServerLib.Core.Transport;

// 구현 은닉(internal): 외부 소비자는 ServerNet.CreateClient()가 반환하는 IClientConnection으로만 사용한다.
internal sealed class SocketPipelineClient : IClientConnection
{
    private static readonly int MinBufferSize = 4096;

    // PipeOptions(useSynchronizationContext:false): 클라이언트는 특히 위험하다 — ConnectAsync를 UI 스레드에서 await하면
    // 기본값(true)에서 FillPipe/ReadPipe의 모든 continuation이 그 UI 스레드에 고정되어 데드락한다(ConfigureAwait로는 못 막음).
    // continuation을 ThreadPool에서 실행하도록 강제. 전 연결 공유라 static readonly 1회 생성.
    //
    // pauseWriterThreshold 기본값(64KB)이 최대 프레임(65,539B)보다 작지만 데드락은 없다: .NET Core 3.0+의 Pipe는
    // reader가 AdvanceTo(examined=End)로 "버퍼 전부를 검토했음"을 알리면 임계값과 무관하게 writer를 재개한다
    // (backpressure는 reader가 실제로 뒤처진 경우에만 적용) — 서버 세션과 동일 근거, 테스트로 실증됨.
    private static readonly PipeOptions s_pipeOptions = new(useSynchronizationContext: false);

    private Socket? _socket;
    private Pipe? _pipe;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private bool _started; // ConnectAsync 진입 후 true — 콜백 재설정 차단(단일 셋업 스레드에서만 접근)
    private long _rttTicks; // 마지막 RTT(ticks) — Volatile로 갱신/읽기
    // SemaphoreSlim: 송신 경로 직렬화 — 커널 전환 없이 스핀 후 관리형 대기로 전환하는 경량 게이트.
    // PING 루프와 앱 SendAsync가 동일 소켓에 동시 기록하는 것을 막아 Thread-safe 계약을 보장한다.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    // CancellationTokenSource(재사용): 송신 시한용. _sendGate가 동시 송신 1건을 보장하므로 단일 인스턴스를 TryReset()으로 재사용해
    // 송신마다 CreateLinkedTokenSource + 내부 Timer를 할당하던 정상부하 GC(실측 패킷당 160B)를 제거. caller 토큰과 링크 안 함(A-max).
    private CancellationTokenSource? _sendTimeoutCts;

    public bool IsConnected => _socket?.Connected ?? false;
    public TimeSpan? PingInterval { get; set; }

    /// <summary>송신 1건의 최대 허용 시간입니다. <see langword="null"/>(기본값)이면 비활성화됩니다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>목적:</b> 수신 버퍼를 비우지 않는(응답불능) 서버로 인해 <see cref="SendAsync"/>가 무한 블록되어
    /// 송신 게이트를 영구 점유하고 PING 루프·앱 송신이 모두 정지하는 것을 방지합니다.</description></item>
    /// <item><description><b>동작:</b> 시한 초과 시 <see cref="System.Net.Sockets.SocketException"/>(<see cref="System.Net.Sockets.SocketError.TimedOut"/>)을 throw합니다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 설정 여부와 무관하게 송신당 Zero-allocation입니다. 설정 시 시한용 <see cref="CancellationTokenSource"/>를
    /// 연결 수명 동안 1개만 두고 <see cref="CancellationTokenSource.TryReset"/>로 매 송신 재사용합니다(송신 게이트가 동시 송신 1건을 보장).</description></item>
    /// <item><description><b>Cancellation 계약:</b> 설정 시 <see cref="SendAsync"/>의 <c>cancellationToken</c>은 송신 게이트 대기를 취소합니다.
    /// 이미 시작된(in-flight) 소켓 쓰기는 caller 토큰이 아니라 이 시한으로 bound됩니다(즉시 끊기지 않고 시한 내로 종료).</description></item>
    /// <item><description><b>Thread Safety:</b> Thread-safe(단순 참조 읽기/쓰기).</description></item>
    /// </list>
    /// </remarks>
    public TimeSpan? SendTimeout { get; set; }
    // Volatile.Read: 수신 루프(writer)와 앱(reader) 간 최신 RTT 가시성 보장
    public TimeSpan Rtt => new TimeSpan(Volatile.Read(ref _rttTicks));
    // E3: 콜백은 ConnectAsync() 전에만 설정 — 연결 후 재할당을 막아 수신 루프 가시성/일관성 보장.
    private Func<ValueTask>? _onConnected;
    public Func<ValueTask>? OnConnected
    {
        get => _onConnected;
        set
        {
            if (_started) throw new InvalidOperationException("OnConnected는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
            _onConnected = value;
        }
    }

    private Func<ValueTask>? _onDisconnected;
    public Func<ValueTask>? OnDisconnected
    {
        get => _onDisconnected;
        set
        {
            if (_started) throw new InvalidOperationException("OnDisconnected는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
            _onDisconnected = value;
        }
    }

    private Func<ReadOnlyMemory<byte>, ValueTask>? _onReceived;
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived
    {
        get => _onReceived;
        set
        {
            if (_started) throw new InvalidOperationException("OnReceived는 ConnectAsync() 호출 전에만 설정할 수 있습니다.");
            _onReceived = value;
        }
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        _started = true; // 이후 콜백 재설정 차단

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.NoDelay = true;  // Nagle 알고리즘 비활성화 — 소량 실시간 패킷의 ~200ms 지연 방지 (클라이언트도 저지연 필수)

        // ConnectAsync: DNS 해석(호스트명일 때)+TCP 3-way 핸드셰이크를 비동기로 — 동기 Connect()의 스레드 블로킹 회피
        await _socket.ConnectAsync(host, port, cancellationToken);

        _pipe = new Pipe(s_pipeOptions);
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

                while (TryReadPacket(ref buffer, out var packet, out var packetId))
                {
                    // 예약 ID 가로채기: PONG이면 RTT만 갱신하고 앱 OnReceived는 호출하지 않는다.
                    // P8(대칭): 이미 파싱한 packetId로 분기 → PONG이 아닌 패킷은 RTT 프로브(stackalloc·CopyTo·재파싱)를 건너뛴다.
                    if (HeartbeatProtocol.IsPong(packetId) && TryHandlePong(packet))
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

    private static bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet, out ushort packetId)
    {
        packetId = 0;
        if (buffer.Length < PacketPool.HeaderSize) { packet = default; return false; }

        // stackalloc: 헤더(최대 4바이트)를 스택 버퍼로 복사 — 패킷마다 힙 할당 없이 처리
        Span<byte> headerBuf = stackalloc byte[PacketPool.HeaderSize];
        buffer.Slice(0, PacketPool.HeaderSize).CopyTo(headerBuf); // 멀티세그먼트여도 중간 버퍼 없이 세그먼트별 직접 복사

        if (!PacketPool.TryParseHeader(headerBuf, out packetId, out int bodyLength)) { packet = default; return false; }

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
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 게이트 대기 중 DisposeAsync가 완료되었을 수 있다 — 해제된 소켓 접근 전에 재확인해 즉시 탈출.
            // (_sendGate는 Dispose하지 않으므로 대기자는 반드시 여기까지 깨어난다 — DisposeAsync 주석 참조)
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            var timeout = SendTimeout;
            if (timeout is null)
            {
                // 시한 미설정: CTS 없이 caller 토큰 직접 사용(무할당).
                await SendAllAsync(_socket, data, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 재사용 CTS 확보: TryReset() 성공 시 기존 CTS+내부 Timer 재사용 → 송신당 무할당.
                // 실패(직전 시한 발화로 이미 취소됨)는 응답불능 서버 teardown 경로라 드묾 → 그때만 새로 생성.
                var cts = _sendTimeoutCts;
                if (cts is null || !cts.TryReset())
                {
                    cts?.Dispose();
                    cts = _sendTimeoutCts = new CancellationTokenSource();
                }
                cts.CancelAfter(timeout.Value); // 내부 Timer를 Change로 재무장 — 새 Timer 할당 없음
                try
                {
                    // A-max: in-flight 소켓 쓰기는 cts(시한)만 관찰. caller 취소는 위 _sendGate.WaitAsync에서 존중.
                    // 시한은 위 CancelAfter 1회로 무장되어 부분 송신 루프 전체(전량 송신 완료까지)에 적용된다.
                    await SendAllAsync(_socket, data, cts.Token).ConfigureAwait(false);
                }
                // cts.Token만 넘기므로 OCE=시한 만료 → SocketException(TimedOut)으로 변환 → PING 루프 등 호출부와 일관.
                catch (OperationCanceledException)
                { throw new SocketException((int)SocketError.TimedOut); }
                // 타이머 무장 해제: 유휴 구간 발화로 다음 TryReset이 실패(→재할당)하는 것을 막는다.
                finally { cts.CancelAfter(Timeout.InfiniteTimeSpan); }
            }
        }
        finally { _sendGate.Release(); }
    }

    // Socket.SendAsync(스트림 소켓)는 send(2) 시맨틱을 따른다: 커널 송신 버퍼 여유만큼만 큐잉하고 "실제 수용한
    // 바이트 수"를 반환한 뒤 정상 완료할 수 있다. 반환값을 무시하면 버퍼 포화 시 패킷 꼬리가 유실되어 이후 스트림의
    // 프레이밍 전체가 오염되므로, 반환 길이만큼 슬라이스를 전진시키며 전량 송신될 때까지 반복한다(추가 할당 없음).
    private static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        for (int sent = 0; sent < data.Length;)
            sent += await socket.SendAsync(data.Slice(sent), SocketFlags.None, token).ConfigureAwait(false);
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

        // ConfigureAwait(false): 호출자가 SyncContext 있는 스레드에서 DisposeAsync를 동기 대기할 경우의 데드락 회피.
        if (_cts != null) await _cts.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose(); // in-flight 송수신을 SocketException으로 즉시 중단 → 게이트 보유자가 finally(Release)로 빠져나온다
        _cts?.Dispose();

        // _sendGate는 의도적으로 Dispose하지 않는다: SemaphoreSlim.Dispose는 대기 중인 WaitAsync Task를 깨우지 않아
        // 대기자가 영구 미완료(hang)로 남는다. AvailableWaitHandle을 만들지 않는 SemaphoreSlim은 커널 핸들 등
        // unmanaged 리소스를 보유하지 않으므로 Dispose 생략이 안전하며(파이널라이저 없음, GC로 회수),
        // 남은 대기자는 게이트 통과 후 SendAsync의 disposed 재확인에서 ObjectDisposedException으로 탈출한다.
        //
        // _sendTimeoutCts는 게이트를 확보해 in-flight 송신의 finally(CancelAfter 해제)까지 끝났음을 관찰한 뒤에만
        // Dispose한다 — 실행 중 송신과 경합하며 Dispose하면 송신 스레드의 CancelAfter가 ObjectDisposedException을 던진다.
        // 확보 실패(비정상 장기 송신)는 CTS 해제를 GC에 위임한다: hang보다 지연 회수가 낫다.
        if (await _sendGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            try { _sendTimeoutCts?.Dispose(); }
            finally { _sendGate.Release(); } // 통행 재개 — 남은 대기자들이 순서대로 깨어나 disposed 확인 후 탈출
        }
    }
}
