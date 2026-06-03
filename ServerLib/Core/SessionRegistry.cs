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
    private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();

    /// <inheritdoc/>
    public int Count => _sessions.Count;

    /// <inheritdoc/>
    public bool TryGet(Guid sessionId, out ISession? session)
        => _sessions.TryGetValue(sessionId, out session);

    /// <inheritdoc/>
    public IReadOnlyCollection<ISession> GetAll()
        => _sessions.Values.ToArray();

    /// <inheritdoc/>
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var snapshot = _sessions.Values.ToArray();
        if (snapshot.Length == 0) return;

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
            Array.Clear(sends, 0, snapshot.Length); // IValueTaskSource 참조 해제
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
