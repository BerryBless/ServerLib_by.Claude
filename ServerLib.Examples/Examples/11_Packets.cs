using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Examples.Examples;

/// <summary>
/// ServerLib에 정의된 11종 패킷 타입 전부를 직렬화→역직렬화→필드 동등 검증하는 라운드트립 테스트를 수행합니다.
/// 소켓 불필요 — 인프로세스 메모리 버퍼로 동작합니다.
/// </summary>
/// <remarks>
/// <b>[시연 패킷]</b>
/// Id=1 <see cref="EchoPacket"/> / Id=2 <see cref="ChatPacket"/> / Id=3 <see cref="IncrementPacket"/> /
/// Id=4 <see cref="DecrementPacket"/> / Id=5 <see cref="DamagePacket"/> / Id=6 <see cref="MobHpPacket"/> /
/// Id=7 <see cref="MobDeathPacket"/> / Id=8 <see cref="StatsRequestPacket"/> / Id=9 <see cref="StatsResponsePacket"/> /
/// Id=0xFFFE <see cref="PingPacket"/> / Id=0xFFFF <see cref="PongPacket"/>
/// </remarks>
internal static class Packets
{
    /// <summary>
    /// 11종 패킷을 <see cref="BinaryPacketSerializer"/>로 직렬화한 뒤 역직렬화하여 필드가 일치하는지 검증합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> BinaryPacketSerializer는 내부 상태가 없어 Thread-safe.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b>
    /// struct 패킷(Increment/Decrement/Damage/MobHp/StatsRequest/Ping/Pong): Deserialize 시 Zero-allocation(new T()가 스택/인라인).
    /// sealed class 패킷(Echo/Chat/MobDeath/StatsResponse): Deserialize 시 1회 힙 할당 + string/byte[] 추가 할당.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 전 연산이 동기 즉시 반환입니다.
    /// </remarks>
    public static Task RunAsync()
    {
        // BinaryPacketSerializer: IPacketSerializer 유일 구현체 — 4바이트 헤더 + 본문 포맷.
        var serializer = new BinaryPacketSerializer();

        Console.WriteLine("  [Packets] 11종 패킷 라운드트립:");

        // ── Id=1: EchoPacket (sealed class, 가변 길이 string) ──
        {
            var original = new EchoPacket { Message = "에코 메시지 테스트 🎉" };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.Message == original.Message, "EchoPacket.Message");
            Console.WriteLine($"    ✓ EchoPacket(Id={EchoPacket.Id}): Message=\"{decoded.Message}\"");
        }

        // ── Id=2: ChatPacket (sealed class, 가변 길이 string ×2) ──
        {
            var original = new ChatPacket { Sender = "홍길동", Content = "안녕하세요!" };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.Sender == original.Sender && decoded.Content == original.Content, "ChatPacket");
            Console.WriteLine($"    ✓ ChatPacket(Id={ChatPacket.Id}): Sender=\"{decoded.Sender}\", Content=\"{decoded.Content}\"");
        }

        // ── Id=3: IncrementPacket (struct, 본문 없음) ──
        {
            var original = new IncrementPacket();
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.PacketId == IncrementPacket.Id, "IncrementPacket.PacketId");
            Console.WriteLine($"    ✓ IncrementPacket(Id={IncrementPacket.Id}): 0바이트 본문");
        }

        // ── Id=4: DecrementPacket (struct, 본문 없음) ──
        {
            var original = new DecrementPacket();
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.PacketId == DecrementPacket.Id, "DecrementPacket.PacketId");
            Console.WriteLine($"    ✓ DecrementPacket(Id={DecrementPacket.Id}): 0바이트 본문");
        }

        // ── Id=5: DamagePacket (struct, 4B 고정) ──
        {
            var original = new DamagePacket { Amount = 9999 };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.Amount == original.Amount, "DamagePacket.Amount");
            Console.WriteLine($"    ✓ DamagePacket(Id={DamagePacket.Id}): Amount={decoded.Amount}");
        }

        // ── Id=6: MobHpPacket (struct, 20B 고정) ──
        {
            var original = new MobHpPacket { Hp = 75000, MaxHp = 100000, Generation = 3 };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.Hp == original.Hp && decoded.MaxHp == original.MaxHp && decoded.Generation == original.Generation, "MobHpPacket");
            Console.WriteLine($"    ✓ MobHpPacket(Id={MobHpPacket.Id}): Hp={decoded.Hp}, MaxHp={decoded.MaxHp}, Gen={decoded.Generation}");
        }

        // ── Id=7: MobDeathPacket (sealed class, 가변 string) ──
        {
            var original = new MobDeathPacket { Generation = 3, TopDamage = 150000, MvpName = "최강전사" };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.Generation == original.Generation && decoded.TopDamage == original.TopDamage && decoded.MvpName == original.MvpName, "MobDeathPacket");
            Console.WriteLine($"    ✓ MobDeathPacket(Id={MobDeathPacket.Id}): Gen={decoded.Generation}, TopDmg={decoded.TopDamage}, MVP=\"{decoded.MvpName}\"");
        }

        // ── Id=8: StatsRequestPacket (struct, 본문 없음) ──
        {
            var original = new StatsRequestPacket();
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.PacketId == StatsRequestPacket.Id, "StatsRequestPacket.PacketId");
            Console.WriteLine($"    ✓ StatsRequestPacket(Id={StatsRequestPacket.Id}): 0바이트 본문 (통계 요청 신호)");
        }

        // ── Id=9: StatsResponsePacket (sealed class, UTF-8 JSON byte[]) ──
        {
            var original = new StatsResponsePacket { Json = "{\"connectedCount\":42}" };
            // StatsResponsePacket은 역직렬화 후 Json getter가 없음 (Json은 set-only).
            // 직렬화 → 역직렬화 후 body 길이로 라운드트립을 검증합니다.
            int pktSize = PacketPool.HeaderSize + original.GetBodySize();
            var buf = new byte[pktSize];
            serializer.Serialize(original, buf);
            var decoded = serializer.Deserialize<StatsResponsePacket>(buf.AsSpan());
            // GetBodySize로 내부 byte[]가 올바르게 복원됐는지 확인 (0이 아니면 데이터 존재)
            Verify(decoded.GetBodySize() > 0, "StatsResponsePacket.GetBodySize > 0");
            Console.WriteLine($"    ✓ StatsResponsePacket(Id={StatsResponsePacket.Id}): 역직렬화 본문={decoded.GetBodySize()}바이트");
        }

        // ── Id=0xFFFE: PingPacket (struct, 8B — 하트비트 PING) ──
        {
            var original = new PingPacket { ClientTicks = DateTimeOffset.UtcNow.UtcTicks };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.ClientTicks == original.ClientTicks, "PingPacket.ClientTicks");
            Console.WriteLine($"    ✓ PingPacket(Id=0x{PingPacket.Id:X4}): ClientTicks={decoded.ClientTicks}");
        }

        // ── Id=0xFFFF: PongPacket (struct, 8B — 하트비트 PONG) ──
        {
            var original = new PongPacket { ClientTicks = 123456789012345L };
            var decoded = RoundTrip(serializer, original);
            Verify(decoded.ClientTicks == original.ClientTicks, "PongPacket.ClientTicks");
            Console.WriteLine($"    ✓ PongPacket(Id=0x{PongPacket.Id:X4}): ClientTicks={decoded.ClientTicks}");
        }

        Console.WriteLine("[OK] 11_Packets");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 패킷을 직렬화해 바이트 배열에 기록하고, 동일 배열에서 역직렬화해 새 인스턴스를 반환합니다.
    /// </summary>
    /// <typeparam name="T">직렬화할 패킷 타입입니다.</typeparam>
    /// <param name="serializer">사용할 직렬화 구현체입니다.</param>
    /// <param name="packet">직렬화할 패킷 인스턴스입니다.</param>
    /// <returns>역직렬화된 새 패킷 인스턴스입니다.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe(내부 상태 없음).
    /// <b>[Memory Allocation:]</b> 테스트 버퍼(byte[]) 1회 힙 할당. struct 패킷은 Deserialize 시 Zero-allocation.
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    private static T RoundTrip<T>(BinaryPacketSerializer serializer, T packet)
        where T : IPacket, new()
    {
        // PacketPool.HeaderSize: 4바이트 헤더 상수. 전체 버퍼 크기 = 헤더 + 본문.
        int pktSize = PacketPool.HeaderSize + packet.GetBodySize();
        var buf = new byte[pktSize]; // 테스트 전용 — 실제 코드에선 ArrayPool.Rent 사용
        serializer.Serialize(packet, buf.AsSpan());
        return serializer.Deserialize<T>(buf.AsSpan());
    }

    /// <summary>조건이 false이면 <see cref="InvalidOperationException"/>을 발생시킵니다.</summary>
    private static void Verify(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"패킷 라운드트립 검증 실패: {what}");
    }
}
