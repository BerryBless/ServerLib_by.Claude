using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace SoakTest.Workloads;

/// <summary>
/// DamagePacket 반복 송신 워크로드입니다.
/// 기존 <see cref="SoakClient"/> 내의 DamagePacket 로직을 동작 무변경으로 추출합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe.
/// 각 <see cref="SoakClient"/> 인스턴스는 고유한 인스턴스를 소유합니다.
/// <b>[Memory:]</b> DamagePacket 버퍼는 생성자에서 1회 직렬화 후 모든 사이클에서 재사용합니다(무할당).
/// <c>SendAsync(ReadOnlyMemory&lt;byte&gt;)</c>는 내부 Pipe에 write만 하므로 버퍼 소유권은 호출자 유지 가능.
/// <b>[Blocking:]</b> Non-blocking. SendAsync는 Pipe write 후 즉시 반환합니다.
/// </remarks>
internal sealed class DamageWorkload : IWorkload
{
    private readonly int       _sendsPerConn;
    private readonly int       _receiveSettleMs;
    private readonly SoakStats _stats;

    // ReadOnlyMemory<byte>: 1회 직렬화한 DamagePacket(8B = 4B헤더+4B body) 버퍼를 모든 사이클에서 참조 재사용.
    // SendAsync 내부 Pipe 쓰기는 동기적이라 버퍼 생명 주기가 호출 범위 내 보장됨.
    private readonly ReadOnlyMemory<byte> _dmgBuf;

    /// <summary>DamageWorkload를 초기화합니다. DamagePacket 버퍼를 1회만 직렬화합니다.</summary>
    /// <param name="sendsPerConn">연결당 DamagePacket 송신 횟수입니다.</param>
    /// <param name="receiveSettleMs">서버 응답(MobHpPacket) 수신 여유 시간(밀리초)입니다.</param>
    /// <param name="stats">공유 lock-free 집계 카운터입니다.</param>
    public DamageWorkload(int sendsPerConn, int receiveSettleMs, SoakStats stats)
    {
        _sendsPerConn    = sendsPerConn;
        _receiveSettleMs = receiveSettleMs;
        _stats           = stats;

        // BinaryPacketSerializer: Thread-safe — 내부 상태 없음. 생성자에서 1회만 사용.
        var serializer = new BinaryPacketSerializer();
        var pkt        = new DamagePacket { Amount = 100 };
        int sz         = PacketPool.HeaderSize + pkt.GetBodySize(); // 4(헤더) + 4(int) = 8B
        var buf        = new byte[sz];
        serializer.Serialize(pkt, buf); // 제네릭 — 구조체 박싱 없음
        _dmgBuf = buf.AsMemory();
    }

    /// <inheritdoc/>
    public async Task RunCycleAsync(IClientConnection conn, int cycleIndex, CancellationToken ct)
    {
        for (int k = 0; k < _sendsPerConn; k++)
        {
            // SendAsync(ReadOnlyMemory<byte>): Pipe에 write 후 즉시 반환(non-blocking 쓰기)
            // _dmgBuf를 재사용: SendAsync가 반환하기 전에 버퍼를 사용하므로 안전(동기 Pipe 쓰기)
            await conn.SendAsync(_dmgBuf, ct);
            _stats.IncSent();
        }

        // 서버가 연결 직후 MobHpPacket을 브로드캐스트하므로, 수신 여유 시간을 두어 recv 카운트 반영
        if (_receiveSettleMs > 0)
            await Task.Delay(_receiveSettleMs, ct);
    }
}
