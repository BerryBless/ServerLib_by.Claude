using System.Buffers;
using ServerLib.Core.Memory;
using ServerLib.Interface;

namespace ServerLib.Core.Serialization;

/// <summary>
/// <see cref="IPacketSerializer"/> 구현체입니다.
/// 4바이트 고정 헤더(PacketId 2B + BodyLength 2B) + 본문 구조의 바이너리 직렬화를 수행합니다.
/// 헤더 파싱은 <see cref="PacketPool"/>을 재사용하며, 본문 인코딩은 <see cref="SpanWriter"/>/<see cref="SpanReader"/>로 처리합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 내부 상태를 변경하지 않으므로 여러 스레드에서 공유 가능합니다.
/// <b>[Memory Allocation:]</b> <see cref="Serialize{T}"/> / <see cref="TryReadPacketLength"/> Zero-allocation.
/// <see cref="Deserialize{T}"/> 는 <c>new T()</c>로 인해 T가 class인 경우 1회 힙 할당이 발생합니다.
/// </remarks>
public sealed class BinaryPacketSerializer : IPacketSerializer
{
    /// <summary>
    /// 패킷을 <paramref name="destination"/> 버퍼에 직렬화합니다.
    /// 앞 4바이트는 헤더(PacketId + BodyLength), 이후는 본문입니다.
    /// </summary>
    /// <typeparam name="T"><see cref="IPacket"/>을 구현한 패킷 타입입니다.</typeparam>
    /// <param name="packet">직렬화할 패킷 인스턴스입니다.</param>
    /// <param name="destination">직렬화 결과가 기록될 버퍼입니다. <c>PacketPool.HeaderSize + packet.GetBodySize()</c> 이상이어야 합니다.</param>
    /// <returns>기록된 전체 바이트 수(헤더 + 본문)입니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation.
    /// <see cref="ArrayPool{T}.Shared"/>로 대여한 버퍼를 전달하는 것을 권장합니다.
    /// </remarks>
    public int Serialize<T>(T packet, Span<byte> destination) where T : IPacket
    {
        int bodySize = packet.GetBodySize();
        PacketPool.WriteHeader(destination, packet.PacketId, bodySize);
        var writer = new SpanWriter(destination.Slice(PacketPool.HeaderSize, bodySize));
        packet.Serialize(ref writer);
        return PacketPool.HeaderSize + bodySize;
    }

    /// <summary>
    /// <paramref name="source"/> 버퍼에서 패킷을 역직렬화합니다.
    /// <paramref name="source"/>는 헤더를 포함한 전체 패킷이어야 합니다.
    /// </summary>
    /// <typeparam name="T"><see cref="IPacket"/>을 구현하고 기본 생성자를 가진 패킷 타입입니다.</typeparam>
    /// <param name="source">헤더 + 본문으로 구성된 전체 패킷 버퍼입니다.</param>
    /// <returns>역직렬화된 패킷 인스턴스입니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> T가 class인 경우 <c>new T()</c>로 1회 힙 할당이 발생합니다.
    /// T를 struct로 정의하면 Zero-allocation이 가능합니다.
    /// </remarks>
    public T Deserialize<T>(ReadOnlySpan<byte> source) where T : IPacket, new()
    {
        var reader = new SpanReader(source.Slice(PacketPool.HeaderSize));
        var packet = new T();
        packet.Deserialize(ref reader);
        return packet;
    }

    /// <summary>
    /// 패킷 헤더를 파싱하여 전체 패킷 길이를 반환합니다.
    /// 부분 수신(partial read) 시 완전한 패킷이 도착했는지 판단하는 데 사용합니다.
    /// </summary>
    /// <param name="header">파싱할 헤더 바이트입니다. <see cref="PacketPool.HeaderSize"/> 이상이어야 합니다.</param>
    /// <param name="totalLength">성공 시 헤더를 포함한 전체 패킷 바이트 수입니다.</param>
    /// <returns>파싱 성공 여부입니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation.
    /// </remarks>
    public bool TryReadPacketLength(ReadOnlySpan<byte> header, out int totalLength)
    {
        if (!PacketPool.TryParseHeader(header, out _, out int bodyLength))
        {
            totalLength = 0;
            return false;
        }
        totalLength = PacketPool.HeaderSize + bodyLength;
        return true;
    }
}
