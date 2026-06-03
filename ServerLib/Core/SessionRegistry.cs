using System.Collections.Concurrent;
using System.Net.Sockets;
using ServerLib.Interface;

namespace ServerLib.Core;

/// <summary><see cref="ISessionRegistry"/>의 <see cref="ConcurrentDictionary{TKey,TValue}"/> 기반 구현체입니다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="ConcurrentDictionary{TKey,TValue}"/>로 락 경합 없이 동작합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="GetAll"/> 및 <see cref="BroadcastAsync"/> 호출 시 스냅샷 배열을 할당합니다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="BroadcastAsync"/>는 모든 병렬 전송 완료까지 비동기 대기합니다. 나머지 멤버는 즉시 반환합니다.</description></item>
/// </list>
/// </remarks>
public sealed class SessionRegistry : ISessionRegistry
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
        await Task.WhenAll(snapshot.Select(async s =>
        {
            try { await s.SendAsync(data, cancellationToken); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }));
    }

    /// <inheritdoc/>
    public void Register(ISession session)
        => _sessions[session.SessionId] = session;

    /// <inheritdoc/>
    public void Unregister(Guid sessionId)
        => _sessions.TryRemove(sessionId, out _);
}
