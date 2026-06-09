using ServerLib.Interface;

namespace ServerLib.Core.Rpc;

// Source Generator가 생성하는 핸들러를 등록하고 패킷 ID로 디스패치한다.
// 실제 핸들러 구현은 Rpc.Generator가 [RpcService] 어트리뷰트를 읽어 자동 생성한다.
public sealed class RpcDispatcher : IRpcHandler
{
    // PacketId → 핸들러 매핑: Dictionary 대신 배열 인덱싱으로 O(1) 룩업
    private readonly Func<ISession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>?[] _handlers;

    public RpcDispatcher(int maxPacketId = 256)
    {
        _handlers = new Func<ISession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>?[maxPacketId];
    }

    public void Register(ushort packetId,
        Func<ISession, ReadOnlyMemory<byte>, CancellationToken, ValueTask> handler)
    {
        // 등록 시점(서버 설정) 가드: 범위 밖 ID는 IndexOutOfRange 대신 의미가 명확한 ArgumentOutOfRange로 알린다.
        // (등록은 서버 측 프로그래밍/설정 오류이므로 throw가 적절. 공격 입력 경로는 DispatchAsync 쪽 가드가 담당)
        if (packetId >= _handlers.Length)
            throw new ArgumentOutOfRangeException(nameof(packetId), packetId,
                $"packetId가 최대 {_handlers.Length - 1}을 초과했습니다. RpcDispatcher(maxPacketId)를 늘리세요.");
        _handlers[packetId] = handler;
    }

    public async ValueTask DispatchAsync(ISession session, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length < 2) return;

        var packetId = (ushort)(payload.Span[0] | (payload.Span[1] << 8));
        // 악성 입력 가드(A1): packetId는 ushort(0~65535)지만 _handlers는 maxPacketId 칸뿐이다.
        // 범위 밖 ID 패킷 하나로 _handlers[packetId]가 IndexOutOfRangeException을 던져 호출 루프를 죽이는 것을 막는다.
        // 미등록 ID와 동일하게 조용히 무시한다.
        if (packetId >= _handlers.Length) return;
        var handler = _handlers[packetId];
        if (handler == null) return;

        await handler(session, payload.Slice(2), cancellationToken);
    }
}
