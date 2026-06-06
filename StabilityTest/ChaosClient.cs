using System.Net;
using System.Net.Sockets;

namespace StabilityTest;

/// <summary>
/// 연결 폭주·세션 정리 자극용 카오스 클라이언트. <b>앱 데이터를 0바이트 송신</b>하므로
/// 서버의 권위 received 집계를 오염시키지 않습니다(데이터유실 단언의 결정성 보장).
/// </summary>
public static class ChaosClient
{
    /// <summary>
    /// <paramref name="count"/>개 연결을 거의 동시에 열고, 짧게 유휴 후 RST로 급작 종료합니다.
    /// 서버의 accept 루프와 세션 정리(누수) 경로를 자극합니다.
    /// </summary>
    public static Task StormAsync(string host, int port, int count, CancellationToken ct)
    {
        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
            tasks[i] = OneAsync(host, port, ct);
        return Task.WhenAll(tasks);
    }

    private static async Task OneAsync(string host, int port, CancellationToken ct)
    {
        // raw Socket: LingerOption(true,0)으로 close 시 FIN 대신 RST를 보내 급작 이탈을 정밀 재현
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Parse(host), port, ct);
            // 짧은 유휴 — accept 직후 세션이 등록된 상태에서 RST가 나도록
            await Task.Delay(Random.Shared.Next(5, 50), ct);
            // SO_LINGER=0: 커널이 큐를 버리고 즉시 RST 전송 → 서버는 비정상 종료 경로로 세션을 정리해야 함
            socket.LingerState = new LingerOption(true, 0);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { /* 폭주 중 연결 거부/리셋은 정상 — 서버가 죽지만 않으면 됨 */ }
        finally
        {
            socket.Dispose(); // Linger 0이면 RST, 미설정 경로(예외 시)면 일반 close
        }
    }
}
