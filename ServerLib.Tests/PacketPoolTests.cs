using Xunit;
using ServerLib.Core.Memory;

namespace ServerLib.Tests;

public class PacketPoolTests
{
    [Fact]
    public void HeaderSize_is_4()
    {
        Assert.Equal(4, PacketPool.HeaderSize);
    }

    [Fact]
    public void WriteHeader_TryParseHeader_round_trip()
    {
        byte[] buf = new byte[4];
        PacketPool.WriteHeader(buf, packetId: 42, bodyLength: 100);

        bool ok = PacketPool.TryParseHeader(buf, out ushort packetId, out int bodyLength);

        Assert.True(ok);
        Assert.Equal(42, packetId);
        Assert.Equal(100, bodyLength);
    }

    [Fact]
    public void TryParseHeader_returns_false_for_short_buffer()
    {
        // 3바이트 — HeaderSize(4)보다 짧으므로 false 반환
        byte[] buf = new byte[3];

        bool ok = PacketPool.TryParseHeader(buf, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void WriteHeader_bodyLength_over_ushort_max_throws()
    {
        byte[] buf = new byte[4];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketPool.WriteHeader(buf, packetId: 1, bodyLength: 65536));
    }

    [Fact]
    public void WriteHeader_bodyLength_zero_is_valid()
    {
        byte[] buf = new byte[4];

        // bodyLength=0은 유효한 경계값이므로 예외 없이 완료해야 한다
        PacketPool.WriteHeader(buf, packetId: 0, bodyLength: 0);

        bool ok = PacketPool.TryParseHeader(buf, out _, out int bodyLength);
        Assert.True(ok);
        Assert.Equal(0, bodyLength);
    }

    [Fact]
    public void WriteHeader_bodyLength_ushort_max_is_valid()
    {
        byte[] buf = new byte[4];

        // bodyLength=65535은 ushort 최대값이므로 예외 없이 완료해야 한다
        PacketPool.WriteHeader(buf, packetId: 1, bodyLength: 65535);

        bool ok = PacketPool.TryParseHeader(buf, out _, out int bodyLength);
        Assert.True(ok);
        Assert.Equal(65535, bodyLength);
    }

    [Fact]
    public void RentSendBuffer_returns_at_least_requested_size()
    {
        byte[] buf = PacketPool.RentSendBuffer(100);
        try
        {
            Assert.True(buf.Length >= 100);
        }
        finally
        {
            PacketPool.ReturnSendBuffer(buf);
        }
    }

    [Fact]
    public void ReturnSendBuffer_does_not_throw()
    {
        byte[] buf = PacketPool.RentSendBuffer(64);

        // 정상 반납 — 예외가 발생하지 않아야 한다
        var ex = Record.Exception(() => PacketPool.ReturnSendBuffer(buf));
        Assert.Null(ex);
    }

    [Fact]
    public void Headers_pool_get_return_works()
    {
        // Get() — null이 아닌 PacketHeader 반환 확인
        PacketHeader header = PacketPool.Headers.Get();
        Assert.NotNull(header);

        // 값을 설정한 뒤 반납하면 Return이 Reset을 호출하고,
        // 다음 Get()에서 초기화된 상태(0)로 돌아와야 한다
        header.PacketId = 99;
        header.BodyLength = 1234;
        PacketPool.Headers.Return(header);

        PacketHeader next = PacketPool.Headers.Get();
        Assert.NotNull(next);
        // Reset 후 필드가 0으로 초기화됐는지 검증
        Assert.Equal(0, next.PacketId);
        Assert.Equal(0, next.BodyLength);

        PacketPool.Headers.Return(next);
    }
}
