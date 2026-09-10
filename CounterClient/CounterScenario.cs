using System.Diagnostics;
using System.IO;
using ServerLib;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace CounterClient;

/// <summary>
/// 서버가 <see cref="CounterValuePacket"/>으로 회신한 카운터 스냅샷입니다.
/// </summary>
/// <param name="Value">조회 시점의 카운터 값(더하기 − 빼기)입니다.</param>
/// <param name="AppliedOps">조회 시점까지 실제로 적용된 증감 연산의 총 횟수입니다.</param>
/// <remarks>
/// <b>[Thread Safety]</b> 불변 값 타입이므로 Thread-safe. <b>[Memory]</b> <c>readonly record struct</c>라 힙 할당이 없습니다.
/// <br/><b>[두 값의 원자성]</b> 서버는 두 값을 <b>따로</b> 원자 읽기 하므로, 갱신이 진행 중일 때의 스냅샷은
/// 두 값이 서로 다른 시점일 수 있습니다. 두 값을 함께 단언하는 것은 <b>정지 상태(배리어 이후 최종 조회)</b>에서만 유효합니다.
/// </remarks>
public readonly record struct CounterSnapshot(long Value, long AppliedOps);

/// <summary>
/// <see cref="CounterScenario.RunAsync"/>의 실행 설정입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety]</b> 생성 후 변경하지 않는 것을 전제로 합니다(<c>init</c> 전용 속성). 그렇게 쓰면 Thread-safe.
/// <br/><b>[Memory]</b> 인스턴스 1회 할당. <b>[Blocking]</b> 해당 없음(순수 데이터).
/// </remarks>
public sealed class CounterScenarioOptions
{
    /// <summary>서버 호스트입니다. 기본값은 루프백(127.0.0.1)입니다.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>서버 포트입니다. 기본값은 <c>CounterServer</c>가 바인딩하는 9300입니다.</summary>
    public int Port { get; init; } = 9300;

    /// <summary>동시에 경합시킬 연결 수입니다. 기본값 8.</summary>
    public int ConnectionCount { get; init; } = 8;

    /// <summary>연결 하나가 보낼 더하기 횟수입니다. 기본값 1,000.</summary>
    public int IncrementsPerConnection { get; init; } = 1_000;

    /// <summary>연결 하나가 보낼 빼기 횟수입니다. 기본값 750.</summary>
    public int DecrementsPerConnection { get; init; } = 750;

    /// <summary>실행 전체에 적용되는 기한입니다. 초과하면 <see cref="TimeoutException"/>과 함께 모든 연결을 정리합니다.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>설정값의 유효성을 검사합니다.</summary>
    /// <exception cref="ArgumentOutOfRangeException">값이 유효 범위를 벗어났을 때.</exception>
    /// <remarks>
    /// <b>[Blocking]</b> Non-blocking. <b>[Memory]</b> 정상 경로 무할당.
    /// <br/><b>[더하기+빼기 합계 규칙]</b> 합계 <b>0(연산이 전혀 없는 빈 실행)은 유효</b>합니다 — 이때 최종값·적용 연산 수가
    /// 모두 0이고 배리어·최종 조회 경로는 그대로 실행됩니다. <b>음수만 거부</b>합니다. 각 값은 위에서 이미 음수를 거부하므로
    /// 합계가 음수가 되는 경우는 <c>int</c> 덧셈 오버플로뿐이며, 이 검사가 그 오버플로를 <c>checked</c> 곱셈(<c>OverflowException</c>)
    /// 이전에 <see cref="ArgumentOutOfRangeException"/>으로 잡아 예외 계약을 지킵니다.
    /// </remarks>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConnectionCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(IncrementsPerConnection);
        ArgumentOutOfRangeException.ThrowIfNegative(DecrementsPerConnection);
        // 합계 0(빈 실행)은 허용하고 음수만 거부한다. 두 값 모두 위에서 음수를 배제했으므로, 합계가 음수가 되는 유일한 경로는
        // int 덧셈 오버플로다. ThrowIfNegative로 그 오버플로를 잡아, RunAsync의 checked 곱셈(OverflowException)에 도달하기 전에
        // API 계약대로 ArgumentOutOfRangeException을 던진다.
        ArgumentOutOfRangeException.ThrowIfNegative(
            IncrementsPerConnection + DecrementsPerConnection, "IncrementsPerConnection + DecrementsPerConnection");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Timeout, TimeSpan.Zero);
    }
}

/// <summary>
/// <see cref="CounterScenario.RunAsync"/>의 검증 결과입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety]</b> 불변(생성 후 변경 없음) → Thread-safe. <b>[Memory]</b> 인스턴스 1회 할당.
/// <br/><see cref="Passed"/>가 <see langword="false"/>면 값 불일치입니다. 통신 오류·타임아웃은 결과가 아니라
/// 예외로 표면화되므로, 결과 객체를 받았다는 것 자체가 "통신은 끝까지 성공했다"는 뜻입니다.
/// </remarks>
public sealed class CounterScenarioResult
{
    /// <summary>최종값과 적용 연산 수가 <b>둘 다</b> 기대값과 일치하면 <see langword="true"/>입니다.</summary>
    public required bool Passed { get; init; }

    /// <summary>기대 카운터 값 = 연결 수 × (더하기 − 빼기)입니다.</summary>
    public required long ExpectedValue { get; init; }

    /// <summary>정지 상태 최종 조회로 얻은 실제 카운터 값입니다.</summary>
    public required long ActualValue { get; init; }

    /// <summary>기대 적용 연산 수 = 연결 수 × (더하기 + 빼기)입니다.</summary>
    public required long ExpectedAppliedOps { get; init; }

    /// <summary>정지 상태 최종 조회로 얻은 실제 적용 연산 수입니다.</summary>
    public required long ActualAppliedOps { get; init; }

    /// <summary>증감 시작 전에 조회한 기준 스냅샷입니다. 새로 시작한 서버라면 <c>(0, 0)</c>이어야 합니다.</summary>
    public required CounterSnapshot InitialSnapshot { get; init; }

    /// <summary>연결 수립부터 최종 조회까지의 실행 시간입니다.</summary>
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 서버의 공유 카운터를 여러 연결이 <b>동시에</b> 증감시켜 경합을 실제로 유발하고,
/// 모든 처리가 끝난 정지 상태에서 최종값이 결정적 기대값과 일치하는지 검증하는 실행기입니다.
/// </summary>
/// <remarks>
/// <b>[이 클래스가 존재하는 이유]</b><br/>
/// <c>CounterClient/Program.cs</c>(수동 콘솔 데모)와 <c>CounterExample.Tests</c>(E2E 자동 검증)가
/// <b>같은 알고리즘</b>을 실행하도록 하기 위함입니다. 테스트가 시나리오를 복제하면 예제와 테스트가 갈라져
/// "테스트는 통과하는데 예제는 틀린" 상태가 생깁니다.
/// <br/><br/>
/// <b>[검증 절차 — 순서가 곧 정확성]</b>
/// <list type="number">
/// <item><description>연결 N개를 만들고 <b>전부 연결될 때까지</b> 기다립니다.</description></item>
/// <item><description>공통 시작 신호를 해제해 모든 연결이 <b>동시에</b> 증감을 쏟아붓게 합니다(경합 유발).</description></item>
/// <item><description>각 연결은 자신의 <b>마지막 증감 직후</b> 조회를 보내고 응답을 기다립니다.
/// 서버 수신 루프가 <b>같은 세션</b>의 패킷을 순차 <c>await</c>로 처리하므로, 이 응답은
/// <b>그 연결의 모든 증감이 이미 적용되었다</b>는 확인(배리어)입니다.</description></item>
/// <item><description><b>모든</b> 연결의 확인 응답을 받은 뒤에야 최종 조회를 1회 더 보냅니다. 이때가 정지 상태입니다.</description></item>
/// <item><description>최종 <c>Value</c>·<c>AppliedOps</c>를 기대값과 비교합니다.</description></item>
/// </list>
/// <b>송신 완료만 기다리고 바로 조회하거나, 중간 조회 응답 중 마지막 도착값을 최종값으로 쓰면 안 됩니다</b> —
/// 다른 연결의 처리가 아직 남아 있을 수 있고, 조회 응답은 그 시점의 값일 뿐입니다.
/// <br/><br/>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="RunAsync"/>는 자체 연결·상태를 생성하므로 여러 번 동시에 호출해도
/// 서로 간섭하지 않습니다. 다만 <b>같은 서버</b>를 대상으로 동시에 실행하면 카운터를 공유하므로 기대값이 깨집니다
/// (검증 실행은 서버당 1회).</description></item>
/// <item><description><b>Memory Allocation:</b> 연결·Task·대기자·스케줄 배열의 <b>초기 할당은 허용</b>합니다.
/// 실행 중 hot loop(수천 회 증감 송신)는 미리 직렬화해 둔 프레임을 재사용해 <b>패킷당 직렬화 할당이 0</b>입니다.
/// 전체 실행에 무할당을 선언하지는 않습니다.</description></item>
/// <item><description><b>Blocking:</b> 완전 비동기(Non-blocking). 어떤 경로에서도 스레드를 동기 블로킹하지 않습니다.</description></item>
/// <item><description><b>실행 전제:</b> <b>새로 시작한 서버</b>에 대해 <b>단일 실행</b>. 카운터는 프로세스 수명 동안 누적되므로
/// 재검증하려면 서버를 재시작해야 합니다.</description></item>
/// </list>
/// </remarks>
public static class CounterScenario
{
    // BinaryPacketSerializer: 무상태(stateless) → Thread-safe. 아래 프레임 상수를 만들 때 1회만 쓰이며,
    // static 필드 초기화는 텍스트 순서로 실행되므로 프레임보다 먼저 선언해야 한다.
    private static readonly BinaryPacketSerializer Serializer = new();

    // 미리 직렬화한 고정 프레임(각 4B = 헤더만, 본문 0B).
    // ReadOnlyMemory<byte>로 노출하는 이유: IClientConnection.SendAsync는 ReadOnlyMemory<byte>를 받으며
    // 전달 시 배열 복사가 아닌 "포인터+길이" 뷰만 넘어가므로(zero-copy) 송신마다 버퍼를 새로 만들 필요가 없다.
    // static readonly + 생성 후 절대 변경하지 않음 → 여러 연결 Task가 동시에 읽어도 안전(불변 공유).
    // 매번 PacketSendExtensions.SendAsync<T>를 쓰면 송신마다 ArrayPool Rent/Return + Serialize가 반복되는데,
    // 본문이 없는 고정 프레임은 결과가 항상 동일하므로 1회 직렬화 후 재사용하는 편이 hot loop에서 명백히 유리하다.
    private static readonly ReadOnlyMemory<byte> IncrementFrame = CreateFrame(new IncrementPacket());
    private static readonly ReadOnlyMemory<byte> DecrementFrame = CreateFrame(new DecrementPacket());
    private static readonly ReadOnlyMemory<byte> QueryFrame = CreateFrame(new CounterQueryPacket());

    /// <summary>
    /// 시나리오를 실행하고 검증 결과를 반환합니다.
    /// </summary>
    /// <param name="options">연결 수·증감 횟수·대상 주소·전체 기한 설정입니다.</param>
    /// <param name="cancellationToken">호출자 취소 토큰입니다.</param>
    /// <returns>기대값과 실측값, PASS/FAIL 판정이 담긴 결과입니다.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/>의 값이 유효 범위를 벗어날 때.</exception>
    /// <exception cref="TimeoutException"><see cref="CounterScenarioOptions.Timeout"/> 안에 완료하지 못했을 때.
    /// 던지기 전에 모든 연결을 정리합니다.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/>이 취소됐을 때.</exception>
    /// <exception cref="IOException">조회 대기 중 연결이 끊겼거나 서버 응답이 프로토콜에 맞지 않을 때.</exception>
    /// <remarks>
    /// <b>[Thread Safety]</b> 호출마다 독립된 연결·상태를 사용합니다(위 클래스 <c>remarks</c>의 단서 참조).
    /// <br/><b>[Blocking]</b> Non-blocking(완전 비동기).
    /// <br/><b>[Memory]</b> 연결 수·전체 연산 수에 비례하는 초기 할당이 발생하고, 증감 송신 루프는 프레임 재사용으로 직렬화 무할당입니다.
    /// <br/><b>[실패 시 정리]</b> 예외·타임아웃 어느 경로에서도 <c>finally</c>에서 <b>모든</b> 연결을 <c>DisposeAsync</c>합니다.
    /// 실패로 중단된 작업은 취소 토큰으로 실제 종료시키며, 대기만 포기하고 작업을 남기지 않습니다.
    /// <br/><b>[검증 결과]</b> 값 불일치는 <c>Passed == false</c>로 반환하고, 통신 오류·타임아웃은 예외로 표면화합니다.
    /// 어느 쪽이든 호출자가 PASS로 오인할 수 없습니다.
    /// </remarks>
    public static async Task<CounterScenarioResult> RunAsync(
        CounterScenarioOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        int opsPerConnection = checked(options.IncrementsPerConnection + options.DecrementsPerConnection);

        // checked: 설정값이 커지면 기대값 계산이 조용히 오버플로해 "틀린 기대값과 일치"하는 최악의 오검증이 생긴다.
        // 상수 폴딩이 아니라 런타임 설정값의 곱셈이므로 checked가 실제로 의미를 갖는다.
        long expectedValue = checked((long)options.ConnectionCount * (options.IncrementsPerConnection - options.DecrementsPerConnection));
        long expectedAppliedOps = checked((long)options.ConnectionCount * opsPerConnection);

        // 증감 순서 스케줄(true=빼기). 연결마다 시작 위치를 회전시켜 서로 다른 순서로 유입되게 한다.
        // bool[]: 생성 후 절대 변경하지 않으므로 모든 연결 Task가 락 없이 동시에 읽어도 안전(불변 공유 데이터).
        // 연산마다 분기 계산을 다시 하지 않고 배열을 미리 만드는 이유는 hot loop에서 나눗셈을 없애기 위함이다.
        bool[] schedule = BuildSchedule(options.IncrementsPerConnection, options.DecrementsPerConnection);

        // CancellationTokenSource.CreateLinkedTokenSource: 호출자 취소와 전체 기한을 하나의 토큰으로 합류시킨다.
        // 두 실패원(사용자 취소·시한 만료)을 한 토큰으로 수렴시키면 정리 경로가 하나로 고정되어
        // "대기만 포기하고 작업은 계속 도는" 좀비 Task가 생기지 않는다.
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(options.Timeout); // 내부 Timer 1개 무장 — 완료 시 Dispose에서 해제된다
        CancellationToken ct = deadlineCts.Token;

        var connections = new CounterConnection[options.ConnectionCount];
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // ── 1. 연결 수립 ──────────────────────────────────────────────────
            for (int i = 0; i < connections.Length; i++)
            {
                connections[i] = new CounterConnection();
                await connections[i].ConnectAsync(options.Host, options.Port, ct).ConfigureAwait(false);
            }

            // 시작 전 기준값 조회. 새 서버라면 (0, 0)이어야 한다.
            // 이 조회는 증감이 아니므로 AppliedOps를 늘리지 않는다.
            CounterSnapshot initial = await connections[0].QueryAsync(ct).ConfigureAwait(false);

            // ── 2. 공통 시작 신호 ─────────────────────────────────────────────
            //
            // TaskCompletionSource<bool>: 모든 워커가 이 하나의 Task를 await하다가 동시에 풀려나는 출발 게이트.
            // RunContinuationsAsynchronously: TrySetResult 호출 스레드에서 N개 워커의 후속 작업이 인라인 직렬 실행되는 것을 막는다.
            //   → 워커들이 실제로 스레드 풀에 흩어져 병렬 출발하므로 경합이 만들어진다(이 옵션이 없으면 사실상 순차 출발).
            var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var workers = new Task[connections.Length];
            for (int i = 0; i < connections.Length; i++)
                workers[i] = RunConnectionAsync(connections[i], i, connections.Length, schedule, startSignal.Task, ct);

            startSignal.TrySetResult(true);

            // ── 3. 배리어 — 전 연결의 확인 응답 수신 ──────────────────────────
            //
            // Task.WhenAll: 개별 Task의 예외를 자신의 AggregateException으로 흡수하므로,
            // 여기서 await하면 실패한 작업 전부가 "관측됨"이 되어 미관측 Task 예외로 프로세스에 남지 않는다.
            await ThrowIfDeadlineAsync(Task.WhenAll(workers), deadlineCts, cancellationToken, options.Timeout).ConfigureAwait(false);

            // ── 4. 정지 상태에서 최종 조회 ────────────────────────────────────
            //
            // 이 시점에는 모든 연결이 "내 증감은 전부 적용됐다"는 확인을 이미 받았다.
            // 따라서 서버에 미처리 증감이 남아 있지 않고(정지 상태), Value·AppliedOps 쌍을 함께 단언해도 된다.
            CounterSnapshot fin = await connections[0].QueryAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();

            return new CounterScenarioResult
            {
                Passed = fin.Value == expectedValue && fin.AppliedOps == expectedAppliedOps,
                ExpectedValue = expectedValue,
                ActualValue = fin.Value,
                ExpectedAppliedOps = expectedAppliedOps,
                ActualAppliedOps = fin.AppliedOps,
                InitialSnapshot = initial,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // 시한 만료를 호출자 취소와 구분해 표면화한다. 정리는 아래 finally가 수행한다.
            throw new TimeoutException(
                $"경합 카운터 시나리오가 기한({options.Timeout}) 안에 완료되지 않았습니다. 서버가 응답하지 않거나 처리가 지연되고 있습니다.");
        }
        finally
        {
            // 부분 초기화 실패를 포함해 이미 만든 연결은 모두 정리한다.
            // deadlineCts는 이 시점에 이미 취소됐거나 곧 Dispose되므로, 진행 중이던 송신·대기도 함께 풀린다.
            foreach (CounterConnection? conn in connections)
            {
                if (conn is null) continue;
                try { await conn.DisposeAsync().ConfigureAwait(false); }
                catch { /* 정리 중 오류는 원래 실패 원인을 가리지 않도록 무시 */ }
            }
        }
    }

    /// <summary>한 연결이 담당하는 증감 송신과, 마지막 증감 직후의 완료 확인 조회를 수행합니다.</summary>
    /// <remarks>
    /// <b>[Blocking]</b> Non-blocking. <b>[Memory]</b> 증감 송신은 미리 직렬화된 프레임을 재사용해 직렬화 할당이 없습니다.
    /// <br/>연결 안에서는 송신을 순차 <c>await</c>합니다 — 같은 연결의 순서를 보장해야 "마지막 증감 뒤 조회"가
    /// 완료 확인으로서 성립하기 때문입니다. 병렬성은 <b>연결 사이</b>에서 나옵니다.
    /// </remarks>
    private static async Task RunConnectionAsync(
        CounterConnection connection,
        int index,
        int connectionCount,
        bool[] schedule,
        Task startSignal,
        CancellationToken cancellationToken)
    {
        await startSignal.ConfigureAwait(false); // 공통 출발 게이트

        int total = schedule.Length;
        // 연결별 시작 위치를 균등하게 흩어 같은 순간에 서로 다른 종류의 명령이 유입되게 한다.
        // 회전은 항목 수를 바꾸지 않으므로 연결마다 더하기·빼기 횟수는 동일하게 유지된다.
        int offset = (int)((long)index * total / connectionCount);

        for (int k = 0; k < total; k++)
        {
            int slot = offset + k;
            if (slot >= total) slot -= total; // 나머지 연산 대신 뺄셈 — hot loop의 div를 피한다
            await connection.SendFrameAsync(schedule[slot] ? DecrementFrame : IncrementFrame, cancellationToken).ConfigureAwait(false);
        }

        // 마지막 증감 뒤 조회 → 응답 수신 = 이 연결의 모든 증감이 서버에 적용 완료됐다는 확인.
        // 반환값(중간 스냅샷)은 판정에 쓰지 않는다. 다른 연결이 아직 처리 중일 수 있기 때문이다.
        _ = await connection.QueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 증감 순서 스케줄을 만듭니다. <see langword="true"/>는 빼기, <see langword="false"/>는 더하기입니다.
    /// </summary>
    /// <param name="increments">더하기 횟수입니다.</param>
    /// <param name="decrements">빼기 횟수입니다.</param>
    /// <returns>길이 <c>increments + decrements</c>이고 <see langword="true"/>가 정확히 <paramref name="decrements"/>개인 배열입니다.</returns>
    /// <remarks>
    /// 앞쪽에 더하기를 몰고 뒤쪽에 빼기를 모으면 중간 시점의 카운터가 단조 증가·감소만 하여 경합 양상이 단조로워집니다.
    /// Bresenham 방식으로 빼기를 전 구간에 균등 분산해 증감이 실제로 뒤섞이게 합니다.
    /// <br/><b>[Memory]</b> 길이 <c>increments+decrements</c>의 <c>bool[]</c> 1회 할당(실행당 1개, 전 연결이 공유).
    /// <b>[Blocking]</b> Non-blocking.
    /// </remarks>
    private static bool[] BuildSchedule(int increments, int decrements)
    {
        int total = checked(increments + decrements);
        var schedule = new bool[total];
        if (decrements == 0) return schedule;

        long emitted = 0;
        for (int k = 0; k < total; k++)
        {
            // floor((k+1)*dec/total)이 증가하는 지점에서만 빼기를 낸다 → 정확히 dec개가 균등 분포한다.
            long next = (long)(k + 1) * decrements / total;
            if (next > emitted)
            {
                schedule[k] = true;
                emitted = next;
            }
        }
        return schedule;
    }

    /// <summary>본문 없는 고정 패킷을 1회 직렬화해 재사용 가능한 프레임 배열로 만듭니다.</summary>
    /// <remarks><b>[Memory]</b> 타입당 배열 1개(정적 초기화 시 1회). <b>[Blocking]</b> Non-blocking.</remarks>
    private static byte[] CreateFrame<T>(T packet) where T : IPacket
    {
        // 정확한 크기의 배열: 정적 수명이라 ArrayPool 대여가 무의미하고(반납하지 않음),
        // Rent는 요청보다 큰 버킷 크기를 줄 수 있어 "배열 전체 = 프레임"이라는 단순한 불변식이 깨진다.
        var buffer = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        Serializer.Serialize(packet, buffer);
        return buffer;
    }

    /// <summary>대기 중 시한이 만료되면 <see cref="TimeoutException"/>으로 변환합니다.</summary>
    private static async Task ThrowIfDeadlineAsync(
        Task work, CancellationTokenSource deadlineCts, CancellationToken callerToken, TimeSpan timeout)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"경합 카운터 시나리오가 기한({timeout}) 안에 완료되지 않았습니다. 서버가 응답하지 않거나 처리가 지연되고 있습니다.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 연결 1개 = 워커 1개. 미완료 조회를 최대 1건으로 제한해 요청 ID 없이 응답을 매칭한다.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 시나리오가 사용하는 연결 1개를 감쌉니다. 증감 프레임 송신과 "미완료 1건" 조회 왕복을 제공합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety]</b> 워커 Task 1개가 단독으로 사용하는 것을 전제로 합니다(송신 순서가 곧 정확성이므로).
    /// 다만 <see cref="_pendingQuery"/>만은 IO 스레드(수신·연결 해제 콜백)와 워커 스레드가 함께 접근하므로
    /// <see cref="Interlocked"/>로 소유권을 옮깁니다.
    /// <br/><b>[Blocking]</b> 모든 멤버 Non-blocking.
    /// </remarks>
    private sealed class CounterConnection : IAsyncDisposable
    {
        // ServerNet.CreateClient(): SocketPipelineClient(internal)를 IClientConnection으로 반환.
        // 구현체가 숨겨져 있어 예제는 인터페이스만 다룬다(캡슐화). ConnectAsync 전까지 소켓이 만들어지지 않는다.
        private readonly IClientConnection _connection = ServerNet.CreateClient();

        // 현재 미완료 조회의 대기자. IO 스레드(OnReceived/OnDisconnected)와 워커 스레드가 동시에 접근하므로
        // 참조 자체를 Interlocked.Exchange/CompareExchange로 주고받아 "한 쪽만 완료시킨다"는 소유권을 보장한다.
        // TaskCompletionSource<T>는 TrySetResult/TrySetException이 스레드 안전하지만, 대기자를 '꺼내 가는' 행위까지
        // 원자적이어야 응답과 연결 해제가 동시에 도착했을 때 두 번 처리되지 않는다.
        private TaskCompletionSource<CounterSnapshot>? _pendingQuery;

        // BinaryPacketSerializer: 무상태 → 여러 연결이 공유해도 안전. 응답 역직렬화에만 쓴다.
        private static readonly BinaryPacketSerializer ResponseSerializer = new();

        /// <summary>서버에 연결하고 수신 콜백을 활성화합니다.</summary>
        /// <remarks><b>[Blocking]</b> Non-blocking(비동기 대기). 콜백은 <c>ConnectAsync</c> 전에 등록해야 합니다.</remarks>
        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            // OnReceived: IO 스레드에서 호출된다. data는 콜백 반환 후 무효가 되는 수신 버퍼 뷰이므로
            // 동기 구간에서 검증·역직렬화만 하고, 밖으로는 값 타입 스냅샷만 넘긴다.
            // 이 콜백에서 예외를 던지면 클라이언트 수신 루프가 죽어 실패 원인이 뒤섞이므로,
            // 프로토콜 위반은 예외를 '던지는' 대신 대기자에 '전달'한다.
            _connection.OnReceived = data =>
            {
                TaskCompletionSource<CounterSnapshot>? waiter = Interlocked.Exchange(ref _pendingQuery, null);
                if (waiter is null)
                    return ValueTask.CompletedTask; // 대기자가 없는 응답(중복·지연 도착)은 조용히 버린다

                ReadOnlySpan<byte> frame = data.Span; // zero-copy 뷰
                if (!PacketPool.TryParseHeader(frame, out ushort packetId, out int bodyLength)
                    || packetId != CounterValuePacket.Id
                    || bodyLength != CounterValuePacket.BodySize
                    || frame.Length != PacketPool.HeaderSize + CounterValuePacket.BodySize)
                {
                    waiter.TrySetException(new IOException(
                        $"조회 응답이 프로토콜에 맞지 않습니다: Id={(frame.Length >= PacketPool.HeaderSize ? packetId : -1)}, " +
                        $"본문 {(frame.Length >= PacketPool.HeaderSize ? bodyLength : -1)}B, 프레임 {frame.Length}B " +
                        $"(기대: Id={CounterValuePacket.Id}, 본문 {CounterValuePacket.BodySize}B)"));
                    return ValueTask.CompletedTask;
                }

                // Deserialize<CounterValuePacket>: struct라 new T()가 스택/인라인 생성 → 힙 할당 0.
                CounterValuePacket packet = ResponseSerializer.Deserialize<CounterValuePacket>(frame);
                waiter.TrySetResult(new CounterSnapshot(packet.Value, packet.AppliedOps));
                return ValueTask.CompletedTask;
            };

            // OnDisconnected: 조회 대기 중 연결이 끊기면 대기자를 즉시 실패시킨다.
            // 이 배선이 없으면 "서버 종료" 실패 경로가 전체 기한이 만료될 때까지 매달려 있게 되어,
            // 원인이 '연결 끊김'인지 '무응답'인지 구분되지 않고 실행 시간만 늘어난다.
            _connection.OnDisconnected = () =>
            {
                Interlocked.Exchange(ref _pendingQuery, null)
                    ?.TrySetException(new IOException("조회 응답을 기다리는 중에 서버 연결이 끊겼습니다."));
                return ValueTask.CompletedTask;
            };

            // ConnectAsync: TCP 3-way handshake + 수신 파이프라인 시작. 완료 후 IsConnected == true.
            await _connection.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>미리 직렬화된 프레임을 그대로 송신합니다(증감 hot loop 전용).</summary>
        /// <remarks>
        /// <b>[Memory]</b> 직렬화·버퍼 대여가 없습니다 — 정적 프레임 뷰를 넘길 뿐이라 송신당 할당이 0입니다
        /// (커널 송신 버퍼 포화로 비동기 전환될 때만 ServerLib 내부 상태머신이 생깁니다).
        /// <b>[Blocking]</b> Non-blocking.
        /// </remarks>
        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
            // IClientConnection.SendAsync(ReadOnlyMemory<byte>): _socket.SendAsync()로 커널 송신 버퍼에 직접 기록한다.
            // 커널 버퍼에 여유가 있으면 동기 완료(무할당), 포화 시에만 비동기 대기한다(TCP 흐름 제어).
            => _connection.SendAsync(frame, cancellationToken);

        /// <summary>조회를 1건 보내고 응답 스냅샷을 기다립니다. 연결당 미완료 조회는 1건으로 제한됩니다.</summary>
        /// <exception cref="InvalidOperationException">이전 조회가 아직 끝나지 않았을 때(요청 ID 없는 매칭 전제 위반).</exception>
        /// <exception cref="IOException">연결이 끊겼거나 응답이 프로토콜에 맞지 않을 때.</exception>
        /// <exception cref="OperationCanceledException">토큰이 취소됐을 때.</exception>
        /// <remarks>
        /// <b>[순서]</b> 대기자를 <b>송신 전에</b> 게시합니다. 송신 후에 게시하면 루프백처럼 빠른 경로에서
        /// 응답이 먼저 도착해 대기자를 놓칠 수 있습니다.
        /// <br/><b>[불변식 — 포기된 조회가 있는 연결은 재사용하지 않는다]</b> 응답 매칭은 요청 ID 없이
        /// "연결당 미완료 조회 1건"이라는 전제에만 의존합니다. 따라서 조회를 <b>in-flight 상태로 포기한</b>(취소·송신 실패로
        /// <c>catch</c>에서 대기자를 회수한) 연결을 다시 조회에 쓰면, 그 사이 도착한 이전 요청의 <b>지연 응답</b>이
        /// 새 대기자를 낡은 스냅샷으로 완료시킬 수 있습니다(요청 ID가 없어 지연 응답과 새 요청을 구분할 수 없음).
        /// <b>실패·취소로 조회를 포기한 연결은 반드시 <see cref="DisposeAsync"/>해야 하며 재사용하지 않습니다.</b>
        /// <c>RunAsync</c>는 이 불변식을 지킵니다 — 조회가 실패하면 곧바로 예외로 빠져나가 <c>finally</c>에서 연결을 폐기합니다.
        /// <br/><b>[Blocking]</b> Non-blocking. <b>[Memory]</b> 조회 1회당 TaskCompletionSource 1개.
        /// </remarks>
        public async Task<CounterSnapshot> QueryAsync(CancellationToken cancellationToken)
        {
            // RunContinuationsAsynchronously: TrySetResult를 호출하는 IO 스레드에서 대기자의 후속 코드가
            // 인라인 실행되면 수신 루프가 그만큼 점유된다. 비동기 재개로 IO 스레드를 즉시 놓아준다.
            var waiter = new TaskCompletionSource<CounterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            // CompareExchange(null → waiter): 미완료 조회가 이미 있으면 게시를 거부한다.
            if (Interlocked.CompareExchange(ref _pendingQuery, waiter, null) is not null)
                throw new InvalidOperationException("이 연결에는 이미 미완료 조회가 있습니다. 연결당 동시 조회는 1건입니다.");

            try
            {
                await _connection.SendAsync(QueryFrame, cancellationToken).ConfigureAwait(false);
                return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 송신 실패·취소로 빠져나갈 때 대기자를 회수한다. 회수하지 않으면 이후 도착한 응답이
                // 죽은 대기자를 완료시키고, 다음 QueryAsync가 "이미 미완료 조회가 있다"로 잘못 거부된다.
                // 값 비교로 교체 — 그 사이 IO 스레드가 이미 가져갔다면 건드리지 않는다(ABA 없이 소유권 존중).
                Interlocked.CompareExchange(ref _pendingQuery, null, waiter);
                throw;
            }
        }

        /// <summary>연결을 정리합니다. 대기 중인 조회가 있으면 실패시킵니다.</summary>
        /// <remarks><b>[Blocking]</b> Non-blocking. 소켓·수신 파이프라인 자원을 해제합니다.</remarks>
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _pendingQuery, null)
                ?.TrySetException(new IOException("조회 응답을 기다리는 중에 연결이 정리되었습니다."));
            // DisposeAsync: CTS 취소 + 소켓 폐기로 수신 루프에 종료를 알린다(await using과 동일 경로).
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
