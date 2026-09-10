using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using Xunit;

namespace CounterExample.Tests;

/// <summary>
/// 경합 카운터 예제가 쓰는 4개 패킷의 ID·본문 길이·직렬화 왕복을 검증합니다.
/// </summary>
/// <remarks>
/// 와이어 포맷은 서버와 클라이언트가 <b>동시에</b> 지켜야 하는 계약이라, 한쪽만 바뀌면
/// 런타임에 "길이 검증 실패"라는 모호한 증상으로만 드러납니다. 여기서 상수로 못 박아 둡니다.
/// </remarks>
public class CounterPacketTests
{
    // BinaryPacketSerializer: 무상태(stateless), Thread-safe → 병렬 실행되는 테스트들이 공유해도 안전.
    private static readonly BinaryPacketSerializer Serializer = new();

    // ── 와이어 포맷 상수 (F-2 확정: 응답 본문은 8B가 아니라 16B) ───────────────
    private const ushort ExpectedIncrementId = 3;
    private const ushort ExpectedDecrementId = 4;
    private const ushort ExpectedQueryId = 18;
    private const ushort ExpectedValueId = 19;
    private const int ExpectedCommandBodySize = 0;   // 증감·조회는 본문 없음
    private const int ExpectedValueBodySize = 16;    // long Value(8) + long AppliedOps(8)

    /// <summary>증감 명령 패킷의 ID와 본문 길이가 계약대로인지 확인합니다.</summary>
    [Fact]
    public void IncrementAndDecrement_HaveExpectedIdsAndEmptyBody()
    {
        Assert.Equal(ExpectedIncrementId, IncrementPacket.Id);
        Assert.Equal(ExpectedDecrementId, DecrementPacket.Id);

        var inc = new IncrementPacket();
        var dec = new DecrementPacket();
        Assert.Equal(ExpectedIncrementId, inc.PacketId);
        Assert.Equal(ExpectedDecrementId, dec.PacketId);
        Assert.Equal(ExpectedCommandBodySize, inc.GetBodySize());
        Assert.Equal(ExpectedCommandBodySize, dec.GetBodySize());
    }

    /// <summary>조회 요청 패킷의 ID와 본문 길이(0B)를 확인합니다.</summary>
    [Fact]
    public void CounterQueryPacket_HasExpectedIdAndEmptyBody()
    {
        Assert.Equal(ExpectedQueryId, CounterQueryPacket.Id);
        Assert.Equal(ExpectedCommandBodySize, CounterQueryPacket.BodySize);

        var query = new CounterQueryPacket();
        Assert.Equal(ExpectedQueryId, query.PacketId);
        Assert.Equal(ExpectedCommandBodySize, query.GetBodySize());
    }

    /// <summary>값 응답 패킷의 ID와 본문 길이(16B = long 2개)를 확인합니다.</summary>
    [Fact]
    public void CounterValuePacket_HasExpectedIdAndSixteenByteBody()
    {
        Assert.Equal(ExpectedValueId, CounterValuePacket.Id);
        Assert.Equal(ExpectedValueBodySize, CounterValuePacket.BodySize);

        var packet = new CounterValuePacket();
        Assert.Equal(ExpectedValueId, packet.PacketId);
        Assert.Equal(ExpectedValueBodySize, packet.GetBodySize());
    }

    /// <summary>직렬화된 프레임의 헤더가 ID와 본문 길이를 정확히 담고, 전체 길이가 4+16=20B인지 확인합니다.</summary>
    /// <remarks>
    /// 클라이언트의 응답 검증 상수는 <b>본문</b> 16이며 <b>프레임</b>은 20입니다.
    /// 4바이트 헤더를 빼먹은 off-by-4는 "16을 쓰기는 썼는데 틀린" 형태로 조용히 통과할 수 있어 여기서 분리 단언합니다.
    /// </remarks>
    [Fact]
    public void CounterValuePacket_SerializedFrame_HasHeaderPlusSixteenByteBody()
    {
        // 정확한 크기의 스택 밖 배열: 테스트 1회용이라 풀 대여 없이 직접 할당한다.
        var buffer = new byte[PacketPool.HeaderSize + ExpectedValueBodySize];
        int written = Serializer.Serialize(new CounterValuePacket { Value = 7, AppliedOps = 11 }, buffer);

        Assert.Equal(PacketPool.HeaderSize + ExpectedValueBodySize, written);
        Assert.True(PacketPool.TryParseHeader(buffer, out ushort packetId, out int bodyLength));
        Assert.Equal(ExpectedValueId, packetId);
        Assert.Equal(ExpectedValueBodySize, bodyLength);
    }

    /// <summary>조회 요청 프레임이 헤더 4바이트뿐인지 확인합니다.</summary>
    [Fact]
    public void CounterQueryPacket_SerializedFrame_IsHeaderOnly()
    {
        var buffer = new byte[PacketPool.HeaderSize];
        int written = Serializer.Serialize(new CounterQueryPacket(), buffer);

        Assert.Equal(PacketPool.HeaderSize, written);
        Assert.True(PacketPool.TryParseHeader(buffer, out ushort packetId, out int bodyLength));
        Assert.Equal(ExpectedQueryId, packetId);
        Assert.Equal(ExpectedCommandBodySize, bodyLength);
    }

    /// <summary>
    /// 값 응답이 0·음수·<see cref="long"/> 경계값에서도 왕복 후 원본과 동일한지 검증합니다.
    /// </summary>
    /// <remarks>
    /// 카운터는 빼기가 더 많으면 음수가 되고, 학습용으로 값 범위를 극단까지 넣어 볼 수 있습니다.
    /// 리틀엔디언 <c>WriteInt64</c>/<c>ReadInt64</c>가 부호 비트와 경계값을 손상시키지 않음을 확인합니다.
    /// </remarks>
    [Theory]
    [InlineData(0L, 0L)]                              // 초기 상태
    [InlineData(2_000L, 14_000L)]                     // 기본 시나리오 기대값
    [InlineData(-1L, 1L)]                             // 빼기 1회만 적용
    [InlineData(-750L, 750L)]                         // 빼기만 누적
    [InlineData(long.MinValue, long.MaxValue)]        // 경계값 (부호 비트)
    [InlineData(long.MaxValue, long.MinValue)]        // 경계값 (반대 배치 — 필드 순서 뒤바뀜 탐지)
    public void CounterValuePacket_RoundTrips(long value, long appliedOps)
    {
        var buffer = new byte[PacketPool.HeaderSize + ExpectedValueBodySize];
        Serializer.Serialize(new CounterValuePacket { Value = value, AppliedOps = appliedOps }, buffer);

        // Deserialize<T>: 헤더를 건너뛰고 본문만 SpanReader로 읽는다. struct라 힙 할당 0.
        CounterValuePacket decoded = Serializer.Deserialize<CounterValuePacket>(buffer);

        Assert.Equal(value, decoded.Value);
        Assert.Equal(appliedOps, decoded.AppliedOps);
    }

    /// <summary>본문 없는 패킷도 왕복 후 ID가 보존되는지 확인합니다.</summary>
    [Fact]
    public void EmptyBodyPackets_RoundTrip()
    {
        var buffer = new byte[PacketPool.HeaderSize];

        Serializer.Serialize(new CounterQueryPacket(), buffer);
        Assert.Equal(ExpectedQueryId, Serializer.Deserialize<CounterQueryPacket>(buffer).PacketId);

        Serializer.Serialize(new IncrementPacket(), buffer);
        Assert.Equal(ExpectedIncrementId, Serializer.Deserialize<IncrementPacket>(buffer).PacketId);

        Serializer.Serialize(new DecrementPacket(), buffer);
        Assert.Equal(ExpectedDecrementId, Serializer.Deserialize<DecrementPacket>(buffer).PacketId);
    }
}
