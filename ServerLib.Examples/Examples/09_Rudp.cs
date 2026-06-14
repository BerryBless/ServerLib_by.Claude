using System.Net;
using System.Net.Sockets;
using ServerLib.Core.Rudp;

namespace ServerLib.Examples.Examples;

/// <summary>
/// RUDP(신뢰 UDP) 스택을 루프백으로 시연합니다.
/// <see cref="RudpChannel"/>을 통한 종단간 신뢰 전송과,
/// <see cref="RudpSendQueue"/> / <see cref="RudpSegment"/> / <see cref="RudpRecvWindow"/>
/// 빌딩블록을 직접 사용하는 패턴을 모두 다룹니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="RudpChannel"/>: ctor(UdpClient, RemoteEndPoint) / Start / SendReliableAsync / OnReceived / RemoteEndPoint / DisposeAsync</description></item>
/// <item><description><see cref="RudpSendQueue"/>: EnqueueAsync / TryDequeue / Count / Dispose + DefaultCapacity / MaxRetries</description></item>
/// <item><description><see cref="RudpSegment"/>: ctor / WithRetry / SequenceNumber / Buffer / Length / SentAt / RetryCount</description></item>
/// <item><description><see cref="RudpRecvWindow"/>: OnReceive / ExpectedSeq / BuildAckBitmap</description></item>
/// </list>
/// <br/><br/>
/// <b>[⚠️ UdpClient 소유권]</b><br/>
/// <see cref="RudpChannel.DisposeAsync"/>는 내부 <see cref="UdpClient"/>를 dispose하지 않습니다.
/// 예제(소비자)가 직접 Dispose해야 합니다.
/// </remarks>
internal static class Rudp
{
    /// <summary>
    /// 두 RudpChannel을 루프백 UdpClient로 연결하고 신뢰 전송 왕복을 검증한 뒤
    /// 빌딩블록(RudpSendQueue/Segment/RecvWindow)을 직접 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> RudpChannel 내부는 Thread-safe(CTS·Channel<T>·Interlocked).
    /// RudpRecvWindow.OnReceive는 Not Thread-safe — 단일 수신 스레드 전용.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> RudpChannel 내부에서 ArrayPool<byte>.Shared로 버퍼 관리.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. TCS로 수신 완료를 결정적으로 대기합니다.
    /// </remarks>
    public static async Task RunAsync()
    {
        // ── RudpChannel 루프백 종단간 신뢰 전송 ──
        Console.WriteLine("  [RUDP] RudpChannel 루프백 시연:");

        // UdpClient(0): OS가 임시 포트를 원자적으로 할당. localPort=0 → IPEndPoint(0.0.0.0, <랜덤>).
        // ⚠️ RudpChannel.DisposeAsync는 이 UdpClient를 dispose하지 않습니다 — 아래 using으로 직접 관리.
        using var udpA = new UdpClient(0);
        using var udpB = new UdpClient(0);

        int portA = ExampleHarness.GetUdpPort(udpA);
        int portB = ExampleHarness.GetUdpPort(udpB);

        Console.WriteLine($"    A 포트={portA}, B 포트={portB}");

        // RudpChannel: UdpClient 위에 시퀀스 번호 + ACK + 재전송 타이머를 구현한 신뢰 채널.
        // ctor에 UdpClient(소유권 없음)와 원격 엔드포인트를 전달합니다.
        // RemoteEndPoint 프로퍼티로 설정된 원격 주소를 확인할 수 있습니다.
        var remoteA = new IPEndPoint(IPAddress.Loopback, portA);
        var remoteB = new IPEndPoint(IPAddress.Loopback, portB);

        await using var channelA = new RudpChannel(udpA, remoteB); // A→B 방향
        await using var channelB = new RudpChannel(udpB, remoteA); // B→A 방향

        Console.WriteLine($"    channelA.RemoteEndPoint={channelA.RemoteEndPoint}");

        // TaskCompletionSource: B가 A의 메시지를 수신할 때 신호합니다.
        var bReceived = new TaskCompletionSource();
        byte[]? receivedPayload = null;

        // OnReceived: B가 데이터를 수신하면 I/O 스레드에서 호출됩니다.
        // 전달된 ReadOnlyMemory<byte>는 수신 버퍼의 깊은복사본입니다(RudpChannel 내부에서 new byte[] 보장).
        channelB.OnReceived = data =>
        {
            receivedPayload = data.ToArray();
            Console.WriteLine($"    [B] 수신: {System.Text.Encoding.UTF8.GetString(receivedPayload)}");
            bReceived.TrySetResult();
            return ValueTask.CompletedTask;
        };

        // Start(): 내부 SendLoopAsync·ReceiveLoopAsync를 시작합니다. ctor 후 반드시 호출해야 합니다.
        channelA.Start();
        channelB.Start();

        // SendReliableAsync: 시퀀스 번호를 부여하고 재전송 큐에 등록한 뒤 송신합니다.
        // 내부에서 ArrayPool.Rent → 헤더+페이로드 복사 → EnqueueAsync를 수행합니다.
        byte[] msg = System.Text.Encoding.UTF8.GetBytes("RUDP 신뢰 전송 테스트");
        await channelA.SendReliableAsync(msg.AsMemory());
        Console.WriteLine($"    [A] SendReliableAsync: {msg.Length}바이트 전송");

        // 수신 대기
        await ExampleHarness.WaitSignaledAsync(bReceived, TimeSpan.FromSeconds(5));

        if (receivedPayload is null || !receivedPayload.SequenceEqual(msg))
            throw new InvalidOperationException("RUDP 페이로드 불일치");

        // ── 빌딩블록 직접 시연 ──
        Console.WriteLine("\n  [RUDP] 빌딩블록 직접 시연:");
        DemoRudpSendQueue();
        DemoRudpRecvWindow();

        Console.WriteLine("[OK] 09_Rudp");
    }

    /// <summary>
    /// <see cref="RudpSendQueue"/>와 <see cref="RudpSegment"/>를 직접 사용하는 패턴을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> RudpSendQueue는 Channel&lt;T&gt; 기반으로 Thread-safe.
    /// <b>[Memory Allocation:]</b> RudpSegment는 readonly struct — 큐 내부 인라인 저장(박싱 없음).
    /// <b>[Blocking:]</b> TryDequeue는 Non-blocking. EnqueueAsync/DequeueAsync는 비동기 대기 가능.
    /// </remarks>
    private static void DemoRudpSendQueue()
    {
        Console.WriteLine("    [RudpSendQueue] DefaultCapacity, MaxRetries, Enqueue/TryDequeue:");
        Console.WriteLine($"      DefaultCapacity={RudpSendQueue.DefaultCapacity}, MaxRetries={RudpSendQueue.MaxRetries}");

        // RudpSendQueue: Channel<T> 기반 락-프리 RUDP 송신 큐.
        // Channel<RudpSegment>: RudpSegment가 struct이므로 큐 내부 저장 시 박싱 없이 인라인 복사.
        using var queue = new RudpSendQueue(capacity: 8);

        // RudpSegment: readonly struct — 시퀀스 번호 + 버퍼 참조 + 길이 + 전송 시각 + 재시도 횟수.
        // Buffer는 ArrayPool 대여 버퍼의 참조(얕은복사) — 소유권은 큐 소비자에게 있습니다.
        byte[] fakeBuf = new byte[16];
        var seg = new RudpSegment(seq: 0, buffer: fakeBuf, length: 16);
        Console.WriteLine($"      RudpSegment: seq={seg.SequenceNumber}, len={seg.Length}, retries={seg.RetryCount}, sentAt={seg.SentAt:HH:mm:ss.fff}");

        // WithRetry(): RetryCount+1인 새 struct 반환(불변값 타입).
        var retried = seg.WithRetry();
        Console.WriteLine($"      WithRetry(): seq={retried.SequenceNumber}, retries={retried.RetryCount}");

        // EnqueueAsync(ValueTask): 큐가 가득 차지 않으면 즉시 완료되는 ValueTask 반환.
        // 동기 완료 시 힙 할당 없음(ValueTask 특성).
        queue.EnqueueAsync(seg).GetAwaiter().GetResult();
        queue.EnqueueAsync(retried).GetAwaiter().GetResult();
        Console.WriteLine($"      EnqueueAsync ×2 → Count={queue.Count}");

        // TryDequeue: Non-blocking 시도. 항목이 있으면 true + segment, 없으면 false.
        if (queue.TryDequeue(out var dequeued))
            Console.WriteLine($"      TryDequeue(): seq={dequeued.SequenceNumber}, retries={dequeued.RetryCount}");

        Console.WriteLine($"      TryDequeue 후 Count={queue.Count}");

        // Dispose: TryComplete 후 남은 세그먼트 버퍼를 모두 ArrayPool에 반납.
        // 미반납 시 ArrayPool 고갈로 이후 Rent가 new byte[]로 폴백(GC 압력).
    }

    /// <summary>
    /// <see cref="RudpRecvWindow"/>의 시퀀스 번호 추적·중복 제거·ACK 비트맵 생성을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> RudpRecvWindow.OnReceive는 Not thread-safe — 단일 수신 스레드 전용.
    /// ExpectedSeq 읽기는 Volatile.Read로 보호되어 다른 스레드에서 안전합니다.
    /// <b>[Memory Allocation:]</b> Zero-allocation. 내부 bool[64] 링버퍼는 생성 시 1회 할당 후 재사용.
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    private static void DemoRudpRecvWindow()
    {
        Console.WriteLine("    [RudpRecvWindow] OnReceive/ExpectedSeq/BuildAckBitmap:");

        // RudpRecvWindow: 슬라이딩 윈도우로 순서 재조립 + 중복 제거를 수행합니다.
        // 내부 bool[64] 링버퍼를 수신 시 모듈로 인덱싱해 재사용 → 패킷마다 할당 없음(GC 무압력).
        var window = new RudpRecvWindow();
        Console.WriteLine($"      초기 ExpectedSeq={window.ExpectedSeq}");

        // OnReceive(seq, out advancedTo): 수신된 시퀀스 번호를 처리합니다.
        // 반환값: 순서에 맞으면 true(콜백 호출), 중복/윈도우 초과면 false.
        bool r0 = window.OnReceive(0, out uint adv0);
        Console.WriteLine($"      OnReceive(0): 수락={r0}, ExpectedSeq 진행={adv0} (현재={window.ExpectedSeq})");

        bool r1 = window.OnReceive(1, out uint adv1);
        Console.WriteLine($"      OnReceive(1): 수락={r1}, ExpectedSeq 진행={adv1} (현재={window.ExpectedSeq})");

        // 중복 수신: seq=0은 이미 처리됐으므로 false 반환
        bool rDup = window.OnReceive(0, out uint advDup);
        Console.WriteLine($"      OnReceive(0) 중복: 수락={rDup} (false 예상)");

        // 윈도우 초과: ExpectedSeq=2에서 seq=100은 WindowSize(64)를 초과하므로 false
        bool rOob = window.OnReceive(100, out _);
        Console.WriteLine($"      OnReceive(100) 윈도우초과: 수락={rOob} (false 예상)");

        // BuildAckBitmap: 최근 32개 수신 여부를 비트맵(uint)으로 반환합니다.
        uint bitmap = window.BuildAckBitmap();
        Console.WriteLine($"      BuildAckBitmap(): 0x{bitmap:X8} (ExpectedSeq={window.ExpectedSeq}로부터 32개 비트)");

        if (r0 != true || r1 != true || rDup != false || rOob != false)
            throw new InvalidOperationException("RudpRecvWindow 검증 실패");
    }
}
