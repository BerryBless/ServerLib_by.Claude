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

    // ─── GAP-C-02: TicketReserveRequestPacket 배치 포맷 라운드트립 ──────────────────────────

    [Fact]
    public void TicketReserveRequestPacket_single_seat_roundtrip()
    {
        // Single() 팩토리: Count=1, Rows=[row], Cols=[col]
        var serializer = Serializer;
        var packet = TicketReserveRequestPacket.Single(1, 2);
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(TicketReserveRequestPacket.Id, p2.PacketId);
        Assert.Equal(1, p2.Count);
        Assert.Equal((byte)1, p2.Rows[0]);
        Assert.Equal((byte)2, p2.Cols[0]);
    }

    [Fact]
    public void TicketReserveRequestPacket_batch_roundtrip()
    {
        // 3석 배치: Count=3, bodySize=1+3*2=7B
        var serializer = Serializer;
        var packet = new TicketReserveRequestPacket
        {
            Count = 3,
            Rows  = new byte[] { 0, 1, 1 },
            Cols  = new byte[] { 0, 0, 2 }
        };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(3, p2.Count);
        Assert.Equal(new byte[] { 0, 1, 1 }, p2.Rows);
        Assert.Equal(new byte[] { 0, 0, 2 }, p2.Cols);
    }

    [Fact]
    public void TicketReserveRequestPacket_count_zero_roundtrip()
    {
        // Count=0: 본문은 [0x00] 1바이트 — 빈 배치 요청 경계값 직렬화 검증
        var serializer = Serializer;
        var packet = new TicketReserveRequestPacket { Count = 0 };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<TicketReserveRequestPacket>(buf);
        Assert.Equal(TicketReserveRequestPacket.Id, p2.PacketId);
        Assert.Equal(0, p2.Count);
        Assert.NotNull(p2.Rows);
        Assert.Empty(p2.Rows);
        Assert.NotNull(p2.Cols);
        Assert.Empty(p2.Cols);
    }

    // ─── GAP-C-03: TicketResultPacket 가변 배열 직렬화 라운드트립 ──────────────────────────

    [Fact]
    public void TicketResultPacket_confirmed_roundtrip()
    {
        // Confirmed: Count=2, Slots=[3,5], Remaining=2
        var serializer = Serializer;
        var packet = new TicketResultPacket
        {
            Status    = TicketStatus.Confirmed,
            Count     = 2,
            Slots     = new byte[] { 3, 5 },
            Remaining = 2
        };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<TicketResultPacket>(buf);
        Assert.Equal(TicketResultPacket.Id, p2.PacketId);
        Assert.Equal(TicketStatus.Confirmed, p2.Status);
        Assert.Equal(2, p2.Count);
        Assert.Equal(new byte[] { 3, 5 }, p2.Slots);
        Assert.Equal((byte)2, p2.Remaining);
    }

    [Fact]
    public void TicketResultPacket_failed_count_zero_roundtrip()
    {
        // 실패(SeatTaken): Count=0, Slots=빈 배열 경계값
        var serializer = Serializer;
        var packet = new TicketResultPacket
        {
            Status    = TicketStatus.SeatTaken,
            Count     = 0,
            Slots     = Array.Empty<byte>(),
            Remaining = 4
        };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<TicketResultPacket>(buf);
        Assert.Equal(TicketStatus.SeatTaken, p2.Status);
        Assert.Equal((byte)0, p2.Count);
        Assert.NotNull(p2.Slots);
        Assert.Empty(p2.Slots);
        Assert.Equal((byte)4, p2.Remaining);
    }

    // ─── GAP-C-04: SeatMapResponsePacket 가변 배열 직렬화 라운드트립 ──────────────────────

    [Fact]
    public void SeatMapResponsePacket_2x3_roundtrip()
    {
        // 2×3 좌석맵: 6석 상태 배열 라운드트립
        var serializer = Serializer;
        var states = new byte[] { 0, 1, 2, 0, 1, 0 }; // Free/Reserved/Sold 혼합
        var packet = new SeatMapResponsePacket { Rows = 2, Cols = 3, States = states };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<SeatMapResponsePacket>(buf);
        Assert.Equal((byte)2, p2.Rows);
        Assert.Equal((byte)3, p2.Cols);
        Assert.Equal(states, p2.States);
    }

    [Fact]
    public void SeatMapResponsePacket_rows_cols_overflow_throws_InvalidDataException()
    {
        // Rows=16, Cols=16 → 16*16=256 > 255(byte.MaxValue) → InvalidDataException
        // 역직렬화 경로 보안 검증: 와이어 조작으로 256석 이상 지정 시 차단됨
        var serializer = Serializer;
        byte rows = 16;
        byte cols = 16;
        int bodySize = 2 + rows * cols; // 2 + 256 = 258B
        byte[] buf = new byte[PacketPool.HeaderSize + bodySize];
        // 헤더: PacketId=17(SeatMapResponsePacket), BodyLength=258
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), SeatMapResponsePacket.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)bodySize);
        // 본문: [Rows=16][Cols=16][States[256]=0..0]
        buf[4] = rows;
        buf[5] = cols;
        // States는 기본 0(Free)

        bool threw = false;
        try { serializer.Deserialize<SeatMapResponsePacket>(buf); }
        catch (InvalidDataException) { threw = true; }
        Assert.True(threw, "Rows*Cols=256 > 255이면 InvalidDataException을 던져야 합니다.");
    }

    // ─── GAP-I-03: LoginRequestPacket 문자열 직렬화 라운드트립 ──────────────────────────────

    [Fact]
    public void LoginRequestPacket_roundtrip()
    {
        // Username + Password 두 문자열 필드 라운드트립
        var serializer = Serializer;
        var packet = new LoginRequestPacket { Username = "alice", Password = "s3cr3t!" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<LoginRequestPacket>(buf);
        Assert.Equal(LoginRequestPacket.Id, p2.PacketId);
        Assert.Equal("alice", p2.Username);
        Assert.Equal("s3cr3t!", p2.Password);
    }

    [Fact]
    public void LoginRequestPacket_empty_password_roundtrip()
    {
        // Password가 빈 문자열인 경계값 — 2바이트 길이 접두어 [0x00, 0x00]만 기록
        var serializer = Serializer;
        var packet = new LoginRequestPacket { Username = "bob", Password = "" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<LoginRequestPacket>(buf);
        Assert.Equal("bob", p2.Username);
        Assert.Equal("", p2.Password);
    }

    // ─── GAP-I-04: LoginResponsePacket bool+string 직렬화 라운드트립 ──────────────────────

    [Fact]
    public void LoginResponsePacket_success_roundtrip()
    {
        // Success=true, Token 문자열 라운드트립
        var serializer = Serializer;
        var packet = new LoginResponsePacket { Success = true, Token = "abc-def_xyz" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<LoginResponsePacket>(buf);
        Assert.Equal(LoginResponsePacket.Id, p2.PacketId);
        Assert.True(p2.Success);
        Assert.Equal("abc-def_xyz", p2.Token);
    }

    [Fact]
    public void LoginResponsePacket_failure_empty_token_roundtrip()
    {
        // 로그인 실패 시 Success=false, Token="" 경계값
        var serializer = Serializer;
        var packet = new LoginResponsePacket { Success = false, Token = "" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<LoginResponsePacket>(buf);
        Assert.False(p2.Success);
        Assert.Equal("", p2.Token);
    }

    // ─── GAP-I-05: AuthTokenPacket 문자열 직렬화 라운드트립 ─────────────────────────────────

    [Fact]
    public void AuthTokenPacket_roundtrip()
    {
        // 게임서버 제출용 base64url 토큰 라운드트립
        var serializer = Serializer;
        var packet = new AuthTokenPacket { Token = "sample-base64url-token_value" };
        byte[] buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        serializer.Serialize(packet, buf);

        var p2 = serializer.Deserialize<AuthTokenPacket>(buf);
        Assert.Equal(AuthTokenPacket.Id, p2.PacketId);
        Assert.Equal("sample-base64url-token_value", p2.Token);
    }
}
