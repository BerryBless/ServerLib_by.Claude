using Xunit;
using ServerLib.Core.Serialization;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Tests;

/// <summary>
/// BinaryPacketSerializer의 헤더 레이아웃, 반환값, 예외 경계를 검증하는 단위 테스트.
/// </summary>
public class BinaryPacketSerializerTests
{
    [Fact]
    public void Serialize_writes_correct_4byte_header_layout()
    {
        // EchoPacket(Message="Hi") 직렬화 후 헤더 바이트 레이아웃 검증
        var serializer = new BinaryPacketSerializer();
        var packet = new EchoPacket { Message = "Hi" };
        int bodySize = packet.GetBodySize();
        byte[] buf = new byte[PacketPool.HeaderSize + bodySize];

        serializer.Serialize(packet, buf);

        // bytes[0..1]: PacketId(LE ushort) == 1
        ushort packetId = (ushort)(buf[0] | (buf[1] << 8));
        Assert.Equal((ushort)1, packetId);

        // bytes[2..3]: BodyLength(LE ushort) == bodySize
        ushort bodyLength = (ushort)(buf[2] | (buf[3] << 8));
        Assert.Equal((ushort)bodySize, bodyLength);
    }

    [Fact]
    public void Serialize_returns_total_bytes_written()
    {
        var serializer = new BinaryPacketSerializer();
        var packet = new EchoPacket { Message = "Test" };
        int expectedTotal = PacketPool.HeaderSize + packet.GetBodySize();
        byte[] buf = new byte[expectedTotal];

        int written = serializer.Serialize(packet, buf);

        Assert.Equal(expectedTotal, written);
    }

    [Fact]
    public void Deserialize_returns_correct_type_and_fields()
    {
        var serializer = new BinaryPacketSerializer();
        var packet = new EchoPacket { Message = "Test" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<EchoPacket>(buf);

        Assert.Equal("Test", p2.Message);
    }

    [Fact]
    public void TryReadPacketLength_returns_correct_total()
    {
        var serializer = new BinaryPacketSerializer();
        var packet = new DamagePacket { Amount = 100 };
        int expectedTotal = PacketPool.HeaderSize + packet.GetBodySize();
        byte[] buf = new byte[expectedTotal];
        serializer.Serialize(packet, buf);

        // 헤더 4바이트만 넘겨도 전체 길이를 반환해야 한다
        bool ok = serializer.TryReadPacketLength(buf.AsSpan(0, PacketPool.HeaderSize), out int totalLength);

        Assert.True(ok);
        Assert.Equal(expectedTotal, totalLength);
    }

    [Fact]
    public void TryReadPacketLength_returns_false_for_short_buffer()
    {
        var serializer = new BinaryPacketSerializer();
        // HeaderSize(4)보다 짧은 3바이트 버퍼
        byte[] buf = new byte[3];

        bool ok = serializer.TryReadPacketLength(buf, out int totalLength);

        Assert.False(ok);
        Assert.Equal(0, totalLength);
    }

    [Fact]
    public void Serialize_throws_ArgumentException_for_small_buffer()
    {
        var serializer = new BinaryPacketSerializer();
        var packet = new EchoPacket { Message = "Hi" };
        // 크기 1 버퍼는 HeaderSize(4)보다 작아 예외 발생
        byte[] tooSmall = new byte[1];

        Assert.Throws<ArgumentException>(() => serializer.Serialize(packet, tooSmall));
    }

    [Fact]
    public void Deserialize_throws_ArgumentException_for_short_buffer()
    {
        var serializer = new BinaryPacketSerializer();
        // 2바이트는 HeaderSize(4)보다 작아 예외 발생
        byte[] tooSmall = new byte[2];

        Assert.Throws<ArgumentException>(() => serializer.Deserialize<EchoPacket>(tooSmall));
    }

    [Fact]
    public void Header_packetId_is_little_endian()
    {
        // EchoPacket Id=1: LE이면 bytes[0]==1, bytes[1]==0
        var serializer = new BinaryPacketSerializer();
        var packet = new EchoPacket { Message = "X" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];

        serializer.Serialize(packet, buf);

        Assert.Equal(1, buf[0]); // 하위 바이트
        Assert.Equal(0, buf[1]); // 상위 바이트
    }

    // ── GAP-I-06: 잘린 본문 → EndOfStreamException ────────────────────────────────────────
    [Fact]
    public void Deserialize_throws_EndOfStreamException_when_body_truncated()
    {
        // 헤더: PacketId=1, BodyLength=10 클레임 — 그러나 실제 버퍼는 헤더(4B)만 존재
        // SpanReader가 EchoPacket.Deserialize → ReadString → EnsureAvailable(2) 시 EndOfStreamException
        var serializer = new BinaryPacketSerializer();
        byte[] buf = new byte[PacketPool.HeaderSize]; // 헤더만, 본문 없음
        buf[0] = 1; buf[1] = 0;   // PacketId = 1 (EchoPacket), LE
        buf[2] = 10; buf[3] = 0;  // BodyLength = 10(클레임), 실제 없음

        bool threw = false;
        try { serializer.Deserialize<EchoPacket>(buf); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "잘린 본문 버퍼는 EndOfStreamException을 던져야 합니다.");
    }
}
