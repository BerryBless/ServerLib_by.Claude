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
public sealed class SessionRegistry : ISessionRegistry, ISessionRegistrar
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
        var snapshot = _sessions.Values.ToArray(); // 참조 스냅샷 깊은복사 — 전송 중 컬렉션 변경과 무관한 고정 대상 목록 확보
        if (snapshot.Length == 0) return;

        // ArrayPool<ValueTask>.Rent: 진행 중 ValueTask들을 담을 임시 배열을 풀에서 대여 — 브로드캐스트마다 new ValueTask[]를 피해 Gen0 회피.
        var sends = ArrayPool<ValueTask>.Shared.Rent(snapshot.Length);
        try
        {
            // 모든 세션에 전송 시작 (async 람다 없음 — 클로저/상태머신 할당 제거)
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { sends[i] = snapshot[i].SendAsync(data, cancellationToken); }
                catch (ObjectDisposedException) { sends[i] = ValueTask.CompletedTask; }
                catch (SocketException) { sends[i] = ValueTask.CompletedTask; }
            }
            // 모든 전송이 이미 시작된 후 완료 대기 (병렬 전송 유지)
            // OperationCanceledException은 의도적으로 전파 — 호출자의 명시적 취소 요청
            for (int i = 0; i < snapshot.Length; i++)
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
            Array.Clear(sends, 0, snapshot.Length);
            ArrayPool<ValueTask>.Shared.Return(sends);
        }
    }

    /// <inheritdoc/>
    public void Register(ISession session)
        => _sessions[session.SessionId] = session;

    /// <inheritdoc/>
    public void Unregister(Guid sessionId)
        => _sessions.TryRemove(sessionId, out _);
}
