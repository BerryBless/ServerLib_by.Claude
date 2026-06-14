using System.Net;
using System.Net.Sockets;

namespace ServerLib.Examples;

/// <summary>
/// 모든 예제에서 공유하는 유틸리티 메서드와 상수를 제공하는 정적 클래스입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> <see cref="GetFreePort"/>와 <see cref="LoopbackHost"/>는 Thread-safe.
/// <see cref="WaitSignaledAsync"/>는 TCS의 Task를 반환하므로 여러 스레드에서 동시 await 가능.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> <see cref="GetFreePort"/>는 <see cref="TcpListener"/> 1회 임시 생성(목적 달성 즉시 Stop). Zero long-lived allocation.
/// </description></item>
/// </list>
/// </remarks>
internal static class ExampleHarness
{
    /// <summary>루프백 주소 상수입니다. 모든 예제 서버와 클라이언트는 이 주소를 사용합니다.</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>
    /// OS에게 임시 포트를 요청해 사용 가능한 TCP 포트 번호를 반환합니다.
    /// </summary>
    /// <returns>OS가 할당한 사용 가능한 포트 번호입니다.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. 여러 예제가 동시에 호출해도 각자 독립된 포트를 얻습니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> <see cref="TcpListener"/> 1회 임시 생성 후 즉시 Stop — 장기 보유 없음.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking(동기). 소켓 bind는 커널 호출 1회로 수 µs 내 완료됩니다.
    /// <br/><br/>
    /// <b>[TOCTOU 창:]</b> 포트 반환 직후 ~ <see cref="ServerLib.Interface.IServerListener.Start"/> 호출 사이에
    /// 다른 프로세스가 해당 포트를 가로챌 수 있습니다. 순차 실행되는 예제에서는 이 창이 무시할 수준입니다.
    /// </remarks>
    public static int GetFreePort()
    {
        // TcpListener(IPAddress.Loopback, 0): OS에게 포트 0을 요청하면 사용 가능한 임시 포트를 원자적으로 할당.
        // 하드코딩 포트를 사용하면 이전 예제의 TIME_WAIT 상태와 충돌할 수 있어 매번 새 포트를 요청한다.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// <see cref="TaskCompletionSource"/>가 신호를 받거나 타임아웃이 경과할 때까지 비동기로 대기합니다.
    /// I/O 스레드 콜백에서 신호를 받아 결정적으로 동기화하는 데 사용합니다.
    /// </summary>
    /// <param name="tcs">수신 콜백이 신호를 보내는 완료 소스입니다.</param>
    /// <param name="timeout">최대 대기 시간입니다. 이 시간 내에 신호가 없으면 타임아웃으로 간주합니다.</param>
    /// <exception cref="TimeoutException">타임아웃 시간 내에 신호를 받지 못한 경우 발생합니다.</exception>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. TCS.Task와 Task.Delay는 모두 동시 await 안전합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> <see cref="Task.WhenAny"/> 호출 시 내부적으로 작은 Task 컨테이너 1회 할당(불가피).
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 비동기 대기입니다.
    /// <br/><br/>
    /// <b>[설계 이유:]</b> 수신 콜백은 I/O 스레드 풀에서 실행되므로 bare <c>Task.Delay</c>로 타이밍을 추정하면 flaky해집니다.
    /// TCS는 콜백에서 정확히 신호를 주므로 결정적 동기화를 보장합니다.
    /// </remarks>
    public static async Task WaitSignaledAsync(TaskCompletionSource tcs, TimeSpan timeout)
    {
        // TaskCompletionSource: 이벤트 기반 비동기 신호 — 콜백 스레드가 TrySetResult()를 호출하면
        // await 중인 Task가 완료 상태가 되어 호출 스레드로 제어가 넘어온다. Monitor.Wait보다 비동기 친화적.
        using var cts = new CancellationTokenSource(timeout);
        // CancellationTokenSource: 타임아웃 발화 시 Task.Delay가 취소되어 WhenAny가 진행됨.
        // 링크드 CTS 없이 별도 Delay로 구성해 tcs.Task와 경쟁 — 어느 쪽이 먼저 완료되면 즉시 반환.
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cts.Token));
        if (completed != tcs.Task)
            throw new TimeoutException($"예상된 신호를 {timeout.TotalSeconds:F1}초 내에 받지 못했습니다.");
    }

    /// <summary>
    /// UDP 소켓을 로컬 루프백에 바인딩하고 OS가 할당한 포트 번호를 반환합니다.
    /// </summary>
    /// <param name="udp">포트 번호를 확인할 UdpClient입니다.</param>
    /// <returns>OS가 할당한 로컬 포트 번호입니다.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe(단순 읽기).
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public static int GetUdpPort(UdpClient udp)
        => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
}

/// <summary>예제에서 세션에 붙이는 사용자 정의 컨텍스트 데이터입니다.</summary>
/// <param name="PlayerId">플레이어 고유 식별자입니다.</param>
/// <param name="Nickname">플레이어 표시 이름입니다.</param>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변 record이므로 Thread-safe.
/// <b>[Memory Allocation:]</b> 세션당 1회 힙 할당(record는 class).
/// </remarks>
internal record GameContext(int PlayerId, string Nickname);
