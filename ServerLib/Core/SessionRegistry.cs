using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core;

/// <summary><see cref="ISessionRegistry"/> 및 <see cref="ISessionRegistrar"/>의 <see cref="ConcurrentDictionary{TKey,TValue}"/> 기반 구현체입니다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="ConcurrentDictionary{TKey,TValue}"/>로 락 경합 없이 동작합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="GetAll"/> 및 <see cref="BroadcastAsync"/> 호출 시 스냅샷 배열을 할당합니다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="BroadcastAsync"/>는 모든 병렬 전송 완료까지 비동기 대기합니다. 나머지 멤버는 즉시 반환합니다.</description></item>
/// </list>
/// </remarks>
// 구현 은닉(internal): 외부 소비자는 ServerNet.CreateSessionRegistry()가 반환하는 ISessionRegistry로만 사용한다.
internal sealed class SessionRegistry : ISessionRegistry, ISessionRegistrar
{
    // ConcurrentDictionary: 내부 버킷을 다중 락 스트라이프로 분할 → Register/Unregister(쓰기)와 조회(읽기)가 락 경합 없이 병행.
    // TryGet/Count는 락-프리 읽기 경로라 hot path에서도 저비용.
    private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();

    /// <inheritdoc/>
    public int Count => _sessions.Count;

    /// <inheritdoc/>
    public bool TryGet(Guid sessionId, out ISession? session)
        => _sessions.TryGetValue(sessionId, out session);

    /// <inheritdoc/>
    public IReadOnlyCollection<ISession> GetAll()
        // .ToArray() = 참조 스냅샷 깊은복사(요소 ISession 자체가 아닌 참조들을 새 배열로 복제).
        // 열거 중 컬렉션이 바뀌어도 안전한 시점 일관성을 주지만 호출마다 배열 Alloc → hot path 반복 호출 금지.
        => _sessions.Values.ToArray();

    /// <inheritdoc/>
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // P5: 대상 세션 스냅샷을 풀 버퍼로 수집. _sessions.Values.ToArray()는 내부 List + 배열 2회(O(N)) 할당에 더해
        // 모든 버킷 락을 잡지만, ArrayPool 임대 + lock-free foreach는 O(N) 힙 할당을 없애고 락도 잡지 않는다.
        // (ConcurrentDictionary 열거자 1개 할당만 남음 — 이 컬렉션 고유의 불가피한 비용.)
        int count = _sessions.Count; // 근사값(동시 변경 가능) — 아래 오버플로 가드로 보정
        // ArrayPool<ISession>.Rent: 세션 참조 스냅샷용 임시 배열을 풀에서 대여 → 브로드캐스트마다 new ISession[] 회피. 최소 4칸 임대로 길이-0 성장 엣지 회피.
        var snapshot = ArrayPool<ISession>.Shared.Rent(Math.Max(count, 4));
        int n = 0;
        try
        {
            // foreach: ConcurrentDictionary의 lock-free 열거 — Values 프로퍼티와 달리 버킷 락을 잡지 않는다.
            foreach (var kvp in _sessions)
            {
                if (n == snapshot.Length) // 임대 후 세션이 늘어 버퍼 초과 → 더 큰 풀 버퍼로 교체(드묾)
                {
                    var bigger = ArrayPool<ISession>.Shared.Rent(snapshot.Length * 2);
                    Array.Copy(snapshot, bigger, n);
                    Array.Clear(snapshot, 0, n);
                    ArrayPool<ISession>.Shared.Return(snapshot);
                    snapshot = bigger;
                }
                snapshot[n++] = kvp.Value;
            }
            if (n == 0) return;

            // ArrayPool<ValueTask>.Rent: 진행 중 ValueTask들을 담을 임시 배열을 풀에서 대여 — 브로드캐스트마다 new ValueTask[]를 피해 Gen0 회피.
            var sends = ArrayPool<ValueTask>.Shared.Rent(n);
            try
            {
                // 모든 세션에 전송 시작 (async 람다 없음 — 클로저/상태머신 할당 제거)
                for (int i = 0; i < n; i++)
                {
                    try { sends[i] = snapshot[i].SendAsync(data, cancellationToken); }
                    catch (ObjectDisposedException) { sends[i] = ValueTask.CompletedTask; }
                    catch (SocketException) { sends[i] = ValueTask.CompletedTask; }
                }
                // 모든 전송이 이미 시작된 후 완료 대기 (병렬 전송 유지)
                // OperationCanceledException은 의도적으로 전파 — 호출자의 명시적 취소 요청
                for (int i = 0; i < n; i++)
                {
                    try { await sends[i].ConfigureAwait(false); }
                    catch (ObjectDisposedException) { }
                    catch (SocketException) { }
                }
            }
            finally
            {
                // Array.Clear: 풀 배열에 남은 ValueTask(내부 IValueTaskSource 참조)를 비워 반납 — 미정리 시 다음 대여자가 죽은 참조를 잡아
                // 객체가 GC되지 못하는 누수가 생긴다(풀은 배열을 zero-fill하지 않으므로 수동 정리 필수).
                Array.Clear(sends, 0, n);
                ArrayPool<ValueTask>.Shared.Return(sends);
            }
        }
        finally
        {
            // 세션 참조 스냅샷도 비우고 반납 — ISession 참조 잔류 시 풀을 통해 객체가 GC되지 못하는 누수 방지.
            Array.Clear(snapshot, 0, n);
            ArrayPool<ISession>.Shared.Return(snapshot);
        }
    }

    /// <inheritdoc/>
    public void Register(ISession session)
        => _sessions[session.SessionId] = session;

    /// <inheritdoc/>
    public void Unregister(Guid sessionId)
        => _sessions.TryRemove(sessionId, out _);
}
