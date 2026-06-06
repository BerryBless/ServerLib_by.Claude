using ServerLib.Core.Memory;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Core.Transport;

namespace StabilityTest;

/// <summary>
/// 데이터유실·손상 검증용 신뢰 클라이언트. 라이브러리 <see cref="SocketPipelineClient"/>를 사용하며
/// 반드시 graceful(FIN)로 종료하여 보낸 모든 패킷이 서버에 도달함을 보장합니다.
/// </summary>
public sealed class ReliableClient : IAsyncDisposable
{
    // SocketPipelineClient: 라이브러리 클라이언트(C2 dispose 경로 포함)도 SUT로 검증하기 위해 사용
    private readonly SocketPipelineClient _client = new();
    // 본문 없는 4바이트 패킷을 1회 빌드해 재사용 — 송신마다 직렬화/할당 회피
    private readonly byte[] _inc = new byte[PacketPool.HeaderSize];
    private readonly byte[] _dec = new byte[PacketPool.HeaderSize];

    public long SentInc { get; private set; }
    public long SentDec { get; private set; }
    public long SentTotal => SentInc + SentDec;

    public ReliableClient()
    {
        PacketPool.WriteHeader(_inc, IncrementPacket.Id, 0);
        PacketPool.WriteHeader(_dec, DecrementPacket.Id, 0);
    }

    public Task ConnectAsync(string host, int port, CancellationToken ct)
        => _client.ConnectAsync(host, port, ct);

    /// <summary><paramref name="count"/>개 패킷을 연속 송신합니다. 짝수 인덱스=increment, 홀수=decrement.</summary>
    /// <remarks>합쳐진(coalesced) 소형 패킷 스트림으로 서버 파이프라인 프레이밍을 자극합니다.</remarks>
    public async Task SendBurstAsync(int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            if ((i & 1) == 0) { await _client.SendAsync(_inc, ct); SentInc++; }
            else { await _client.SendAsync(_dec, ct); SentDec++; }
        }
    }

    // graceful 종료: DisposeAsync가 _cts 취소 후 소켓 Dispose → 큐된 데이터 전송 후 FIN.
    // 매 SendAsync를 await했으므로 종료 시점에 모든 바이트가 커널 송신 버퍼에 있어 FIN 이전에 도달한다.
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
