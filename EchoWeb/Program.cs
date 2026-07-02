// =============================================================================
// EchoWeb — 브라우저 ↔ WebSocket ↔ ServerLib EchoClient(TCP) 브리지 예제
// =============================================================================
// 동작: 브라우저가 WebSocket("/ws")으로 보낸 텍스트를 ServerLib IClientConnection으로
//       기존 EchoServer(127.0.0.1:9000)에 EchoPacket(Id=1)으로 중계하고,
//       에코 응답을 다시 WebSocket 텍스트 프레임으로 돌려줍니다.
//
//       라이브러리 자체는 raw TCP(길이 프리픽스 바이너리 프레이밍)만 제공하므로
//       브라우저가 직접 말할 수 없습니다. 이 프로젝트는 "웹 소비자"를 위한
//       프로토콜 변환 계층(WebSocket ↔ TCP)만 담당하며, 에코 로직 자체는
//       기존 EchoServer.exe를 그대로 재사용합니다(별도 프로세스 브리지).
//
// 실행법:
//   1) dotnet run --project EchoServer   (9000 포트, 먼저 기동)
//   2) dotnet run --project EchoWeb      (127.0.0.1:8080)
//   3) 브라우저에서 http://127.0.0.1:8080 접속 → 메시지 입력 → 에코 확인
//
// WebSocket 연결 1개 = EchoClient(TCP) 연결 1개 (per-session 격리).
// =============================================================================

using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// BinaryPacketSerializer: 내부 상태가 없는 무상태(stateless) 클래스 → 여러 WebSocket 연결(브리지 인스턴스)이
// 동시에 호출해도 안전(Thread-safe). 연결마다 새로 만들지 않고 앱 전역에서 단일 인스턴스를 공유합니다.
var serializer = new BinaryPacketSerializer();

// UseWebSockets(): HTTP 요청을 WebSocket 프로토콜로 업그레이드(101 Switching Protocols)할 수 있게 하는 미들웨어.
// 이 미들웨어가 없으면 HttpContext.WebSockets.IsWebSocketRequest가 항상 false로 취급됩니다.
app.UseWebSockets();

// UseDefaultFiles + UseStaticFiles: wwwroot/index.html을 "/" 요청에 매핑해 정적 파일로 서빙.
// (커스텀 컨트롤러·razor 없이 순수 정적 HTML+JS 클라이언트만 필요하므로 최소 구성)
app.UseDefaultFiles();
app.UseStaticFiles();

// "/ws" 엔드포인트: 브라우저 WebSocket 연결의 진입점.
app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // AcceptWebSocketAsync(): TCP 연결을 유지한 채 HTTP를 WebSocket 프레임 프로토콜로 전환합니다.
    // using: 브리지 종료 시(정상/예외 불문) WebSocket 자원을 확실히 Dispose.
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

    // context.RequestAborted: 브라우저가 TCP 연결을 끊거나 서버가 셧다운되면 트립되는 토큰.
    // 브리지 루프의 취소 기반(linkCts)으로 그대로 이어받습니다.
    await BridgeAsync(socket, serializer, context.RequestAborted);
});

// 프로젝트의 루프백 전용 보안 관례(Server/Program.cs의 admin 포트와 동일)에 맞춰
// 외부 네트워크에 노출하지 않고 127.0.0.1에만 바인딩합니다.
app.Run("http://127.0.0.1:8080");

/// <summary>WebSocket 연결 1개를 ServerLib TCP EchoClient 연결 1개로 중계하는 브리지 루프입니다.</summary>
/// <param name="socket">브라우저와 이미 핸드셰이크가 완료된 WebSocket</param>
/// <param name="serializer">패킷 직렬화기 (앱 전역 공유 인스턴스, 무상태)</param>
/// <param name="requestAborted">HTTP 요청(=WebSocket 연결) 수명과 연동된 취소 토큰</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> ASP.NET Core Kestrel 요청 스레드에서 시작되어, 연결이 유지되는
/// 동안 이 메서드가 async 상태머신으로 계속 실행됩니다. <c>echo.OnReceived</c>는 ServerLib의 내부
/// IO 스레드에서 호출되므로, 이 메서드의 나머지 흐름과는 별도 스레드 컨텍스트입니다.</description></item>
/// <item><description><b>Memory Policy:</b> WebSocket 수신 버퍼는 연결당 1회 할당해 재사용합니다(연결 수명
/// = 배열 수명이므로 ArrayPool 대여/반납 왕복 이득이 적음). EchoPacket 역직렬화 시 발생하는 string 할당은
/// ServerLib 자체 동작(UTF-8 → string 변환)이며 이 메서드에서 추가로 복사하지 않습니다.</description></item>
/// <item><description><b>Blocking 여부:</b> 전 구간 비동기(Non-blocking). <c>ReceiveAsync</c>/<c>SendAsync</c>는
/// 데이터 도착·전송 가능 시까지 스레드를 점유하지 않고 대기합니다.</description></item>
/// <item><description><b>Teardown 순서(소켓 누수·hang 방지):</b> ① <c>linkCts.Cancel()</c> ②
/// <c>outbound</c> 채널 <c>Complete()</c> ③ 송신 펌프 Task 대기(잔여 메시지 배출) ④ WebSocket close
/// ⑤ <c>echo</c> DisposeAsync(호출자의 <c>await using</c> 없이, 이 메서드 내부 <c>await using</c>으로 보장).
/// 브라우저 종료와 9000 서버 드롭이라는 두 실패원을 <c>linkCts</c> 하나로 수렴시켜 순서를 고정합니다.</description></item>
/// </list>
/// </remarks>
static async Task BridgeAsync(WebSocket socket, BinaryPacketSerializer serializer, CancellationToken requestAborted)
{
    // CancellationTokenSource.CreateLinkedTokenSource: 브라우저 종료(requestAborted)와 9000 서버 드롭
    // (echo.OnDisconnected에서 수동 Cancel) 두 실패원을 단일 토큰으로 합류시켜, 아래에서 정확히
    // 한 곳(finally)에서만 teardown 순서를 실행하도록 만듭니다.
    using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);

    // Channel<string>: 내부적으로 lock-free 큐로 구현되어, 다중 생산자(ServerLib IO 스레드의
    // OnReceived 콜백)가 락 경합 없이 값을 적재할 수 있습니다. WebSocket.SendAsync는 동일 소켓에 대한
    // 동시 호출을 허용하지 않으므로, 반드시 단일 소비자(아래 송신 펌프)로 직렬화해 이 제약을 만족시킵니다.
    Channel<string> outbound = Channel.CreateUnbounded<string>();

    // ServerNet.CreateClient(): 내부 구현체(SocketPipelineClient, internal)를 IClientConnection
    // 인터페이스로 반환 — 소비자는 구체 타입 없이 인터페이스만으로 제어(캡슐화).
    // await using: 이 메서드를 벗어나는 모든 경로(return/예외)에서 DisposeAsync()가 호출되어
    // TCP 소켓과 수신 파이프라인 자원이 정리됩니다.
    await using IClientConnection echo = ServerNet.CreateClient();

    echo.OnReceived = data =>
    {
        // ReadOnlyMemory<byte>는 콜백 반환 후 무효화될 수 있는 내부 버퍼 뷰입니다(재사용 예정).
        // 콜백 안에서 즉시 EchoPacket으로 역직렬화하고, 그 결과(string)만 채널에 적재합니다.
        // 메모리 뷰 자체를 큐에 넣으면 나중에 다른 스레드가 이미 재사용된 버퍼를 읽는 문제가 생깁니다.
        EchoPacket pkt = serializer.Deserialize<EchoPacket>(data.Span);
        outbound.Writer.TryWrite(pkt.Message);
        return ValueTask.CompletedTask;
    };
    echo.OnDisconnected = () =>
    {
        // 9000 에코 서버가 먼저 끊긴 경우: 브라우저 쪽 루프도 동일 절차로 정리되도록 같은 토큰을 트립.
        linkCts.Cancel();
        return ValueTask.CompletedTask;
    };

    try
    {
        await echo.ConnectAsync("127.0.0.1", 9000, linkCts.Token);
    }
    catch (Exception ex)
    {
        // 9000 서버 미기동 등 연결 실패: hang 없이 즉시 브라우저에 에러 텍스트 1건을 보내고 종료합니다.
        await TrySendTextAsync(socket, $"[에러] 에코 서버(127.0.0.1:9000)에 연결할 수 없습니다: {ex.Message}");
        await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "connect-failed");
        return;
    }

    // 송신 펌프: outbound 채널 → WebSocket.SendAsync 순차 실행. reader.ReadAllAsync()는 Writer.Complete()가
    // 호출될 때까지 대기하며 도착한 항목을 모두 소비하므로, 별도 취소 토큰 없이도 "남은 메시지를 다 보낸 뒤
    // 자연 종료"를 보장합니다(취소 즉시 중단이 아님 — teardown 순서 ③과 일치).
    Task pumpTask = PumpOutboundAsync(socket, outbound.Reader);

    // WebSocket 텍스트 프레임 수신용 버퍼. 연결 수명 동안 1회만 할당해 재사용(연결마다 ArrayPool
    // Rent/Return을 오가는 대신, 연결 자체가 짧고 버퍼 수명이 연결 수명과 동일하므로 고정 배열이 더 간단).
    var recvBuffer = new byte[4096];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(recvBuffer, linkCts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.Count == 0)
                continue;

            string text = Encoding.UTF8.GetString(recvBuffer, 0, result.Count);

            // IClientConnection.SendAsync<EchoPacket>: ArrayPool 대여 → 직렬화(Zero-allocation) →
            // 소켓 직접 송신(_socket.SendAsync, Non-blocking) → 버퍼 반납까지 한 번에 처리하는 확장 메서드.
            await echo.SendAsync(new EchoPacket { Message = text }, linkCts.Token);
        }
    }
    catch (OperationCanceledException)
    {
        // teardown 정상 경로: linkCts가 트립되어 ReceiveAsync가 취소됨(브라우저 종료 또는 9000 드롭).
    }
    catch (WebSocketException)
    {
        // 브라우저 측 비정상 종료(네트워크 끊김 등).
    }
    finally
    {
        // ── Teardown 순서 고정 ──────────────────────────────────────────────
        // ① Cancel: 아직 진행 중인 echo.SendAsync/ConnectAsync 대기를 정리
        linkCts.Cancel();
        // ② 채널 종료: 더 이상 새 메시지가 적재되지 않음을 펌프에 알림
        outbound.Writer.TryComplete();
        // ③ 펌프가 잔여 메시지를 모두 배출한 뒤 자연 종료할 때까지 대기
        await pumpTask;
        // ④ WebSocket close (echo의 DisposeAsync는 메서드를 벗어날 때 await using이 처리)
        await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bridge-closed");
    }
}

/// <summary>outbound 채널의 메시지를 순차적으로 WebSocket 텍스트 프레임으로 전송하는 단일 소비자 펌프입니다.</summary>
/// <remarks>
/// WebSocket.SendAsync는 동일 소켓에 대해 동시 호출을 허용하지 않으므로(내부적으로 한 번에 하나의
/// 송신 작업만 진행 가능), 여러 생산자가 적재하는 <paramref name="reader"/>를 이 메서드 하나만 소비하도록
/// 강제해 직렬화합니다. Thread-safe 여부는 무관 — 애초에 동시 호출이 발생하지 않도록 설계되었습니다.
/// </remarks>
static async Task PumpOutboundAsync(WebSocket socket, ChannelReader<string> reader)
{
    try
    {
        // ReadAllAsync(): Writer.Complete() 호출 전까지 대기하며 도착한 항목을 모두 순회.
        // Complete() 이후 남은 항목까지 다 읽고 나서야 열거가 끝나므로 "잔여 배출 후 종료"가 자연히 보장됩니다.
        await foreach (string message in reader.ReadAllAsync())
        {
            if (socket.State != WebSocketState.Open)
                break;

            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
    }
    catch (WebSocketException)
    {
        // 브라우저 쪽이 이미 닫힌 상태에서 송신 시도 — teardown이 뒤이어 정리하므로 조용히 종료.
    }
}

static async Task TrySendTextAsync(WebSocket socket, string message)
{
    if (socket.State != WebSocketState.Open)
        return;

    try
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }
    catch (WebSocketException)
    {
        // 전송 시점에 이미 끊긴 경우 — 호출자가 뒤이어 종료 처리.
    }
}

static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
{
    if (socket.State != WebSocketState.Open)
        return;

    try
    {
        await socket.CloseAsync(status, description, CancellationToken.None);
    }
    catch (WebSocketException)
    {
        // 이미 상대측이 닫은 경우 등 — 무시하고 종료.
    }
}
