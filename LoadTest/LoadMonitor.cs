namespace LoadTest;

// 5초 주기로 연결 수 / TPS / 큐 잔여량을 콘솔에 출력
// 모든 카운터는 Interlocked — lock 없음
public sealed class LoadMonitor
{
    private long _connected;
    private long _packetsSentTotal;
    private long _packetsReceivedTotal;
    private long _packetsSentWindow;   // 5초 구간 카운터
    private long _packetsReceivedWindow;

    public void OnClientConnected() => Interlocked.Increment(ref _connected);
    public void OnClientDisconnected() => Interlocked.Decrement(ref _connected);
    public void OnPacketSent()
    {
        Interlocked.Increment(ref _packetsSentTotal);
        Interlocked.Increment(ref _packetsSentWindow);
    }
    public void OnPacketReceived()
    {
        Interlocked.Increment(ref _packetsReceivedTotal);
        Interlocked.Increment(ref _packetsReceivedWindow);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct).ContinueWith(_ => { });  // 취소 시 예외 방지

            var elapsed = DateTimeOffset.UtcNow - start;
            var sentTps = Interlocked.Exchange(ref _packetsSentWindow, 0) / 5;
            var recvTps = Interlocked.Exchange(ref _packetsReceivedWindow, 0) / 5;
            var connected = Interlocked.Read(ref _connected);
            var sentTotal = Interlocked.Read(ref _packetsSentTotal);

            Console.WriteLine(
                $"[{elapsed:hh\\:mm\\:ss}] " +
                $"Connections: {connected,6} | " +
                $"Send TPS: {sentTps,8:N0} | " +
                $"Recv TPS: {recvTps,8:N0} | " +
                $"Total Sent: {sentTotal,10:N0}");
        }
    }
}
