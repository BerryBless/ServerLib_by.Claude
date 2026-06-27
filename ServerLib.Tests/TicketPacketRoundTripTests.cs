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

    // ─────── TicketReserveRequestPacket (배치 포맷: [Count(1B)][Row,Col 쌍...]) ───────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(255, 255)]
    public void TicketReserveRequest_roundtrip_single_seat_preserves_row_and_col(byte row, byte col)
    {
        // Single(): Count=1, Rows=[row], Cols=[col]
        var pkt    = TicketReserveRequestPacket.Single(row, col);
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(TicketReserveRequestPacket.Id, p2.PacketId);
        Assert.Equal(1,   p2.Count);
        Assert.Equal(row, p2.Rows[0]);
        Assert.Equal(col, p2.Cols[0]);
    }

    [Fact]
    public void TicketReserveRequest_roundtrip_batch_preserves_all_pairs()
    {
        // 3석 배치: (0,1), (1,0), (1,2) — Count=3, bodySize=1+3*2=7
        var pkt = new TicketReserveRequestPacket
        {
            Count = 3,
            Rows  = new byte[] { 0, 1, 1 },
            Cols  = new byte[] { 1, 0, 2 }
        };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(TicketReserveRequestPacket.Id, p2.PacketId);
        Assert.Equal(3, p2.Count);
        Assert.Equal(new byte[] { 0, 1, 1 }, p2.Rows);
        Assert.Equal(new byte[] { 1, 0, 2 }, p2.Cols);
    }

    [Theory]
    [InlineData(0, 1)]  // Count=0 → 1+0*2=1
    [InlineData(1, 3)]  // Count=1 → 1+1*2=3
    [InlineData(4, 9)]  // Count=4 → 1+4*2=9
    public void TicketReserveRequest_bodySize_is_1_plus_count_times_2(byte count, int expected)
    {
        var pkt = new TicketReserveRequestPacket
        {
            Count = count,
            Rows  = new byte[count],
            Cols  = new byte[count]
        };
        Assert.Equal(expected, pkt.GetBodySize());
    }

    // ─────── TicketPayRequestPacket ───────

    [Fact]
    public void TicketPayRequest_roundtrip_preserves_packetId()
    {
        // [SEC-NEW-01] SimulateFailure 필드 제거됨 — 본문 0B, 패킷 ID만 라운드트립 검증
        var pkt    = new TicketPayRequestPacket();
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketPayRequestPacket>(buf);
        Assert.Equal(TicketPayRequestPacket.Id, p2.PacketId);
    }

    [Fact]
    public void TicketPayRequest_bodySize_is_zero()
    {
        // [SEC-NEW-01] SimulateFailure 필드 제거 → 본문 0B
        Assert.Equal(0, new TicketPayRequestPacket().GetBodySize());
    }

    // ─────── TicketResultPacket (배치 포맷: [Status(1B)][Count(1B)][Slots...][Remaining(1B)]) ───────

    [Theory]
    // (status, count, seatId대표값, remaining) — count>0이면 Slots=[seatId], 0이면 빈 배열
    [InlineData(TicketStatus.Reserved,       1, (byte)0,    2)]
    [InlineData(TicketStatus.SoldOut,        0, (byte)0xFF, 0)]
    [InlineData(TicketStatus.AlreadyReserved,0, (byte)0xFF, 1)]
    [InlineData(TicketStatus.NotReserved,    0, (byte)0xFF, 3)]
    [InlineData(TicketStatus.Confirmed,      1, (byte)2,    0)]
    [InlineData(TicketStatus.PaymentFailed,  0, (byte)0xFF, 2)]
    [InlineData(TicketStatus.Released,       1, (byte)1,    3)]
    [InlineData(TicketStatus.SeatTaken,      0, (byte)0xFF, 5)]
    [InlineData(TicketStatus.RateLimited,    0, (byte)0xFF, 0)] // GAP-I-17: RateLimited 상태 라운드트립
    public void TicketResult_roundtrip_preserves_all_fields(
        TicketStatus status, byte count, byte seatId, byte remaining)
    {
        // count>0이면 단일 슬롯 배열, 0이면 빈 배열
        byte[] slots = count > 0 ? new[] { seatId } : Array.Empty<byte>();
        var pkt    = new TicketResultPacket { Status = status, Count = count, Slots = slots, Remaining = remaining };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketResultPacket>(buf);
        Assert.Equal(TicketResultPacket.Id, p2.PacketId);
        Assert.Equal(status,    p2.Status);
        Assert.Equal(count,     p2.Count);
        Assert.Equal(remaining, p2.Remaining);
        if (count > 0)
        {
            Assert.NotNull(p2.Slots);
            Assert.Equal(count, p2.Slots.Length);
            Assert.Equal(seatId, p2.Slots[0]);
        }
        else
        {
            Assert.NotNull(p2.Slots);
            Assert.Empty(p2.Slots);
        }
    }

    [Fact]
    public void TicketResult_roundtrip_batch_two_slots()
    {
        // 2석 배치 확정: Status=Confirmed, Count=2, Slots=[1,4], Remaining=2
        var pkt = new TicketResultPacket
        {
            Status    = TicketStatus.Confirmed,
            Count     = 2,
            Slots     = new byte[] { 1, 4 },
            Remaining = 2
        };
        byte[] buf = new byte[PacketPool.HeaderSize + pkt.GetBodySize()];
        _serializer.Serialize(pkt, buf);

        var p2 = _serializer.Deserialize<TicketResultPacket>(buf);
        Assert.Equal(TicketStatus.Confirmed, p2.Status);
        Assert.Equal(2,   p2.Count);
        Assert.Equal(new byte[] { 1, 4 }, p2.Slots);
        Assert.Equal(2,   p2.Remaining);
        Assert.Equal(3 + 2, pkt.GetBodySize()); // 5B: [Status][Count][1][4][Remaining]
    }

    [Fact]
    public void TicketResult_noSlot_constant_is_0xFF()
    {
        Assert.Equal((byte)0xFF, TicketResultPacket.NoSlot);
    }

    [Fact]
    public void TicketResult_bodySize_is_3_plus_count()
    {
        // Count=0(기본): bodySize=3+0=3 (하위호환)
        Assert.Equal(3, new TicketResultPacket().GetBodySize());
        // Count=1: bodySize=3+1=4
        Assert.Equal(4, new TicketResultPacket { Count = 1, Slots = new byte[1] }.GetBodySize());
        // Count=4: bodySize=3+4=7
        Assert.Equal(7, new TicketResultPacket { Count = 4, Slots = new byte[4] }.GetBodySize());
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
