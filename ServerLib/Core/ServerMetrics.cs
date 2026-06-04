namespace ServerLib.Core;

// 모든 카운터는 Interlocked 전용 — lock 없음
// long(값 타입) 필드 4개를 인라인 보유: 박싱·힙 할당 없는 카운터(Alloc 0). 다수 IO 스레드가 동시 갱신하지만
// Interlocked로 원자 연산하므로 Monitor 락의 컨텍스트 스위치 비용 없이 경합을 처리한다.
public sealed class ServerMetrics
{
    private long _connectedCount;
    private long _totalPacketsReceived;
    private long _totalBytesSent;
    private long _totalBytesReceived;

    // Interlocked.Read: 64-bit long은 32-bit 플랫폼에서 비원자적으로 찢겨 읽힐 수 있으므로, 단순 읽기여도 원자 읽기로 정합성 보장.
    public long ConnectedCount => Interlocked.Read(ref _connectedCount);
    public long TotalPacketsReceived => Interlocked.Read(ref _totalPacketsReceived);
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public void OnClientConnected() => Interlocked.Increment(ref _connectedCount);
    public void OnClientDisconnected() => Interlocked.Decrement(ref _connectedCount);
    public void OnPacketReceived() => Interlocked.Increment(ref _totalPacketsReceived);
    public void OnBytesSent(int count) => Interlocked.Add(ref _totalBytesSent, count);
    public void OnBytesReceived(int count) => Interlocked.Add(ref _totalBytesReceived, count);

    public void Reset()
    {
        Interlocked.Exchange(ref _connectedCount, 0);
        Interlocked.Exchange(ref _totalPacketsReceived, 0);
        Interlocked.Exchange(ref _totalBytesSent, 0);
        Interlocked.Exchange(ref _totalBytesReceived, 0);
    }
}
