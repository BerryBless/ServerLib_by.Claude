using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Interface;

namespace ServerLib.Core.Serialization;

/// <summary>
/// <see cref="IPacket"/>를 직접 송신하는 편의 확장 메서드입니다. 버퍼 대여·직렬화·반납을 캡슐화하여
/// 호출자가 <see cref="ArrayPool{T}"/> 관리를 직접 하지 않도록 합니다.
/// </summary>
/// <remarks>
/// <b>[레이어]</b> 직렬화 인지(知) 헬퍼이므로 <c>Core.Serialization</c>에 둔다. 인터페이스 멤버가 아닌 확장 메서드라
/// 기존 <see cref="ISession"/>/<see cref="IClientConnection"/> 구현체를 깨지 않으며, 의존성 방향(Core→Interface)도 지킨다.
/// <br/><b>[Memory Allocation]</b> 송신이 동기 완료하면 Zero-allocation(대여 버퍼 즉시 반납). 비동기 suspend 시에만
/// 상태머신 1개를 할당한다. 1000회/초 규모의 hot loop라면 패킷을 1회 직렬화해 재사용하는
/// <see cref="ISession.SendAsync(ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/> 직접 호출이 여전히 유리하다.
/// </remarks>
public static class PacketSendExtensions
{
    // 무상태 직렬화기 공유 (내부 상태 없음 → thread-safe). HeartbeatProtocol과 동일 패턴.
    private static readonly BinaryPacketSerializer Serializer = new();

    /// <summary><paramref name="packet"/>을 직렬화하여 세션으로 송신합니다. 내부에서 풀 버퍼를 대여·반납합니다.</summary>
    /// <typeparam name="T"><see cref="IPacket"/> 구현 타입. struct면 직렬화 경로도 무할당.</typeparam>
    /// <param name="session">송신 대상 세션.</param>
    /// <param name="packet">송신할 패킷.</param>
    /// <param name="cancellationToken">송신 취소 토큰.</param>
    /// <returns>송신 완료 시 완료되는 <see cref="ValueTask"/>.</returns>
    /// <remarks>
    /// <b>[Thread Safety]</b> 기반 <see cref="ISession.SendAsync(ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>와 동일(Thread-safe).
    /// <b>[Blocking]</b> Non-blocking. <b>[Memory]</b> 동기 완료 시 무할당, 비동기 시 상태머신 1개.
    /// </remarks>
    public static ValueTask SendAsync<T>(this ISession session, T packet, CancellationToken cancellationToken = default)
        where T : IPacket
    {
        // ArrayPool<byte>.Rent: 헤더+본문 직렬화 버퍼를 풀에서 대여(송신마다 new byte[] 회피). 송신 완료 후 반드시 반납.
        var buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + packet.GetBodySize());
        ValueTask sendOp;
        try
        {
            int written = Serializer.Serialize(packet, buffer);
            sendOp = session.SendAsync(buffer.AsMemory(0, written), cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer); // 직렬화/동기 송신 throw 시 버퍼 누수 방지
            throw;
        }
        return CompleteAsync(sendOp, buffer);
    }

    /// <summary><paramref name="packet"/>을 직렬화하여 서버로 송신합니다. 내부에서 풀 버퍼를 대여·반납합니다.</summary>
    /// <typeparam name="T"><see cref="IPacket"/> 구현 타입.</typeparam>
    /// <param name="connection">송신 대상 클라이언트 연결.</param>
    /// <param name="packet">송신할 패킷.</param>
    /// <param name="cancellationToken">송신 취소 토큰.</param>
    /// <returns>송신 완료 시 완료되는 <see cref="ValueTask"/>.</returns>
    public static ValueTask SendAsync<T>(this IClientConnection connection, T packet, CancellationToken cancellationToken = default)
        where T : IPacket
    {
        // ArrayPool<byte>.Rent: 위와 동일 — 송신 버퍼 풀 대여로 무할당 경로 확보.
        var buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + packet.GetBodySize());
        ValueTask sendOp;
        try
        {
            int written = Serializer.Serialize(packet, buffer);
            sendOp = connection.SendAsync(buffer.AsMemory(0, written), cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        return CompleteAsync(sendOp, buffer);
    }

    // 송신 ValueTask가 동기 성공 완료면 즉시 반납(무할당), 아니면 await 후 반납(상태머신 1개).
    private static ValueTask CompleteAsync(ValueTask sendOp, byte[] buffer)
    {
        if (sendOp.IsCompletedSuccessfully)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return ValueTask.CompletedTask;
        }
        return AwaitAndReturnAsync(sendOp, buffer);
    }

    private static async ValueTask AwaitAndReturnAsync(ValueTask sendOp, byte[] buffer)
    {
        try { await sendOp.ConfigureAwait(false); }
        finally { ArrayPool<byte>.Shared.Return(buffer); } // 비동기 완료/실패 모두 반납 보장
    }
}
