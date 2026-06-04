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
    // ObjectPool: PacketHeader(참조 타입)를 최대 256개까지 재사용 풀에 보관 → 수신 패킷마다 new PacketHeader()를 하지 않아
    // 헤더 파싱 경로의 Gen0 할당을 제거(GC 회피). Return 시 Reset으로 상태를 초기화해 재사용 안전성을 보장한다.
    public static readonly ObjectPool<PacketHeader> Headers =
        new DefaultObjectPool<PacketHeader>(new PacketHeaderPoolPolicy(), maximumRetained: 256);

    // ArrayPool<byte>.Shared.Rent: 버킷 단위 풀에서 TLS 슬롯을 우선 확인 → 같은 스레드에서 Rent/Return 시 힙 할당 없이 버퍼 재사용.
    // 직렬화마다 new byte[]를 하면 Gen0 압력이 누적되므로 풀 대여로 회피한다(반드시 ReturnSendBuffer로 반납해야 효과).
    public static byte[] RentSendBuffer(int minimumSize) =>
        ArrayPool<byte>.Shared.Rent(minimumSize);

    public static void ReturnSendBuffer(byte[] buffer) =>
        ArrayPool<byte>.Shared.Return(buffer); // 풀에 반납 — 미반납 시 풀이 고갈되어 매번 새 할당으로 퇴화

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
        // BinaryPrimitives.Read*: lvalue 메모리(data)를 복사 없이 in-place 해석해 값(rvalue)만 추출. Slice(2)도 얕은 뷰라 무할당.
        packetId = BinaryPrimitives.ReadUInt16LittleEndian(data);
        bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2));
        return true;
    }

    // 헤더 기록: Span에 직접 쓰기 (Zero-copy)
    public static void WriteHeader(Span<byte> destination, ushort packetId, int bodyLength)
    {
        if ((uint)bodyLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bodyLength), bodyLength, "본문 길이는 0~65535 범위여야 합니다.");
        // BinaryPrimitives.Write*: rvalue(packetId/bodyLength)를 lvalue 메모리(destination)에 직접 기록 — 중간 버퍼 없이 Zero-copy.
        BinaryPrimitives.WriteUInt16LittleEndian(destination, packetId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), (ushort)bodyLength);
    }
}
