using System.Net;
using System.Net.Sockets;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;
using Xunit;

namespace EchoExample.Tests;

/// <summary>
/// <c>EchoServer/Program.cs</c>의 핵심 동작(받은 메시지를 동일하게 에코)을
/// 루프백 실소켓으로 End-to-End 검증합니다.
/// </summary>
/// <remarks>
/// <b>[재현 대상 — EchoServer/Program.cs의 에코 핸들러]</b>
/// <code>
/// listener.OnReceived = (session, data) =>
///     session.SendAsync(new EchoPacket { Message = serializer.Deserialize&lt;EchoPacket&gt;(data.Span).Message });
/// </code>
/// <para>
/// <c>Program.cs</c>는 top-level 문이라 테스트에서 직접 호출할 수 없습니다.
/// 에코 핸들러가 1줄이므로 각 테스트가 동일 와이어링을 재현합니다.
/// 재현 코드와 <c>Program.cs</c> 코드를 비교하면 예제 동작이 실증됩니다.
/// </para>
/// <para>
/// <b>[테스트 격리 전략]</b><br/>
/// 각 테스트는 <see cref="GetFreePort"/>로 독립 포트를 확보합니다.
/// 서버는 <see cref="IPAddress.Loopback"/> 전용으로 바인딩(외부 노출 없음).
/// </para>
/// </remarks>
public class EchoEndToEndTests
{
    // BinaryPacketSerializer: 무상태(stateless), Thread-safe. 여러 테스트가 동시 실행돼도 공유 안전.
    private static readonly BinaryPacketSerializer Serializer = new();

    // ── 테스트 타임아웃 상수 ─────────────────────────────────────────────────
    // 루프백 에코 왕복은 <1ms가 일반적이나, CI 환경의 스케줄링 지연을 고려해 5초로 설정.
    private const int EchoTimeoutMs = 5_000;

    /// <summary>
    /// OS에서 임시 포트를 확보해 반환합니다. 각 테스트 메서드가 독립 포트를 사용하도록 합니다.
    /// </summary>
    /// <remarks>
    /// <see cref="TcpListener"/>(Loopback, 0):
    ///   포트 0으로 바인딩하면 OS 커널(TCP/IP 스택)이 현재 미사용인 임시 포트(Ephemeral Port)를 자동 배정합니다.
    ///   Start() → LocalEndpoint.Port 읽기 → Stop() 순서로 배정된 포트 번호를 확보합니다.
    ///   Stop() 이후 실제 IServerListener 바인딩까지 짧은 TOCTOU 창이 존재하나,
    ///   루프백 전용 테스트에서는 다른 프로세스가 같은 포트를 탈취할 가능성이 극히 낮아 충분히 안전합니다.
    /// </remarks>
    private static int GetFreePort()
    {
        // TcpListener(Loopback, 0): System.Net.Sockets 기반 임시 포트 확보.
        // Socket.Bind(port=0) → 커널이 미사용 포트 선택 → LocalEndpoint로 실제 포트 조회.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// 에코 서버를 생성하고 지정 포트에 시작하는 헬퍼입니다.
    /// EchoServer/Program.cs의 에코 와이어링을 재현합니다.
    /// </summary>
    /// <remarks>
    /// 호출자는 테스트 완료 후 반드시 <see cref="IServerListener.Stop"/>을 호출해야 합니다(finally 블록 권장).
    /// </remarks>
    private static IServerListener StartEchoListener(int port)
    {
        // ServerNet.CreateListener(): SocketPipelineListener(internal)를 IServerListener로 반환.
        // 콜백 등록은 Start() 호출 전에 완료해야 합니다(이후 설정 시 InvalidOperationException).
        IServerListener listener = ServerNet.CreateListener();

        // OnReceived: EchoServer/Program.cs 에코 핸들러와 동일한 로직.
        //   data(ReadOnlyMemory<byte>): 헤더(4B) + 본문 = 완전한 패킷 1개. 콜백 동안만 유효.
        //   data.Span: zero-copy 뷰 → 동기 Deserialize 후 즉시 반환하므로 복사 불필요.
        //   session.SendAsync<EchoPacket>: ArrayPool 대여 → 직렬화 → 송신 → 반납 (extension 메서드).
        listener.OnReceived = (ISession session, ReadOnlyMemory<byte> data) =>
            session.SendAsync(new EchoPacket { Message = Serializer.Deserialize<EchoPacket>(data.Span).Message });

        // Start(port, IPAddress.Loopback): 루프백 전용 바인딩. accept 루프를 백그라운드 Task에서 시작.
        // Non-blocking: 이 줄 이후 즉시 반환. 연결 수락은 백그라운드에서 비동기로 처리됨.
        listener.Start(port, IPAddress.Loopback);
        return listener;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 1 — 단일 메시지 에코
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 클라이언트가 보낸 단일 메시지가 서버로부터 동일하게 에코되어 수신되는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// 이 테스트가 실증하는 ServerLib 동작:
    /// <list type="bullet">
    /// <item><description>ConnectAsync 완료 후 즉시 SendAsync 가능.</description></item>
    /// <item><description>OnReceived(서버측)가 완전한 패킷 1개를 수신하고, session.SendAsync로 에코를 되돌림.</description></item>
    /// <item><description>OnReceived(클라측)가 에코 패킷 1개를 정확히 수신함.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Server_EchoesSingleMessageBack()
    {
        const string TestMessage = "hello echo";
        int port = GetFreePort();
        IServerListener listener = StartEchoListener(port);

        try
        {
            // TaskCompletionSource<string>: IO 스레드(OnReceived 콜백)에서 에코 결과를 테스트 스레드로 전달하는 신호기.
            // RunContinuationsAsynchronously: SetResult 호출 시 await 대기자가 IO 스레드에서 인라인 실행되지 않도록 방지.
            //   → IO 스레드 점유 시간 최소화, 데드락 위험 제거.
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // await using: IClientConnection은 IAsyncDisposable. 블록 종료 시 DisposeAsync() 자동 호출
            //   → CTS 취소 + 소켓 폐기로 수신 루프 종료 신호.
            //   FillPipeAsync/ReadPipeAsync Task를 명시적으로 await하지 않으므로 반환 시점에 완료 보장은 없습니다.
            await using IClientConnection client = ServerNet.CreateClient();

            // OnReceived: IO 스레드에서 호출. data는 콜백 동안만 유효 → 동기 Deserialize 후 즉시 반환.
            client.OnReceived = (ReadOnlyMemory<byte> data) =>
            {
                // TrySetResult: 첫 번째 에코만 캡처(중복 Set 무시).
                tcs.TrySetResult(Serializer.Deserialize<EchoPacket>(data.Span).Message);
                return ValueTask.CompletedTask; // 동기 완료 → 캐시드 ValueTask, 무할당.
            };

            // ConnectAsync: TCP 3-way handshake + 수신 파이프라인 시작. 완료 후 IsConnected == true.
            await client.ConnectAsync("127.0.0.1", port);
            // SendAsync<EchoPacket>: ArrayPool 대여 → 직렬화 → _socket.SendAsync(소켓 직접 기록) → 반납.
            await client.SendAsync(new EchoPacket { Message = TestMessage });

            // WaitAsync: .NET 6+ 내장 타임아웃 — 완료 즉시 내부 타이머 취소(Task.Delay 잔류 없음).
            // 타임아웃 시 TimeoutException throw.
            string echoed = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(EchoTimeoutMs));
            Assert.Equal(TestMessage, echoed);
        }
        finally
        {
            // listener.Stop(): CTS 취소 + 활성 세션 순차 정리(동기). AcceptLoopAsync는 취소 후 스스로 종료.
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 2 — 유니코드·빈 문자열 에코
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 유니코드(한글), 빈 문자열, 이모지가 에코 후 원본 그대로 보존되는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// BinaryPacketSerializer의 WriteString/ReadString이 UTF-8 가변 길이 인코딩을
    /// 경계 케이스에서도 올바르게 처리함을 실소켓으로 실증합니다.
    /// </remarks>
    [Theory]
    [InlineData("한글 에코 테스트")]  // UTF-8 3바이트/문자 — BodySize가 char 수보다 훨씬 큼
    [InlineData("")]                  // 빈 문자열: BodySize = 2(프리픽스 only) → 빈 Message 에코
    [InlineData("🎯")]                // 4바이트 서로게이트 쌍 — .NET string 2 char, UTF-8 4 bytes
    public async Task Server_EchoesUnicodeAndEmpty(string message)
    {
        int port = GetFreePort();
        IServerListener listener = StartEchoListener(port);

        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = (ReadOnlyMemory<byte> data) =>
            {
                tcs.TrySetResult(Serializer.Deserialize<EchoPacket>(data.Span).Message);
                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);
            await client.SendAsync(new EchoPacket { Message = message });

            // WaitAsync: 완료 즉시 타이머 취소. 타임아웃 시 TimeoutException.
            string echoed = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(EchoTimeoutMs));
            Assert.Equal(message, echoed);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 3 — 연속 N개 메시지 순서 보존
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 연속으로 전송한 N개 메시지가 전송 순서와 동일하게 에코되는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// 이 테스트가 실증하는 ServerLib 보증:
    /// <list type="bullet">
    /// <item><description>
    ///   <b>프레이밍 보증:</b> SocketPipelineSession이 "완전한 패킷 1개 단위로 OnReceived를 호출"합니다.
    ///   N개 패킷을 빠르게 전송해도 각 콜백이 정확히 패킷 1개씩 수신됨을 검증합니다.
    /// </description></item>
    /// <item><description>
    ///   <b>순서 보존:</b> TCP 스트림은 전송 순서를 보증하고, 서버 OnReceived는 순차 처리합니다.
    ///   에코 응답 순서도 전송 순서와 일치해야 합니다.
    /// </description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Server_EchoesMultipleMessagesInOrder()
    {
        int port = GetFreePort();
        var sentMessages = new[] { "first", "second", "third", "fourth", "fifth" };
        IServerListener listener = StartEchoListener(port);

        try
        {
            // List<string>: IO 스레드(OnReceived)에서 Add, 테스트 스레드에서 읽기.
            // TaskCompletionSource의 TrySetResult → await의 happens-before 관계가 확립되므로
            // await tcs.Task 이후 received 읽기는 Thread-safe(메모리 가시성 보장).
            var received = new List<string>();

            // TaskCompletionSource<bool>: 수집 완료(expectedCount개 에코 도착) 신호.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int expectedCount = sentMessages.Length;

            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = (ReadOnlyMemory<byte> data) =>
            {
                // IO 스레드에서 호출. data.Span: zero-copy 뷰 → 동기 역직렬화 후 즉시 반환.
                received.Add(Serializer.Deserialize<EchoPacket>(data.Span).Message);
                // 예상 수 달성 시 신호 발송. TrySetResult: 중복 호출 무시(안전).
                if (received.Count >= expectedCount)
                    tcs.TrySetResult(true);
                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);

            // 순차 전송: await으로 직렬화하여 전송 순서를 보장합니다.
            // SendAsync는 Non-blocking이나, 이전 패킷의 Pipe 기록 완료 후 다음 패킷을 기록합니다.
            foreach (string msg in sentMessages)
                await client.SendAsync(new EchoPacket { Message = msg });

            // WaitAsync: 완료 즉시 타이머 취소. 타임아웃 시 TimeoutException(잔류 타이머 없음).
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(EchoTimeoutMs));

            // 전송 순서 == 에코 수신 순서
            Assert.Equal(sentMessages, received.ToArray());
        }
        finally
        {
            listener.Stop();
        }
    }
}
