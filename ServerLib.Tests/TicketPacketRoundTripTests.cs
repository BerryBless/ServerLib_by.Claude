using Xunit;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Tests;

/// <summary>
/// 티켓팅 패킷 5종(SeatMapRequest, SeatMapResponse, TicketReserveRequest, TicketPayRequest, TicketResult)의
/// 직렬화·역직렬화 라운드트립을 검증합니다.
/// </summary>
public class TicketPacketRoundTripTests
{
    private readonly BinaryPacketSerializer _serializer = new();

    // ─────── SeatMapRequestPacket ───────

    [Fact]
    public void SeatMapRequest_roundtrip_preserves_packetId()
    {
        var pkt    = new SeatMapRequestPacket();
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<SeatMapRequestPacket>(buf);
        Assert.Equal(SeatMapRequestPacket.Id, p2.PacketId);
    }

    [Fact]
    public void SeatMapRequest_bodySize_is_zero()
    {
        Assert.Equal(0, new SeatMapRequestPacket().GetBodySize());
    }

    // ─────── SeatMapResponsePacket ───────

    [Fact]
    public void SeatMapResponse_roundtrip_preserves_rows_cols_states()
    {
        byte rows  = 2;
        byte cols  = 3;
        // States: [Free, Reserved, Sold, Free, Free, Reserved] — 6석
        var states = new byte[] { 0, 1, 2, 0, 0, 1 };
        var pkt    = new SeatMapResponsePacket { Rows = rows, Cols = cols, States = states };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<SeatMapResponsePacket>(buf);
        Assert.Equal(SeatMapResponsePacket.Id, p2.PacketId);
        Assert.Equal(rows,   p2.Rows);
        Assert.Equal(cols,   p2.Cols);
        Assert.NotNull(p2.States);
        Assert.Equal(states, p2.States);
    }

    [Fact]
    public void SeatMapResponse_bodySize_is_2_plus_rows_times_cols()
    {
        var pkt = new SeatMapResponsePacket { Rows = 3, Cols = 4, States = new byte[12] };
        Assert.Equal(2 + 3 * 4, pkt.GetBodySize()); // 14B
    }

    [Fact]
    public void SeatMapResponse_single_seat_roundtrip()
    {
        var pkt    = new SeatMapResponsePacket { Rows = 1, Cols = 1, States = new byte[] { 2 } }; // Sold
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<SeatMapResponsePacket>(buf);
        Assert.Equal(1, p2.Rows);
        Assert.Equal(1, p2.Cols);
        Assert.NotNull(p2.States);
        Assert.Equal(2, p2.States[0]); // Sold
    }

    // ─────── TicketReserveRequestPacket (Row/Col 추가됨) ───────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(255, 255)]
    public void TicketReserveRequest_roundtrip_preserves_row_and_col(byte row, byte col)
    {
        var pkt    = new TicketReserveRequestPacket { Row = row, Col = col };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(TicketReserveRequestPacket.Id, p2.PacketId);
        Assert.Equal(row, p2.Row);
        Assert.Equal(col, p2.Col);
    }

    [Fact]
    public void TicketReserveRequest_bodySize_is_two()
    {
        Assert.Equal(2, new TicketReserveRequestPacket().GetBodySize());
    }

    // ─────── TicketPayRequestPacket ───────

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

    [Fact]
    public void TicketPayRequest_bodySize_is_one()
    {
        Assert.Equal(1, new TicketPayRequestPacket().GetBodySize());
    }

    // ─────── TicketResultPacket ───────

    [Theory]
    [InlineData(TicketStatus.Reserved,       0,    2)]
    [InlineData(TicketStatus.SoldOut,        0xFF, 0)]
    [InlineData(TicketStatus.AlreadyReserved,1,    1)]
    [InlineData(TicketStatus.NotReserved,    0xFF, 3)]
    [InlineData(TicketStatus.Confirmed,      2,    0)]
    [InlineData(TicketStatus.PaymentFailed,  0,    2)]
    [InlineData(TicketStatus.Released,       1,    3)]
    [InlineData(TicketStatus.SeatTaken,      0xFF, 5)] // 신규: 좌석 점유됨
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
    public void TicketResult_bodySize_is_three()
    {
        Assert.Equal(3, new TicketResultPacket().GetBodySize());
    }

    // ─────── TicketStatus 열거형 검증 ───────

    [Fact]
    public void TicketStatus_seatTaken_value_is_seven()
    {
        Assert.Equal((byte)7, (byte)TicketStatus.SeatTaken);
    }

    [Fact]
    public void TicketStatus_all_values_are_unique()
    {
        var values = Enum.GetValues<TicketStatus>().Cast<byte>().ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    // ─────── 패킷 ID 상수 충돌 없음 검증 ───────

    [Fact]
    public void All_ticket_packet_ids_are_unique()
    {
        var ids = new[]
        {
            SeatMapRequestPacket.Id,
            SeatMapResponsePacket.Id,
            TicketReserveRequestPacket.Id,
            TicketPayRequestPacket.Id,
            TicketResultPacket.Id,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
