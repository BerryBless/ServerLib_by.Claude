using CounterServer;
using Xunit;

namespace CounterExample.Tests;

/// <summary>
/// 실제 <see cref="CounterState"/>를 여러 작업이 동시에 갱신했을 때
/// <c>Value</c>와 <c>AppliedOps</c>가 결정적 기대값과 일치하는지 검증합니다(경합 정확성).
/// </summary>
/// <remarks>
/// <b>[이 테스트가 실증하는 것]</b> <see cref="Interlocked"/> 기반 갱신은 갱신 유실(lost update)을 만들지 않습니다.
/// 만약 구현이 <c>_value++</c>(읽기→더하기→쓰기 3단계, 비원자)로 퇴행하면 이 테스트들은
/// 기대값보다 <b>작은</b> 값으로 실패합니다.
/// <br/><br/>
/// <b>[상쇄 은폐 방지]</b> 더하기와 빼기가 같은 수면 <c>Value</c>가 0이 되는데, 이는 "정확히 상쇄됨"과
/// "아무것도 처리되지 않음"을 구분하지 못합니다. 그래서 <b>모든</b> 테스트가 <c>AppliedOps</c>를 함께 단언합니다.
/// <br/><br/>
/// <b>[정지 상태 단언]</b> <c>Value</c>·<c>AppliedOps</c> 쌍은 원자적으로 읽히지 않으므로,
/// 두 값을 함께 단언하는 것은 <b>모든 갱신 작업이 완료된 뒤</b>에만 유효합니다.
/// 각 테스트는 <c>Task.WhenAll</c>로 전 작업 완료를 확인한 다음에만 검증합니다.
/// </remarks>
public class CounterStateTests
{
    // ── 부하 규모 상수 ────────────────────────────────────────────────────────
    // 작업 수를 CPU 코어 수보다 크게 잡아 실제 선점·인터리빙이 일어나게 한다(경합 재현).
    private const int TaskCount = 8;
    private const int IncrementsPerTask = 10_000;
    private const int DecrementsPerTask = 7_500;

    /// <summary>새로 만든 상태는 <c>(0, 0)</c>이어야 합니다.</summary>
    [Fact]
    public void NewState_StartsAtZero()
    {
        var state = new CounterState();

        Assert.Equal(0L, state.Value);
        Assert.Equal(0L, state.AppliedOps);
    }

    /// <summary>단일 스레드에서 증감이 값과 적용 연산 수에 각각 어떻게 반영되는지 확인합니다.</summary>
    /// <remarks>빼기여도 <c>AppliedOps</c>는 <b>증가</b>합니다(수행한 연산 개수를 세는 값이므로).</remarks>
    [Fact]
    public void IncrementAndDecrement_UpdateValueAndAppliedOpsIndependently()
    {
        var state = new CounterState();

        Assert.Equal(1L, state.Increment());
        Assert.Equal(2L, state.Increment());
        Assert.Equal(1L, state.Decrement());

        Assert.Equal(1L, state.Value);
        Assert.Equal(3L, state.AppliedOps); // 더하기 2 + 빼기 1 = 연산 3회
    }

    /// <summary>여러 작업이 동시에 더하기만 했을 때 증가분이 하나도 유실되지 않는지 검증합니다.</summary>
    [Fact]
    public async Task ConcurrentIncrements_LoseNoUpdates()
    {
        // checked: 규모 상수를 키웠을 때 기대값이 조용히 오버플로해 "틀린 기대값과 일치"하는 오검증을 막는다.
        long expectedValue = checked((long)TaskCount * IncrementsPerTask);
        long expectedOps = expectedValue;

        var state = new CounterState();
        await RunConcurrentlyAsync(state, IncrementsPerTask, decrements: 0);

        Assert.Equal(expectedValue, state.Value);
        Assert.Equal(expectedOps, state.AppliedOps);
    }

    /// <summary>여러 작업이 동시에 빼기만 했을 때 감소분이 하나도 유실되지 않는지 검증합니다.</summary>
    [Fact]
    public async Task ConcurrentDecrements_LoseNoUpdates()
    {
        long expectedValue = checked(-((long)TaskCount * DecrementsPerTask));
        long expectedOps = checked((long)TaskCount * DecrementsPerTask);

        var state = new CounterState();
        await RunConcurrentlyAsync(state, increments: 0, decrements: DecrementsPerTask);

        Assert.Equal(expectedValue, state.Value);
        Assert.Equal(expectedOps, state.AppliedOps);
    }

    /// <summary>더하기·빼기 수가 다른 혼합 부하에서 순(net) 결과와 연산 수가 모두 정확한지 검증합니다.</summary>
    /// <remarks>불균형(1만 vs 7,500)으로 두어, 상쇄로 결함이 가려지지 않게 합니다.</remarks>
    [Fact]
    public async Task ConcurrentMixedUnbalanced_ProducesDeterministicNetResult()
    {
        long expectedValue = checked((long)TaskCount * (IncrementsPerTask - DecrementsPerTask));
        long expectedOps = checked((long)TaskCount * (IncrementsPerTask + DecrementsPerTask));

        var state = new CounterState();
        await RunConcurrentlyAsync(state, IncrementsPerTask, DecrementsPerTask);

        Assert.Equal(expectedValue, state.Value);
        Assert.Equal(expectedOps, state.AppliedOps);
    }

    /// <summary>
    /// 더하기와 빼기 수가 같아 순 결과가 0인 부하에서, <c>AppliedOps</c>가 "정말 처리됐음"을 증명하는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// <c>Value == 0</c> 하나만 보면 갱신이 통째로 유실돼도 통과합니다. <c>AppliedOps</c> 단언이 그 은폐를 깹니다.
    /// </remarks>
    [Fact]
    public async Task ConcurrentBalanced_NetsToZeroButRecordsEveryOperation()
    {
        long expectedOps = checked((long)TaskCount * (IncrementsPerTask + IncrementsPerTask));

        var state = new CounterState();
        await RunConcurrentlyAsync(state, IncrementsPerTask, decrements: IncrementsPerTask);

        Assert.Equal(0L, state.Value);
        Assert.Equal(expectedOps, state.AppliedOps);
    }

    /// <summary>
    /// <see cref="TaskCount"/>개 작업이 공통 시작 신호에 맞춰 동시에 출발해 증감을 뒤섞어 적용합니다.
    /// </summary>
    /// <param name="state">갱신 대상 상태입니다.</param>
    /// <param name="increments">작업 하나가 수행할 더하기 횟수입니다.</param>
    /// <param name="decrements">작업 하나가 수행할 빼기 횟수입니다.</param>
    /// <remarks>
    /// <b>[정지 상태 보장]</b> 반환 시점에는 모든 작업이 완료돼 있으므로, 호출자는 두 값을 함께 단언해도 됩니다.
    /// </remarks>
    private static async Task RunConcurrentlyAsync(CounterState state, int increments, int decrements)
    {
        // TaskCompletionSource<bool>: 모든 작업이 하나의 Task를 await하다 동시에 풀려나는 출발 게이트.
        // RunContinuationsAsynchronously: TrySetResult 호출 스레드에서 N개 작업이 인라인 직렬 실행되는 것을 막아
        //   실제로 스레드 풀에 흩어져 병렬 출발하게 한다(이 옵션이 없으면 사실상 순차 실행이라 경합이 재현되지 않는다).
        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[TaskCount];

        // bool[] schedule: true=빼기. 생성 후 절대 변경하지 않으므로 모든 작업이 락 없이 동시에 읽어도 안전(불변 공유).
        // Bresenham 방식으로 빼기를 전 구간에 균등 분산해 정확히 decrements개를 만든다.
        bool[] schedule = BuildSchedule(increments, decrements);

        for (int i = 0; i < workers.Length; i++)
        {
            int index = i;
            // Task.Run: 스레드 풀 스레드에서 실행해 작업들이 서로 다른 코어에 분산되도록 한다.
            workers[i] = Task.Run(async () =>
            {
                await startSignal.Task.ConfigureAwait(false);

                // 더하기·빼기를 뒤섞어 적용해 같은 캐시라인에 대한 읽기-수정-쓰기가 실제로 겹치게 한다.
                // 앞뒤로 몰아 실행하면 구간별로 단조 증가/감소만 하여 인터리빙이 약해진다.
                //
                // schedule은 불변 공유 배열이고, 작업마다 읽기 시작 위치만 회전시킨다.
                // 회전은 항목 수를 바꾸지 않으므로 작업별 더하기·빼기 횟수는 정확히 보존된다(기대값의 전제).
                int steps = schedule.Length;
                int offset = steps == 0 ? 0 : (int)((long)index * steps / TaskCount);

                for (int k = 0; k < steps; k++)
                {
                    int slot = offset + k;
                    if (slot >= steps) slot -= steps; // 나머지 연산 대신 뺄셈 — hot loop의 div 회피
                    if (schedule[slot]) state.Decrement();
                    else state.Increment();
                }
            });
        }

        startSignal.TrySetResult(true);

        // Task.WhenAll: 모든 갱신이 끝난 정지 상태를 만든다. 개별 작업 예외도 여기서 관측된다.
        await Task.WhenAll(workers);
    }

    /// <summary>
    /// 증감 순서 스케줄을 만듭니다. <see langword="true"/>는 빼기, <see langword="false"/>는 더하기이며
    /// <see langword="true"/>의 개수는 정확히 <paramref name="decrements"/>입니다.
    /// </summary>
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
}
