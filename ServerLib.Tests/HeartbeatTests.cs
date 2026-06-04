using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using Xunit;

namespace ServerLib.Tests;

public sealed class HeartbeatTests
{
    private static readonly BinaryPacketSerializer Serializer = new();

    [Fact]
    public void PingPacket_RoundTrip_PreservesClientTicks()
    {
        var ping = new PingPacket { ClientTicks = 1234567890L };
        Span<byte> buf = stackalloc byte[64];
        int written = Serializer.Serialize(ping, buf);
        var decoded = Serializer.Deserialize<PingPacket>(buf.Slice(0, written));

        Assert.Equal(PingPacket.Id, ping.PacketId);
        Assert.Equal(1234567890L, decoded.ClientTicks);
    }

    [Fact]
    public void PongPacket_RoundTrip_PreservesClientTicks()
    {
        var pong = new PongPacket { ClientTicks = 9876543210L };
        Span<byte> buf = stackalloc byte[64];
        int written = Serializer.Serialize(pong, buf);
        var decoded = Serializer.Deserialize<PongPacket>(buf.Slice(0, written));

        Assert.Equal(PongPacket.Id, pong.PacketId);
        Assert.Equal(9876543210L, decoded.ClientTicks);
    }

    [Fact]
    public void PingPacket_BodySize_Is8()
    {
        Assert.Equal(8, new PingPacket().GetBodySize());
        Assert.Equal(8, new PongPacket().GetBodySize());
    }
}
