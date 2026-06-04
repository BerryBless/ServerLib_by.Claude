using System.Net;
using System.Net.Sockets;

namespace ServerLib.Core.Transport;

// UDP 홀펀칭: 두 NAT 피어가 중계 서버를 통해 서로의 공인 주소를 교환하고
// 동시에 UDP 패킷을 전송하여 NAT 매핑을 열어 직접 통신을 성립시킨다.
public sealed class UdpHolePuncher : IDisposable
{
    private readonly UdpClient _udp;
    private int _disposed;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public UdpHolePuncher(int localPort = 0)
    {
        _udp = new UdpClient(localPort); // localPort=0이면 OS가 임시 포트 할당
        // SO_REUSEADDR: TIME_WAIT 상태 포트 재바인드 허용 → 재연결 시 동일 포트 즉시 재사용
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    // 중계 서버에 자신의 공인 엔드포인트 등록
    public async ValueTask RegisterAsync(IPEndPoint relayEndPoint, Guid peerId, CancellationToken ct = default)
    {
        var payload = peerId.ToByteArray();  // Guid 16바이트 고정 표현 — 등록 메시지 페이로드
        // UdpClient.SendAsync는 타임아웃 없는 Task → WaitAsync(ct)로 취소·타임아웃 의미 부여
        await _udp.SendAsync(payload, payload.Length, relayEndPoint).WaitAsync(ct);
    }

    // 상대방 공인 엔드포인트로 홀펀칭 패킷 전송 (동시 시작 필요)
    public async ValueTask PunchAsync(IPEndPoint peerEndPoint, int attempts = 5, CancellationToken ct = default)
    {
        ReadOnlyMemory<byte> punch = new byte[] { 0xFF };  // 1바이트 마커
        for (int i = 0; i < attempts && !ct.IsCancellationRequested; i++)
        {
            await _udp.SendAsync(punch, peerEndPoint, ct);
            // 시도 간 50ms 간격 — 커널이 연속 전송을 합치지 않고 NAT 매핑을 여러 번 자극하도록 분산
            await Task.Delay(50, ct);
        }
    }

    // 홀펀칭 성공 확인: 상대방 패킷 수신 대기
    public async ValueTask<IPEndPoint?> WaitForPeerAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // 링크드 CTS: 외부 취소(ct)와 타임아웃(CancelAfter)을 하나로 묶음 — 둘 중 먼저 발생하면 ReceiveAsync 중단
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var result = await _udp.ReceiveAsync(timeoutCts.Token);
            return result.RemoteEndPoint;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Interlocked.Exchange: 이전 값을 원자적으로 반환 → 첫 호출자만 진행(멱등 Dispose)
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _udp.Dispose();
    }
}
