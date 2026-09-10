using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using CounterClient;
using CounterServer;
using ServerLib;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;
using Xunit;

namespace CounterExample.Tests;

/// <summary>
/// 실제 루프백 소켓 위에서 <see cref="CounterHandler"/>(서버)와 <see cref="CounterScenario"/>(클라이언트)를
/// 그대로 구동해 경합 카운터 예제를 End-to-End 검증합니다.
/// </summary>
/// <remarks>
/// <b>[알고리즘을 복제하지 않는다]</b><br/>
/// 서버 측은 <c>CounterServer/Program.cs</c>가 쓰는 <b>바로 그</b> <see cref="CounterHandler.HandleAsync"/>를
/// <c>listener.OnReceived</c>에 연결하고, 클라이언트 측 주 시나리오는 <c>CounterClient/Program.cs</c>가 호출하는
/// <b>바로 그</b> <see cref="CounterScenario.RunAsync"/>를 호출합니다. 프로토콜 수준의 실패 경로(잘못된 길이·미지 ID)만
/// 원시 프레임을 직접 조립합니다 — 예제 API로는 만들 수 없는 프레임이기 때문입니다.
/// <br/><br/>
/// <b>[테스트 격리]</b> 각 테스트가 <see cref="GetFreePort"/>로 독립 포트를 확보하고,
/// <see cref="CounterState"/>도 테스트마다 새로 만듭니다(<c>static</c>이 아니므로 서로 오염되지 않습니다).
/// 서버는 <see cref="IPAddress.Loopback"/> 전용 바인딩으로 외부에 노출되지 않습니다.
/// <br/><br/>
/// <b>[검증 한계 — 배리어의 필요성은 증명되지 않는다]</b><br/>
/// 아래 시나리오 테스트는 "배리어 경로가 기대값을 산출한다"까지만 보장합니다.
/// 배리어를 제거했을 때의 결함은 <b>비결정적</b> 오값으로만 드러나므로, 그것을 단언하려 하면
/// flaky 테스트가 됩니다. 배리어의 필요성은 설계 근거(서버 수신 루프의 세션별 순차 처리)로만 성립합니다.
/// </remarks>
public class CounterEndToEndTests
{
    // BinaryPacketSerializer: 무상태(stateless), Thread-safe → 병렬 테스트가 공유해도 안전.
    private static readonly BinaryPacketSerializer Serializer = new();

    // ── 타임아웃 상수 ────────────────────────────────────────────────────────
    // 단순 왕복(연결 1개, 패킷 수 개)은 루프백에서 <1ms지만 CI 스케줄링 지연을 고려해 5초.
    private const int RoundTripTimeoutMs = 5_000;
    // 8연결 × 1,750회 = 14,000 패킷 왕복. 단독 실행은 수 초지만, 전체 스위트 병렬 실행 시의
    // 스레드 풀 경합까지 감안해 30초 상한을 둔다(실측 기반: 아래 시나리오 테스트가 Elapsed를 출력).
    private const int ScenarioTimeoutSeconds = 30;
    // 무응답 서버 테스트 전용 짧은 기한. 이 값을 넘겨 실제로 TimeoutException이 나는지 본다.
    private const int UnresponsiveTimeoutMs = 1_000;
    // 기한 만료 후 정리까지 포함해 RunAsync가 반드시 반환해야 하는 상한(테스트가 매달리지 않게 하는 안전망).
    private const int UnresponsiveObserveMarginMs = 15_000;

    // ── 기본 시나리오 규모 상수 ──────────────────────────────────────────────
    private const int ScenarioConnections = 8;
    private const int ScenarioIncrements = 1_000;
    private const int ScenarioDecrements = 750;
    // 총 연산 0 시나리오: 더하기 = 빼기. Value는 0이지만 AppliedOps는 2배로 쌓여야 한다.
    private const int BalancedConnections = 4;
    private const int BalancedOpsEachWay = 500;
    // 단일 연결 시나리오.
    private const int SingleConnectionIncrements = 5;
    private const int SingleConnectionDecrements = 2;
    // 미지 패킷 ID. 기존 사용 ID(1~19)와 하트비트 예약 ID(0xFFFE/0xFFFF)를 모두 피한다.
    // 서버는 정상 PING(0xFFFE)만 하트비트로 가로채므로 그 외 ID는 앱 경로(CounterHandler)에 도달한다.
    private const ushort UnknownPacketId = 250;

    /// <summary>OS에서 임시 포트를 확보해 반환합니다.</summary>
    /// <remarks>
    /// <see cref="TcpListener"/>(Loopback, 0): 포트 0으로 바인딩하면 커널 TCP/IP 스택이 미사용 임시 포트를 배정합니다.
    /// Stop() 이후 실제 바인딩까지 짧은 TOCTOU 창이 있으나, 루프백 전용 테스트에서는 탈취 가능성이 극히 낮습니다.
    /// </remarks>
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// <c>CounterServer/Program.cs</c>와 동일한 와이어링(실제 <see cref="CounterHandler"/>)으로 리스너를 시작합니다.
    /// </summary>
    /// <param name="port">바인딩할 포트입니다.</param>
    /// <param name="state">이 리스너가 사용할 공유 카운터입니다.</param>
    /// <param name="sessionFaulted">
    /// 프로토콜 위반 등으로 세션이 오류 종료될 때 완료되는 신호입니다.
    /// 실패 경로 테스트가 "서버가 실제로 그 패킷을 처리(=거부)한 뒤" 상태를 단언하도록 하는 배리어입니다.
    /// </param>
    /// <remarks>호출자는 반드시 <c>finally</c>에서 <see cref="IServerListener.Stop"/>을 호출해야 합니다.</remarks>
    private static IServerListener StartCounterListener(
        int port, CounterState state, out Task<Exception> sessionFaulted)
    {
        // TaskCompletionSource<Exception>: IO 스레드(OnClientError 콜백)에서 발생한 예외를 테스트 스레드로 전달하는 신호기.
        // RunContinuationsAsynchronously: SetResult 시 대기자가 IO 스레드에서 인라인 실행되지 않게 해 수신 루프 점유를 막는다.
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionFaulted = faulted.Task;

        IServerListener listener = ServerNet.CreateListener();
        var handler = new CounterHandler(state);

        // CounterServer/Program.cs와 동일: 핸들러 메서드를 그대로 콜백으로 사용한다.
        listener.OnReceived = handler.HandleAsync;
        listener.OnClientError = (ISession _, Exception ex) =>
        {
            faulted.TrySetResult(ex);
            return ValueTask.CompletedTask;
        };

        // Start(port, IPAddress.Loopback): 루프백 전용 바인딩. accept 루프를 백그라운드 Task로 시작(Non-blocking).
        listener.Start(port, IPAddress.Loopback);
        return listener;
    }

    /// <summary>지정 설정으로 <see cref="CounterScenario.RunAsync"/>를 실행할 옵션을 만듭니다.</summary>
    private static CounterScenarioOptions OptionsFor(int port, int connections, int increments, int decrements)
        => new()
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectionCount = connections,
            IncrementsPerConnection = increments,
            DecrementsPerConnection = decrements,
            Timeout = TimeSpan.FromSeconds(ScenarioTimeoutSeconds),
        };

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 1 — 새 서버의 초기 조회는 (0, 0)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>증감을 보내기 전에 조회하면 <c>Value=0, AppliedOps=0</c>이 회신되는지 검증합니다.</summary>
    /// <remarks>조회 요청 자체는 증감이 아니므로 <c>AppliedOps</c>를 늘리지 않아야 합니다.</remarks>
    [Fact]
    public async Task Query_OnFreshServer_ReturnsZeroValueAndZeroAppliedOps()
    {
        int port = GetFreePort();
        var state = new CounterState();
        IServerListener listener = StartCounterListener(port, state, out _);

        try
        {
            await using var client = new RawCounterClient();
            await client.ConnectAsync(port);

            CounterSnapshot snapshot = await client.QueryAsync();

            Assert.Equal(0L, snapshot.Value);
            Assert.Equal(0L, snapshot.AppliedOps);
            // 조회를 한 번 더 해도 AppliedOps가 늘지 않아야 한다(조회는 증감이 아니다).
            Assert.Equal(0L, (await client.QueryAsync()).AppliedOps);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 2 — 단일 연결 증감
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>연결 하나가 더하기·빼기를 보낸 뒤 조회하면 순 결과와 연산 수가 정확한지 검증합니다.</summary>
    /// <remarks>
    /// 같은 세션의 패킷은 서버 수신 루프가 순차 처리하므로, 마지막 명령 뒤의 조회 응답은
    /// 앞선 모든 명령이 적용된 뒤의 값입니다(이것이 시나리오 배리어의 근거).
    /// </remarks>
    [Fact]
    public async Task SingleConnection_IncrementsAndDecrements_AreAllApplied()
    {
        // checked: 상수를 키웠을 때 기대값이 조용히 오버플로하는 오검증을 막는다.
        long expectedValue = checked(SingleConnectionIncrements - SingleConnectionDecrements);
        long expectedOps = checked(SingleConnectionIncrements + SingleConnectionDecrements);

        int port = GetFreePort();
        var state = new CounterState();
        IServerListener listener = StartCounterListener(port, state, out _);

        try
        {
            await using var client = new RawCounterClient();
            await client.ConnectAsync(port);

            for (int i = 0; i < SingleConnectionIncrements; i++)
                await client.SendIncrementAsync();
            for (int i = 0; i < SingleConnectionDecrements; i++)
                await client.SendDecrementAsync();

            CounterSnapshot snapshot = await client.QueryAsync();

            Assert.Equal(expectedValue, snapshot.Value);
            Assert.Equal(expectedOps, snapshot.AppliedOps);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 3 — 8연결 동시 경합 후 배리어·최종 조회 (주 시나리오)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 실제 <see cref="CounterScenario.RunAsync"/>로 8연결이 동시에 증감했을 때
    /// 최종값이 <c>8 × (1,000 − 750) = 2,000</c>, 적용 연산 수가 <c>8 × 1,750 = 14,000</c>인지 검증합니다.
    /// </summary>
    [Fact]
    public async Task Scenario_EightConnections_ProducesDeterministicFinalValue()
    {
        long expectedValue = checked((long)ScenarioConnections * (ScenarioIncrements - ScenarioDecrements));
        long expectedOps = checked((long)ScenarioConnections * (ScenarioIncrements + ScenarioDecrements));

        int port = GetFreePort();
        var state = new CounterState();
        IServerListener listener = StartCounterListener(port, state, out _);

        try
        {
            CounterScenarioResult result = await CounterScenario.RunAsync(
                OptionsFor(port, ScenarioConnections, ScenarioIncrements, ScenarioDecrements));

            Assert.Equal(new CounterSnapshot(0, 0), result.InitialSnapshot);
            Assert.Equal(expectedValue, result.ExpectedValue);
            Assert.Equal(expectedOps, result.ExpectedAppliedOps);
            Assert.Equal(expectedValue, result.ActualValue);
            Assert.Equal(expectedOps, result.ActualAppliedOps);
            Assert.True(result.Passed, $"시나리오 FAIL: Value {result.ActualValue}/{result.ExpectedValue}, " +
                                       $"AppliedOps {result.ActualAppliedOps}/{result.ExpectedAppliedOps}");

            // 서버 측 상태로도 교차 확인한다(클라이언트 응답만 믿지 않는다).
            Assert.Equal(expectedValue, state.Value);
            Assert.Equal(expectedOps, state.AppliedOps);

            // 실측 소요 시간이 타임아웃 상수 대비 충분히 여유로운지 확인한다.
            Assert.True(result.Elapsed < TimeSpan.FromSeconds(ScenarioTimeoutSeconds),
                $"실행 시간 {result.Elapsed}이 기한 {ScenarioTimeoutSeconds}초에 근접합니다.");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 4 — 총 연산 0 (상쇄 은폐 방지)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 더하기와 빼기가 같은 수인 시나리오에서 <c>Value == 0</c>이면서
    /// <c>AppliedOps</c>는 전량 계수되는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// <c>Value == 0</c>만 보면 "패킷이 하나도 처리되지 않음"과 구분되지 않습니다.
    /// <c>AppliedOps</c> 단언이 그 은폐를 깹니다.
    /// </remarks>
    [Fact]
    public async Task Scenario_BalancedOperations_NetsToZeroButCountsEveryOperation()
    {
        long expectedOps = checked((long)BalancedConnections * (BalancedOpsEachWay + BalancedOpsEachWay));

        int port = GetFreePort();
        var state = new CounterState();
        IServerListener listener = StartCounterListener(port, state, out _);

        try
        {
            CounterScenarioResult result = await CounterScenario.RunAsync(
                OptionsFor(port, BalancedConnections, BalancedOpsEachWay, BalancedOpsEachWay));

            Assert.True(result.Passed);
            Assert.Equal(0L, result.ActualValue);
            Assert.Equal(expectedOps, result.ActualAppliedOps);
            Assert.Equal(0L, state.Value);
            Assert.Equal(expectedOps, state.AppliedOps);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 5 — 잘못된 본문 길이는 상태를 바꾸지 않는다
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 헤더가 <c>Id=3</c>인데 본문이 0B가 아닌 프레임은 거부되고,
    /// <c>Value</c>와 <c>AppliedOps</c>가 <b>둘 다</b> 변하지 않는지 검증합니다.
    /// 이어서 다른 연결은 정상 동작해야 합니다(오류는 해당 세션에만 국한).
    /// </summary>
    /// <remarks>
    /// <b>[배리어]</b> 프레임을 보낸 직후 단언하면 서버가 아직 처리하기 전일 수 있어 테스트가 무의미하게 통과합니다.
    /// 서버의 <c>OnClientError</c> 신호를 기다린 뒤에야 상태를 단언합니다.
    /// </remarks>
    [Fact]
    public async Task MalformedBodyLength_IsRejected_AndLeavesStateUntouched()
    {
        int port = GetFreePort();
        var state = new CounterState();
        IServerListener listener = StartCounterListener(port, state, out Task<Exception> sessionFaulted);

        try
        {
            await using (var bad = new RawCounterClient())
            {
                await bad.ConnectAsync(port);
                // Id=3(더하기)인데 본문 4B를 선언·전송 → 증감 명령의 고정 길이(0B) 규칙 위반.
                await bad.SendRawFrameAsync(BuildFrame(IncrementPacket.Id, bodyLength: 4));

                Exception error = await sessionFaulted.WaitAsync(TimeSpan.FromMilliseconds(RoundTripTimeoutMs));
                Assert.IsType<InvalidDataException>(error);
                Assert.Contains(CounterHandler.InvalidBodyMessagePrefix, error.Message);
            }

            // 거부된 프레임은 카운터를 전혀 건드리지 않아야 한다 — 값과 연산 수 둘 다.
            Assert.Equal(0L, state.Value);
            Assert.Equal(0L, state.AppliedOps);

            // 오류로 한 세션이 끊긴 뒤에도 새 연결은 정상 동작해야 한다.
            await using var healthy = new RawCounterClient();
            await healthy.ConnectAsync(port);
            await healthy.SendIncrementAsync();
            CounterSnapshot snapshot = await healthy.QueryAsync();

            Assert.Equal(1L, snapshot.Value);
            Assert.Equal(1L, snapshot.AppliedOps);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 6 — 알 수 없는 패킷 ID
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 서버가 모르는 ID(250)의 프레임을 거부하고 상태를 그대로 두는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// 서버 측은 정상 PING(0xFFFE)만 하트비트로 가로채며 그 외 ID는 앱 경로(<see cref="CounterHandler"/>)에 도달합니다.
    /// 따라서 250은 실제로 핸들러의 미지 ID 분기를 탑니다.
    /// </remarks>
    [Fact]
    public async Task UnknownPacketId_IsRejected_AndLeavesStateUntouched()
    {
        int port = GetFreePort();
        var state = new CounterState();
        IServerListener listener = StartCounterListener(port, state, out Task<Exception> sessionFaulted);

        try
        {
            await using (var bad = new RawCounterClient())
            {
                await bad.ConnectAsync(port);
                await bad.SendRawFrameAsync(BuildFrame(UnknownPacketId, bodyLength: 0));

                Exception error = await sessionFaulted.WaitAsync(TimeSpan.FromMilliseconds(RoundTripTimeoutMs));
                Assert.IsType<InvalidDataException>(error);
                Assert.Contains(CounterHandler.UnknownPacketMessagePrefix, error.Message);
            }

            Assert.Equal(0L, state.Value);
            Assert.Equal(0L, state.AppliedOps);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 7 — 처리 도중 서버 종료
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 시나리오 진행 중 서버가 종료되면 <see cref="CounterScenario.RunAsync"/>가
    /// PASS를 반환하지 않고 실패로 표면화하며, 모든 연결을 정리하고 반환하는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// <b>[결정성 — 시간에 의존하지 않는다]</b><br/>
    /// "N밀리초 뒤에 서버를 내린다"는 방식은 시나리오가 먼저 끝나 버리면 통과해 버리는 경주가 됩니다.
    /// 대신 서버가 <b>두 번째 조회부터는 응답하지 않고</b> 종료하도록 만듭니다.
    /// 첫 조회는 <see cref="CounterScenario.RunAsync"/>의 기준값 조회이고, 두 번째부터는 배리어 조회이므로,
    /// 최소 한 연결은 <b>반드시</b> 확인 응답을 받지 못합니다 — 시나리오는 절대 완료될 수 없습니다.
    /// <br/><b>[속도]</b> 클라이언트가 연결 해제 시 대기 중인 조회를 즉시 실패시키므로,
    /// 이 테스트는 기한 만료를 기다리지 않고 곧바로 끝나야 합니다(그 배선이 빠지면 기한까지 매달려 실패).
    /// </remarks>
    [Fact]
    public async Task Scenario_ServerStopsMidRun_FailsFastWithoutClaimingPass()
    {
        const int Connections = 4;
        const int IncrementsEach = 100;
        const int DecrementsEach = 100;
        const int RunTimeoutSeconds = 20;      // 이 테스트 전용 기한
        const int FailFastBudgetSeconds = 10;  // 연결 해제 배선이 동작하면 이보다 훨씬 빨리 끝나야 한다

        int port = GetFreePort();
        var state = new CounterState();
        var handler = new CounterHandler(state);

        IServerListener listener = ServerNet.CreateListener();
        // long: Interlocked 대상 카운터. 여러 세션의 IO 스레드가 동시에 증가시키므로 원자 연산이 필요하다.
        long queryCount = 0;
        // Stop()을 정확히 1회만 실행하기 위한 핸들. Stop()은 활성 세션을 순회하며 동기 Dispose하므로
        // 두 스레드가 동시에 들어가면 이중 Dispose로 ObjectDisposedException이 날 수 있다.
        // 결정성은 "응답을 드롭한다"에서 나오고 Stop()은 fail-fast 단언을 의미 있게 만드는 역할뿐이므로 1회면 충분하다.
        Task? stopTask = null;
        listener.OnReceived = (ISession session, ReadOnlyMemory<byte> data) =>
        {
            // TryParseHeader: 앞 4B를 무할당으로 해석해 조회 패킷만 골라낸다.
            if (PacketPool.TryParseHeader(data.Span, out ushort packetId, out _)
                && packetId == CounterQueryPacket.Id)
            {
                long n = Interlocked.Increment(ref queryCount);
                if (n == 2)
                {
                    // Task.Run으로 분리: 지금 이 콜백을 실행 중인 세션 자신을 인라인으로 Dispose하면
                    // 자기 자신의 완료를 기다리는 교착이 생길 수 있다. 정확히 1회만 스케줄한다.
                    stopTask = Task.Run(listener.Stop);
                }
                if (n >= 2)
                {
                    // 응답하지 않고 즉시 반환 → 이 연결의 배리어 확인은 영원히 오지 않는다(결정적 실패).
                    return ValueTask.CompletedTask;
                }
            }
            return handler.HandleAsync(session, data);
        };
        listener.Start(port, IPAddress.Loopback);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = new CounterScenarioOptions
            {
                Host = "127.0.0.1",
                Port = port,
                ConnectionCount = Connections,
                IncrementsPerConnection = IncrementsEach,
                DecrementsPerConnection = DecrementsEach,
                Timeout = TimeSpan.FromSeconds(RunTimeoutSeconds),
            };

            // 값 불일치는 Passed=false로, 통신 실패는 예외로 온다. 어느 쪽이든 PASS가 아니어야 한다.
            Exception? captured = null;
            CounterScenarioResult? result = null;
            try
            {
                result = await CounterScenario.RunAsync(options);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            stopwatch.Stop();

            Assert.True(captured is not null || result?.Passed == false,
                "서버가 중도 종료됐는데도 시나리오가 PASS를 반환했습니다.");

            // 연결 해제 시 대기자를 즉시 실패시키는 배선이 있으면 기한(20초)을 기다리지 않는다.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(FailFastBudgetSeconds),
                $"서버 종료 후 시나리오가 기한까지 매달렸습니다({stopwatch.Elapsed}). 연결 해제 시 조회 대기자를 실패시키는 배선을 확인하십시오.");
        }
        finally
        {
            // 콜백이 스케줄한 Stop()이 끝난 뒤에만 정리한다. 두 Stop()이 겹치면 세션 이중 Dispose가 될 수 있다.
            if (stopTask is not null) await stopTask;
            else listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 테스트 8 — 무응답 서버 타임아웃
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 조회에 절대 응답하지 않는 서버를 상대로 <see cref="CounterScenario.RunAsync"/>가
    /// 기한 안에 <see cref="TimeoutException"/>으로 끝나고, 매달리지 않고 반환하는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// 단순히 대기를 포기하는 것이 아니라 <b>실제 작업 취소와 연결 정리</b>까지 마쳐야 합니다.
    /// "반환했다"만 확인하면 <c>finally</c>의 정리 루프가 중간에 빠져 연결이 새어도 통과하므로,
    /// 서버 측 <c>OnClientDisconnected</c>가 <b>연결 수만큼</b> 호출되는 것까지 확인합니다.
    /// </remarks>
    [Fact]
    public async Task Scenario_UnresponsiveServer_TimesOutAndCleansUp()
    {
        const int Connections = 2;

        int port = GetFreePort();

        IServerListener listener = ServerNet.CreateListener();
        // 수신은 하되 어떤 응답도 보내지 않는다 → 조회 대기가 영원히 풀리지 않는 상황을 만든다.
        listener.OnReceived = (ISession _, ReadOnlyMemory<byte> _) => ValueTask.CompletedTask;

        // 소켓이 실제로 정리됐음을 서버 쪽에서 관측하는 신호기.
        // TaskCompletionSource<bool>: IO 스레드(해제 콜백) → 테스트 스레드 전달. RunContinuationsAsynchronously로
        // 대기자가 IO 스레드에서 인라인 실행되지 않게 한다.
        var allDisconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // long: Interlocked 대상 카운터. 여러 세션의 IO 스레드가 동시에 증가시킨다.
        long disconnected = 0;
        listener.OnClientDisconnected = (ISession _) =>
        {
            if (Interlocked.Increment(ref disconnected) >= Connections)
                allDisconnected.TrySetResult(true);
            return ValueTask.CompletedTask;
        };
        listener.Start(port, IPAddress.Loopback);

        try
        {
            var options = new CounterScenarioOptions
            {
                Host = "127.0.0.1",
                Port = port,
                ConnectionCount = Connections,
                IncrementsPerConnection = 1,
                DecrementsPerConnection = 1,
                Timeout = TimeSpan.FromMilliseconds(UnresponsiveTimeoutMs),
            };

            var stopwatch = Stopwatch.StartNew();
            // WaitAsync: RunAsync가 정리까지 마치고 반환하지 않으면 여기서 TimeoutException으로 명확히 실패한다
            // (테스트가 무기한 hang되지 않도록 하는 안전망).
            await Assert.ThrowsAsync<TimeoutException>(
                () => CounterScenario.RunAsync(options).WaitAsync(TimeSpan.FromMilliseconds(UnresponsiveObserveMarginMs)));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(UnresponsiveObserveMarginMs),
                $"무응답 서버에서 시나리오 정리가 지연됐습니다({stopwatch.Elapsed}).");

            // 반환만으로는 부족하다 — 연결이 실제로 끊겼는지 서버 쪽에서 확인한다.
            // 정리 루프가 일부 연결을 빠뜨리면 이 대기가 타임아웃되어 실패한다.
            await allDisconnected.Task.WaitAsync(TimeSpan.FromMilliseconds(RoundTripTimeoutMs));
            Assert.Equal(Connections, (int)Interlocked.Read(ref disconnected));
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 헬퍼
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 임의의 ID와 <b>선언 본문 길이</b>를 가진 원시 프레임을 만듭니다(잘못된 프레임 생성용).
    /// </summary>
    /// <param name="packetId">헤더에 기록할 패킷 ID입니다.</param>
    /// <param name="bodyLength">헤더에 기록하고 실제로도 채울 본문 바이트 수입니다.</param>
    /// <remarks>
    /// 선언 길이와 실제 길이를 <b>일치</b>시키는 이유: 프레이밍 계층은 헤더가 선언한 만큼을 잘라 전달하므로,
    /// 여기서 불일치를 만들면 핸들러에 도달하기 전에 프레이밍 단계에서 갈립니다.
    /// 이 테스트가 겨냥하는 것은 <b>핸들러의 ID별 고정 길이 검증</b>입니다.
    /// </remarks>
    private static byte[] BuildFrame(ushort packetId, int bodyLength)
    {
        var frame = new byte[PacketPool.HeaderSize + bodyLength];
        PacketPool.WriteHeader(frame, packetId, bodyLength);
        return frame; // 본문은 0으로 채워진 상태 그대로 — 내용은 검증 대상이 아니다
    }

    /// <summary>
    /// 프로토콜 수준 테스트를 위해 원시 프레임을 직접 주고받는 최소 클라이언트입니다.
    /// </summary>
    /// <remarks>
    /// <see cref="CounterScenario"/>가 캡슐화한 연결은 예제 알고리즘 전용이라 임의의 잘못된 프레임을 보낼 수 없습니다.
    /// 이 헬퍼는 <b>클라이언트 알고리즘의 복제가 아니라</b> 프로토콜 하위 계층 접근 수단입니다
    /// (주 시나리오 검증은 실제 <see cref="CounterScenario.RunAsync"/>가 담당합니다).
    /// </remarks>
    private sealed class RawCounterClient : IAsyncDisposable
    {
        // ServerNet.CreateClient(): SocketPipelineClient(internal)를 IClientConnection으로 반환.
        private readonly IClientConnection _connection = ServerNet.CreateClient();

        // 조회 응답 대기자. IO 스레드(OnReceived)와 테스트 스레드가 함께 접근하므로 Interlocked로 소유권을 옮긴다.
        private TaskCompletionSource<CounterSnapshot>? _pending;

        public async Task ConnectAsync(int port)
        {
            _connection.OnReceived = data =>
            {
                TaskCompletionSource<CounterSnapshot>? waiter = Interlocked.Exchange(ref _pending, null);
                if (waiter is null) return ValueTask.CompletedTask;

                // data는 콜백 반환 후 무효가 되는 수신 버퍼 뷰 → 동기 검증·역직렬화 후 값 타입만 넘긴다.
                // 검증 없이 Deserialize하면 짧은 프레임에서 EndOfStreamException이 콜백 밖으로 새어
                // 수신 루프가 죽고, 명확한 단언 실패가 "타임아웃까지 hang"으로 퇴화한다.
                // 예외는 던지지 않고 대기자에 전달한다(CounterConnection과 동일한 원칙).
                ReadOnlySpan<byte> frame = data.Span; // zero-copy 뷰
                if (!PacketPool.TryParseHeader(frame, out ushort packetId, out int bodyLength)
                    || packetId != CounterValuePacket.Id
                    || bodyLength != CounterValuePacket.BodySize
                    || frame.Length != PacketPool.HeaderSize + CounterValuePacket.BodySize)
                {
                    waiter.TrySetException(new IOException(
                        $"조회 응답이 프로토콜에 맞지 않습니다(프레임 {frame.Length}B). " +
                        $"기대: Id={CounterValuePacket.Id}, 본문 {CounterValuePacket.BodySize}B."));
                    return ValueTask.CompletedTask;
                }

                CounterValuePacket packet = Serializer.Deserialize<CounterValuePacket>(frame);
                waiter.TrySetResult(new CounterSnapshot(packet.Value, packet.AppliedOps));
                return ValueTask.CompletedTask;
            };
            _connection.OnDisconnected = () =>
            {
                Interlocked.Exchange(ref _pending, null)
                    ?.TrySetException(new IOException("조회 대기 중 연결이 끊겼습니다."));
                return ValueTask.CompletedTask;
            };
            await _connection.ConnectAsync("127.0.0.1", port);
        }

        // SendAsync<T>: ArrayPool 대여 → 직렬화 → 소켓 직접 기록 → 반납. struct 패킷이라 박싱 없음.
        public ValueTask SendIncrementAsync() => _connection.SendAsync(new IncrementPacket());
        public ValueTask SendDecrementAsync() => _connection.SendAsync(new DecrementPacket());

        // ReadOnlyMemory<byte>: 배열 복사 없이 "포인터+길이" 뷰만 넘어간다(zero-copy).
        public ValueTask SendRawFrameAsync(byte[] frame) => _connection.SendAsync(frame.AsMemory());

        public async Task<CounterSnapshot> QueryAsync()
        {
            // RunContinuationsAsynchronously: TrySetResult 호출 IO 스레드에서 후속 코드가 인라인 실행되지 않게 한다.
            var waiter = new TaskCompletionSource<CounterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _pending, waiter, null) is not null)
                throw new InvalidOperationException("미완료 조회가 이미 있습니다.");

            // 대기자를 송신 전에 게시한다 — 루프백은 응답이 먼저 도착할 만큼 빠르다.
            await _connection.SendAsync(new CounterQueryPacket());
            return await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(RoundTripTimeoutMs));
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _pending, null)?.TrySetException(new IOException("연결이 정리되었습니다."));
            await _connection.DisposeAsync();
        }
    }
}
