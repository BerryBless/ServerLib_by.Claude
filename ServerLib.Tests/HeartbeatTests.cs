using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;
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

    [Fact]
    public void BuildPing_ThenTryBuildPong_ProducesPongWithSameTicks()
    {
        Span<byte> pingBuf = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int pingLen = HeartbeatProtocol.BuildPing(5000L, pingBuf);

        Span<byte> pongBuf = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int pongLen = HeartbeatProtocol.TryBuildPong(pingBuf.Slice(0, pingLen), pongBuf);

        Assert.True(pongLen > 0);
        var pong = Serializer.Deserialize<PongPacket>(pongBuf.Slice(0, pongLen));
        Assert.Equal(5000L, pong.ClientTicks);
    }

    [Fact]
    public void TryBuildPong_NonPingPacket_ReturnsZero()
    {
        var inc = new IncrementPacket();
        Span<byte> buf = stackalloc byte[64];
        int len = Serializer.Serialize(inc, buf);

        Span<byte> pong = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int written = HeartbeatProtocol.TryBuildPong(buf.Slice(0, len), pong);

        Assert.Equal(0, written);
    }

    [Fact]
    public void TryComputeRtt_PongPacket_ReturnsElapsedTicks()
    {
        var pong = new PongPacket { ClientTicks = 1000L };
        Span<byte> buf = stackalloc byte[HeartbeatProtocol.MaxPacketSize];
        int len = Serializer.Serialize(pong, buf);

        bool ok = HeartbeatProtocol.TryComputeRtt(buf.Slice(0, len), nowTicks: 1500L, out long rttTicks);

        Assert.True(ok);
        Assert.Equal(500L, rttTicks);
    }

    [Fact]
    public void TryComputeRtt_NonPongPacket_ReturnsFalse()
    {
        var inc = new IncrementPacket();
        Span<byte> buf = stackalloc byte[64];
        int len = Serializer.Serialize(inc, buf);

        bool ok = HeartbeatProtocol.TryComputeRtt(buf.Slice(0, len), nowTicks: 1500L, out _);

        Assert.False(ok);
    }
}
