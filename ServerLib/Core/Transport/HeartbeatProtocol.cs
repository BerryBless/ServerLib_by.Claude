using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Core.Transport;

/// <summary>
/// 하트비트 PING/PONG의 직렬화·RTT 계산을 소켓과 무관하게 처리하는 순수 정적 헬퍼입니다.
/// 세션(서버)과 클라이언트가 공유하며, 소켓 없이 단위 테스트 가능합니다.
/// </summary>
internal static class HeartbeatProtocol
{
    // PING/PONG은 헤더(4B) + long(8B) = 12B 고정 — 스택 버퍼 크기 상한으로 사용
    public const int MaxPacketSize = PacketPool.HeaderSize + 8;

    // 무상태 직렬화기 공유 (내부 상태 없음 → thread-safe)
    private static readonly BinaryPacketSerializer Serializer = new();

    /// <summary>지정한 패킷 ID가 PING인지 반환합니다. 세션이 이미 파싱한 ID로 직접 분기해 매 패킷 PONG 프로브를 피하도록 합니다.</summary>
    public static bool IsPing(ushort packetId) => packetId == PingPacket.Id;

    /// <summary>지정한 패킷 ID가 PONG인지 반환합니다. 클라이언트가 이미 파싱한 ID로 직접 분기해 매 패킷 RTT 프로브를 피하도록 합니다.</summary>
    public static bool IsPong(ushort packetId) => packetId == PongPacket.Id;

    /// <summary>PING 패킷을 dest에 직렬화하고 기록 바이트 수를 반환합니다.</summary>
    public static int BuildPing(long clientTicks, Span<byte> dest)
    {
        var ping = new PingPacket { ClientTicks = clientTicks };
        return Serializer.Serialize(ping, dest);
    }

    /// <summary>
    /// <paramref name="packet"/>이 PING이면 동일 ticks의 PONG을 <paramref name="dest"/>에 직렬화하고
    /// 기록 바이트 수를 반환합니다. PING이 아니면 0을 반환합니다.
    /// </summary>
    public static int TryBuildPong(ReadOnlySpan<byte> packet, Span<byte> dest)
    {
        if (!PacketPool.TryParseHeader(packet, out ushort id, out _) || id != PingPacket.Id)
            return 0;
        var ping = Serializer.Deserialize<PingPacket>(packet);
        var pong = new PongPacket { ClientTicks = ping.ClientTicks };
        return Serializer.Serialize(pong, dest);
    }

    /// <summary>
    /// <paramref name="packet"/>이 PONG이면 <paramref name="nowTicks"/> - 에코된 ClientTicks로
    /// RTT(ticks)를 계산해 true를 반환합니다. PONG이 아니면 false.
    /// </summary>
    public static bool TryComputeRtt(ReadOnlySpan<byte> packet, long nowTicks, out long rttTicks)
    {
        rttTicks = 0;
        if (!PacketPool.TryParseHeader(packet, out ushort id, out _) || id != PongPacket.Id)
            return false;
        var pong = Serializer.Deserialize<PongPacket>(packet);
        rttTicks = nowTicks - pong.ClientTicks;
        return true;
    }
}
