using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.ObjectPool;

namespace ServerLib.Core.Memory;

// 헤더 파싱 결과를 재사용하여 헤더 파싱 할당을 제거한다.
public sealed class PacketHeader
{
    public ushort PacketId;
    public int BodyLength;

    public void Reset()
    {
        PacketId = 0;
        BodyLength = 0;
    }
}

public sealed class PacketHeaderPoolPolicy : IPooledObjectPolicy<PacketHeader>
{
    public PacketHeader Create() => new();
    public bool Return(PacketHeader obj)
    {
        obj.Reset();
        return true;
    }
}

public static class PacketPool
{
    public static readonly ObjectPool<PacketHeader> Headers =
        new DefaultObjectPool<PacketHeader>(new PacketHeaderPoolPolicy(), maximumRetained: 256);

    // 고정 크기 전송 버퍼 대여 (패킷 직렬화용)
    public static byte[] RentSendBuffer(int minimumSize) =>
        ArrayPool<byte>.Shared.Rent(minimumSize);

    public static void ReturnSendBuffer(byte[] buffer) =>
        ArrayPool<byte>.Shared.Return(buffer);

    // 패킷 헤더 파싱: 4바이트 [PacketId(2) | BodyLength(2)]
    public const int HeaderSize = 4;

    public static bool TryParseHeader(ReadOnlySpan<byte> data, out ushort packetId, out int bodyLength)
    {
        if (data.Length < HeaderSize)
        {
            packetId = 0;
            bodyLength = 0;
            return false;
        }
        packetId = BinaryPrimitives.ReadUInt16LittleEndian(data);
        bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2));
        return true;
    }

    // 헤더 기록: Span에 직접 쓰기 (Zero-copy)
    public static void WriteHeader(Span<byte> destination, ushort packetId, int bodyLength)
    {
        if ((uint)bodyLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bodyLength), bodyLength, "본문 길이는 0~65535 범위여야 합니다.");
        BinaryPrimitives.WriteUInt16LittleEndian(destination, packetId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), (ushort)bodyLength);
    }
}
