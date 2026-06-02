namespace ServerLib.Core;

// 모든 카운터는 Interlocked 전용 — lock 없음
public sealed class ServerMetrics
{
    private long _connectedCount;
    private long _totalPacketsReceived;
    private long _totalBytesSent;
    private long _totalBytesReceived;

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
