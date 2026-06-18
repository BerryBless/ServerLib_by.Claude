using ServerLib;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace SoakTest;

/// <summary>
/// 단일 클라이언트 연결 churn 루프를 실행합니다.
/// connect → DamagePacket × N 송신 → 해제를 취소될 때까지 무한 반복합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe. <see cref="RunAsync"/>는 단일 Task에서만 호출합니다.
/// 다수의 인스턴스를 병렬로 생성해도 각자 독립적인 <see cref="IClientConnection"/>을 사용하므로 안전합니다.
/// <b>[Memory:]</b> DamagePacket 버퍼는 생성자에서 1회 직렬화 후 모든 사이클에서 재사용합니다(무할당).
/// <c>PingInterval</c>을 설정하지 않아 하트비트 송신 없음 — 서버 received 카운트를 DamagePacket만으로 유지합니다.
/// <b>[Blocking:]</b> Non-blocking. <c>await using</c>으로 graceful FIN을 보장해 서버 데이터유실 결정성을 확보합니다.
/// </remarks>
public sealed class SoakClient
{
    private readonly string _host;
    private readonly int    _port;
    private readonly int    _sendsPerConn;
    private readonly int    _churnDelayMs;
    private readonly int    _receiveSettleMs;
    private readonly SoakStats _stats;

    // ReadOnlyMemory<byte>: 1회 직렬화한 DamagePacket 버퍼를 모든 사이클에서 참조 재사용
    // SendAsync(ReadOnlyMemory<byte>)는 내부적으로 Pipe에 write만 하므로 버퍼 소유권은 호출자 유지 가능
    private readonly ReadOnlyMemory<byte> _dmgBuf;

    /// <summary>
    /// 클라이언트 churn 루프를 초기화합니다.
    /// DamagePacket 버퍼를 한 번만 직렬화해 재사용 준비합니다.
    /// </summary>
    /// <param name="host">서버 호스트 주소입니다.</param>
    /// <param name="port">서버 포트입니다.</param>
    /// <param name="sendsPerConn">연결당 DamagePacket 송신 횟수입니다.</param>
    /// <param name="churnDelayMs">사이클 간 지연(밀리초). 0이면 즉시 재연결합니다.</param>
    /// <param name="receiveSettleMs">서버 응답 수신 여유 시간(밀리초)입니다.</param>
    /// <param name="stats">공유 lock-free 집계 카운터입니다.</param>
    public SoakClient(
        string host, int port,
        int sendsPerConn, int churnDelayMs, int receiveSettleMs,
        SoakStats stats)
    {
        _host            = host;
        _port            = port;
        _sendsPerConn    = sendsPerConn;
        _churnDelayMs    = churnDelayMs;
        _receiveSettleMs = receiveSettleMs;
        _stats           = stats;

        // BinaryPacketSerializer: Thread-safe — 내부 상태 없음. 생성자에서 1회만 사용.
        var serializer = new BinaryPacketSerializer();
        var pkt        = new DamagePacket { Amount = 100 };
        int sz         = PacketPool.HeaderSize + pkt.GetBodySize(); // 4(헤더) + 4(int) = 8B
        var buf        = new byte[sz];
        serializer.Serialize(pkt, buf); // Span<byte>로 암묵 변환 — byte[] 힙 할당 1회만 발생
        _dmgBuf = buf.AsMemory();
    }

    /// <summary>
    /// 취소 신호가 올 때까지 연결 churn 루프를 무한 반복합니다.
    /// </summary>
    /// <param name="ct">루프 중단 신호 토큰입니다.</param>
    /// <remarks>
    /// <b>[종료 보장:]</b> <c>await using</c>으로 모든 경로에서 <c>DisposeAsync</c>(graceful FIN)가 호출됩니다.
    /// RST 방식 미사용 — 서버 received 카운터와 클라이언트 sent 카운터의 정합성을 보장합니다.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // IClientConnection: IAsyncDisposable — await using으로 사이클 종료 시 graceful TCP FIN 보장
            // 매 사이클 새 인스턴스: 연결 수립/해제 경로를 반복 실행해 연결 풀 leak·fd 누수 검증
            await using IClientConnection conn = ServerNet.CreateClient();

            // OnReceived: IO 스레드에서 호출 — 즉시 반환. Pipe 버퍼는 콜백 반환 직후 반납되므로 복사 불필요.
            conn.OnReceived = _ =>
            {
                _stats.IncReceived();
                return ValueTask.CompletedTask;
            };
            // PingInterval 미설정: 하트비트 패킷 없음 → 서버 totalReceived를 DamagePacket 수신만으로 유지

            try
            {
                await conn.ConnectAsync(_host, _port, ct);
                _stats.IncConnect();

                for (int k = 0; k < _sendsPerConn; k++)
                {
                    // SendAsync(ReadOnlyMemory<byte>): Pipe에 write 후 즉시 반환(non-blocking 쓰기)
                    // _dmgBuf를 재사용: SendAsync가 반환하기 전에 버퍼를 사용하므로 안전(동기 Pipe 쓰기)
                    await conn.SendAsync(_dmgBuf, ct);
                    _stats.IncSent();
                }

                // 서버가 연결 직후 MobHpPacket을 브로드캐스트하므로, 수신 여유 시간을 두어 recv 카운트 반영
                if (_receiveSettleMs > 0)
                    await Task.Delay(_receiveSettleMs, ct);
            }
            catch (OperationCanceledException) { break; } // 정상 취소 — 루프 탈출
            catch (Exception)
            {
                _stats.IncError(); // 연결 실패·송신 오류 등 — Hard 판정 기준값 증가
            }
            // await using 블록 종료: DisposeAsync 호출 → TCP FIN → 서버 세션 정리

            _stats.IncCycle();

            if (_churnDelayMs > 0)
            {
                try { await Task.Delay(_churnDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
