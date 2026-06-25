using ServerLib;
using ServerLib.Interface;
using SoakTest.Workloads;

namespace SoakTest;

/// <summary>
/// 단일 클라이언트 연결 churn 루프를 실행합니다.
/// connect → <see cref="IWorkload.RunCycleAsync"/> → 해제를 취소될 때까지 무한 반복합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe. <see cref="RunAsync"/>는 단일 Task에서만 호출합니다.
/// 다수의 인스턴스를 병렬로 생성해도 각자 독립적인 <see cref="IClientConnection"/>·<see cref="IWorkload"/>를
/// 사용하므로 안전합니다.
/// <b>[Memory:]</b> 연결 수명 관리만 담당. 패킷 버퍼 관리는 <see cref="IWorkload"/> 구현이 책임집니다.
/// <c>PingInterval</c>을 설정하지 않아 하트비트 송신 없음.
/// <b>[Blocking:]</b> Non-blocking. <c>await using</c>으로 graceful FIN을 보장해 서버 세션 정리를 확인합니다.
/// </remarks>
public sealed class SoakClient
{
    private readonly string    _host;
    private readonly int       _port;
    private readonly int       _churnDelayMs;
    private readonly SoakStats _stats;

    // IWorkload: 워크로드 전략 패턴 — DamageWorkload(기존 동작) 또는 TicketingWorkload 중 하나.
    // 연결 수명(connect/FIN)은 SoakClient, "무엇을 보낼 것인가"는 IWorkload가 소유.
    private readonly IWorkload _workload;

    /// <summary>
    /// 클라이언트 churn 루프를 초기화합니다.
    /// </summary>
    /// <param name="host">서버 호스트 주소입니다.</param>
    /// <param name="port">서버 포트입니다.</param>
    /// <param name="churnDelayMs">사이클 간 지연(밀리초). 0이면 즉시 재연결합니다.</param>
    /// <param name="stats">공유 lock-free 집계 카운터입니다.</param>
    /// <param name="workload">이 클라이언트의 워크로드 전략 인스턴스입니다.</param>
    public SoakClient(
        string host, int port,
        int churnDelayMs,
        SoakStats stats,
        IWorkload workload)
    {
        _host         = host;
        _port         = port;
        _churnDelayMs = churnDelayMs;
        _stats        = stats;
        _workload     = workload;
    }

    /// <summary>
    /// 취소 신호가 올 때까지 연결 churn 루프를 무한 반복합니다.
    /// </summary>
    /// <param name="ct">루프 중단 신호 토큰입니다.</param>
    /// <remarks>
    /// <b>[종료 보장:]</b> <c>await using</c>으로 모든 경로에서 <c>DisposeAsync</c>(graceful FIN)가 호출됩니다.
    /// RST 방식 미사용 — 서버 세션 정리 경로를 결정론적으로 실행합니다.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        int cycleIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            // IClientConnection: IAsyncDisposable — await using으로 사이클 종료 시 graceful TCP FIN 보장.
            // 매 사이클 새 인스턴스: 연결 수립/해제 경로를 반복 실행해 연결 풀 leak·fd 누수 검증.
            await using IClientConnection conn = ServerNet.CreateClient();

            // OnReceived: IO 스레드에서 호출 — 즉시 반환. fire-and-pace: 응답 내용 파싱 없음.
            // 수신 카운터만 유지해 서버 응답 흐름을 간접 관찰.
            conn.OnReceived = _ =>
            {
                _stats.IncReceived();
                return ValueTask.CompletedTask;
            };
            // PingInterval 미설정: 하트비트 없음 → 서버 received는 워크로드 패킷만 카운트

            try
            {
                await conn.ConnectAsync(_host, _port, ct);
                _stats.IncConnect();

                // 워크로드 실행: 연결 위에서 한 사이클 분량 트래픽 발사
                await _workload.RunCycleAsync(conn, cycleIndex, ct);
            }
            catch (OperationCanceledException) { break; } // 정상 취소 — 루프 탈출
            catch (Exception)
            {
                // 연결 실패·송신 오류 등 — Hard 판정 기준값 증가.
                // SeatTaken·RateLimited는 서버 응답이라 클라가 예외를 받지 않음 → 여기서 카운트 안 됨.
                _stats.IncError();
            }
            // await using 블록 종료: DisposeAsync 호출 → TCP FIN → 서버 세션 정리

            _stats.IncCycle();
            cycleIndex++;

            if (_churnDelayMs > 0)
            {
                try { await Task.Delay(_churnDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
