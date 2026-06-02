using ServerLib.Interface;

namespace ServerLib.Core.Rpc;

// Source Generator가 생성하는 핸들러를 등록하고 패킷 ID로 디스패치한다.
// 실제 핸들러 구현은 Rpc.Generator가 [RpcService] 어트리뷰트를 읽어 자동 생성한다.
public sealed class RpcDispatcher : IRpcHandler
{
    // PacketId → 핸들러 매핑: Dictionary 대신 배열 인덱싱으로 O(1) 룩업
    private readonly Func<ISession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>?[] _handlers;

    public RpcDispatcher(int maxPacketId = 65536)
    {
        _handlers = new Func<ISession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>?[maxPacketId];
    }

    public void Register(ushort packetId,
        Func<ISession, ReadOnlyMemory<byte>, CancellationToken, ValueTask> handler)
    {
        _handlers[packetId] = handler;
    }

    public async ValueTask DispatchAsync(ISession session, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length < 2) return;

        var packetId = (ushort)(payload.Span[0] | (payload.Span[1] << 8));
        var handler = _handlers[packetId];
        if (handler == null) return;

        await handler(session, payload.Slice(2), cancellationToken);
    }
}
