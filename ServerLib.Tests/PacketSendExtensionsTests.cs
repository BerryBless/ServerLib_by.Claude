using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Tests.Stubs;
using Xunit;

namespace ServerLib.Tests;

/// <summary>E1: 패킷 레벨 SendAsync&lt;T&gt; 편의 오버로드 검증.</summary>
public sealed class PacketSendExtensionsTests
{
    [Fact]
    public async Task SendAsync_PacketOverload_SendsBytesEqualToManualSerialization()
    {
        var stub = new StubSession();
        var packet = new IncrementPacket();

        await stub.SendAsync(packet);

        // 수동 직렬화(헤더+본문) 결과와 바이트 단위로 동일해야 한다.
        var serializer = new BinaryPacketSerializer();
        var expected = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        int n = serializer.Serialize(packet, expected);

        var sent = Assert.Single(stub.SentBuffers);
        Assert.Equal(expected.AsSpan(0, n).ToArray(), sent);
        Assert.True(PacketPool.TryParseHeader(sent, out ushort id, out _));
        Assert.Equal(IncrementPacket.Id, id);
    }

    [Fact]
    public async Task SendAsync_PacketOverload_WhenSendThrowsSynchronously_Propagates()
    {
        // ThrowOnSend 스텁은 SendAsync에서 동기 throw → 확장이 대여 버퍼를 반납하고 예외를 전파해야 한다.
        var stub = new StubSession { ThrowOnSend = true };

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await stub.SendAsync(new IncrementPacket()));
    }
}
