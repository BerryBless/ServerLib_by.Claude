using System.Buffers.Binary;
using Xunit;
using ServerLib.Core.Serialization;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Tests;

/// <summary>
/// 11종 패킷 타입의 직렬화 → 역직렬화 왕복(round-trip) 필드 동등성을 검증하는 단위 테스트.
/// </summary>
public class PacketRoundTripTests
{
    private static BinaryPacketSerializer Serializer => new BinaryPacketSerializer();

    [Fact]
    public void EchoPacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new EchoPacket { Message = "Hello World" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<EchoPacket>(buf);

        Assert.Equal("Hello World", p2.Message);
    }

    [Fact]
    public void ChatPacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new ChatPacket { Sender = "Alice", Content = "Hi Bob" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<ChatPacket>(buf);

        Assert.Equal("Alice", p2.Sender);
        Assert.Equal("Hi Bob", p2.Content);
    }

    [Fact]
    public void IncrementPacket_roundtrip()
    {
        // struct, 본문 없음 — 헤더 4바이트만 직렬화되었는지 와이어 수준에서 검증
        var serializer = Serializer;
        var packet = new IncrementPacket();
        // bodySize=0이므로 버퍼는 헤더 4바이트만
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        // 헤더 직접 검증: 직렬화기가 망가져도 PacketId 상수는 항상 3이므로
        // 버퍼의 와이어 바이트를 확인해야 실질적인 역직렬화 경로를 보장한다.
        Assert.Equal(PacketPool.HeaderSize, buf.Length);                                     // 본문 없이 헤더만
        Assert.Equal((ushort)IncrementPacket.Id, BinaryPrimitives.ReadUInt16LittleEndian(buf));    // PacketId LE
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2)));    // BodyLength=0

        var p2 = serializer.Deserialize<IncrementPacket>(buf);
        Assert.Equal(IncrementPacket.Id, p2.PacketId);
        Assert.Equal(0, p2.GetBodySize());
    }

    [Fact]
    public void DecrementPacket_roundtrip()
    {
        // struct, 본문 없음 — 헤더 4바이트만 직렬화되었는지 와이어 수준에서 검증
        var serializer = Serializer;
        var packet = new DecrementPacket();
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        // 헤더 직접 검증
        Assert.Equal(PacketPool.HeaderSize, buf.Length);
        Assert.Equal((ushort)DecrementPacket.Id, BinaryPrimitives.ReadUInt16LittleEndian(buf));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2)));

        var p2 = serializer.Deserialize<DecrementPacket>(buf);
        Assert.Equal(DecrementPacket.Id, p2.PacketId);
        Assert.Equal(0, p2.GetBodySize());
    }

    [Fact]
    public void DamagePacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new DamagePacket { Amount = 9999 };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<DamagePacket>(buf);

        Assert.Equal(9999, p2.Amount);
    }

    [Fact]
    public void MobHpPacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new MobHpPacket { Hp = 50000L, MaxHp = 100000L, Generation = 3 };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<MobHpPacket>(buf);

        Assert.Equal(50000L, p2.Hp);
        Assert.Equal(100000L, p2.MaxHp);
        Assert.Equal(3, p2.Generation);
    }

    [Fact]
    public void MobDeathPacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new MobDeathPacket
        {
            Generation = 2,
            TopDamage = 12345L,
            MvpName = "Player1"
        };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<MobDeathPacket>(buf);

        Assert.Equal(2, p2.Generation);
        Assert.Equal(12345L, p2.TopDamage);
        Assert.Equal("Player1", p2.MvpName);
    }

    [Fact]
    public void StatsRequestPacket_roundtrip()
    {
        // struct, 본문 없음 — 헤더 4바이트만 직렬화되었는지 와이어 수준에서 검증
        var serializer = Serializer;
        var packet = new StatsRequestPacket();
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        // 헤더 직접 검증
        Assert.Equal(PacketPool.HeaderSize, buf.Length);
        Assert.Equal((ushort)StatsRequestPacket.Id, BinaryPrimitives.ReadUInt16LittleEndian(buf));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2)));

        var p2 = serializer.Deserialize<StatsRequestPacket>(buf);
        Assert.Equal(StatsRequestPacket.Id, p2.PacketId);
        Assert.Equal(0, p2.GetBodySize());
    }

    [Fact]
    public void StatsResponsePacket_roundtrip_byte_buffers_equal()
    {
        // Json은 write-only이므로 필드 비교 불가 — 버퍼 바이트 동등성으로 왕복 검증
        var serializer = Serializer;
        var p1 = new StatsResponsePacket { Json = "{\"hp\":50000}" };
        byte[] buf1 = new byte[PacketPool.HeaderSize + p1.GetBodySize()];
        serializer.Serialize(p1, buf1);

        // 역직렬화 후 재직렬화한 버퍼가 원본과 동일해야 한다
        var p2 = serializer.Deserialize<StatsResponsePacket>(buf1);
        byte[] buf2 = new byte[PacketPool.HeaderSize + p2.GetBodySize()];
        serializer.Serialize(p2, buf2);

        Assert.Equal<byte>(buf1, buf2);
    }

    [Fact]
    public void PingPacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new PingPacket { ClientTicks = 123456789L };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<PingPacket>(buf);

        Assert.Equal(123456789L, p2.ClientTicks);
    }

    [Fact]
    public void PongPacket_roundtrip()
    {
        var serializer = Serializer;
        var packet = new PongPacket { ClientTicks = 987654321L };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<PongPacket>(buf);

        Assert.Equal(987654321L, p2.ClientTicks);
    }
}
