namespace ServerLib.Interface;

public interface IRpcHandler
{
    // Source Generator가 생성하는 디스패치 진입점
    ValueTask DispatchAsync(ISession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
