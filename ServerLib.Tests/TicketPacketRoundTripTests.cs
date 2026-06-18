using Xunit;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Tests;

/// <summary>
/// 티켓팅 패킷 3종(TicketReserveRequest, TicketPayRequest, TicketResult)의 직렬화·역직렬화 라운드트립을 검증합니다.
/// </summary>
public class TicketPacketRoundTripTests
{
    private readonly BinaryPacketSerializer _serializer = new();

    [Fact]
    public void TicketReserveRequest_roundtrip_preserves_packetId()
    {
        // 본문이 없는 패킷 — ID 보존만 검증
        var pkt    = new TicketReserveRequestPacket();
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(TicketReserveRequestPacket.Id, p2.PacketId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TicketPayRequest_roundtrip_preserves_simulateFailure(bool simulateFailure)
    {
        var pkt    = new TicketPayRequestPacket { SimulateFailure = simulateFailure };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketPayRequestPacket>(buf);
        Assert.Equal(TicketPayRequestPacket.Id, p2.PacketId);
        Assert.Equal(simulateFailure, p2.SimulateFailure);
    }

    [Theory]
    [InlineData(TicketStatus.Reserved,       0,    2)]
    [InlineData(TicketStatus.SoldOut,        0xFF, 0)]
    [InlineData(TicketStatus.AlreadyReserved,1,    1)]
    [InlineData(TicketStatus.NotReserved,    0xFF, 3)]
    [InlineData(TicketStatus.Confirmed,      2,    0)]
    [InlineData(TicketStatus.PaymentFailed,  0,    2)]
    [InlineData(TicketStatus.Released,       1,    3)]
    public void TicketResult_roundtrip_preserves_all_fields(TicketStatus status, byte slot, byte remaining)
    {
        var pkt    = new TicketResultPacket { Status = status, Slot = slot, Remaining = remaining };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketResultPacket>(buf);
        Assert.Equal(TicketResultPacket.Id, p2.PacketId);
        Assert.Equal(status,    p2.Status);
        Assert.Equal(slot,      p2.Slot);
        Assert.Equal(remaining, p2.Remaining);
    }

    [Fact]
    public void TicketResult_noSlot_constant_is_0xFF()
    {
        Assert.Equal((byte)0xFF, TicketResultPacket.NoSlot);
    }

    [Fact]
    public void TicketReserveRequest_bodySize_is_zero()
    {
        Assert.Equal(0, new TicketReserveRequestPacket().GetBodySize());
    }

    [Fact]
    public void TicketPayRequest_bodySize_is_one()
    {
        Assert.Equal(1, new TicketPayRequestPacket().GetBodySize());
    }

    [Fact]
    public void TicketResult_bodySize_is_three()
    {
        Assert.Equal(3, new TicketResultPacket().GetBodySize());
    }
}
