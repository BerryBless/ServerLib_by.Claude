using Xunit;
using ServerLib.Core;

namespace ServerLib.Tests;

public class ServerMetricsTests
{
    [Fact]
    public void Initial_state_all_counters_are_zero()
    {
        var metrics = new ServerMetrics();

        Assert.Equal(0, metrics.ConnectedCount);
        Assert.Equal(0, metrics.TotalPacketsReceived);
        Assert.Equal(0, metrics.TotalBytesSent);
        Assert.Equal(0, metrics.TotalBytesReceived);
    }

    [Fact]
    public void OnClientConnected_increments_ConnectedCount()
    {
        var metrics = new ServerMetrics();
        metrics.OnClientConnected();

        Assert.Equal(1, metrics.ConnectedCount);
    }

    [Fact]
    public void OnClientDisconnected_decrements_ConnectedCount()
    {
        var metrics = new ServerMetrics();
        metrics.OnClientConnected();
        metrics.OnClientDisconnected();

        Assert.Equal(0, metrics.ConnectedCount);
    }

    [Fact]
    public void OnPacketReceived_increments_counter()
    {
        var metrics = new ServerMetrics();
        metrics.OnPacketReceived();
        metrics.OnPacketReceived();
        metrics.OnPacketReceived();

        Assert.Equal(3, metrics.TotalPacketsReceived);
    }

    [Fact]
    public void OnBytesSent_accumulates()
    {
        var metrics = new ServerMetrics();
        metrics.OnBytesSent(100);
        metrics.OnBytesSent(200);

        Assert.Equal(300, metrics.TotalBytesSent);
    }

    [Fact]
    public void OnBytesReceived_accumulates()
    {
        var metrics = new ServerMetrics();
        metrics.OnBytesReceived(50);
        metrics.OnBytesReceived(75);

        Assert.Equal(125, metrics.TotalBytesReceived);
    }

    [Fact]
    public void Reset_clears_all_counters()
    {
        var metrics = new ServerMetrics();
        metrics.OnClientConnected();
        metrics.OnClientConnected();
        metrics.OnPacketReceived();
        metrics.OnBytesSent(500);
        metrics.OnBytesReceived(300);

        metrics.Reset();

        Assert.Equal(0, metrics.ConnectedCount);
        Assert.Equal(0, metrics.TotalPacketsReceived);
        Assert.Equal(0, metrics.TotalBytesSent);
        Assert.Equal(0, metrics.TotalBytesReceived);
    }

    // ── GAP-I-13: 음수 count 입력 — 가드 없음, 명세로 고정 ────────────────────────────────
    [Fact]
    public void OnBytesSent_negative_count_decrements_total()
    {
        // Interlocked.Add에 가드 없음 — 음수 값은 누적을 감소시킨다. 이 동작을 명세화.
        var metrics = new ServerMetrics();
        metrics.OnBytesSent(100);
        metrics.OnBytesSent(-30);
        Assert.Equal(70, metrics.TotalBytesSent);
    }

    [Fact]
    public void OnBytesReceived_negative_count_decrements_total()
    {
        var metrics = new ServerMetrics();
        metrics.OnBytesReceived(200);
        metrics.OnBytesReceived(-50);
        Assert.Equal(150, metrics.TotalBytesReceived);
    }

    // ── GAP-I-14: 동시성 카운터 정확성 ────────────────────────────────────────────────────
    [Fact]
    public void Concurrent_OnPacketReceived_CountIsAccurate()
    {
        // Interlocked.Increment: N개 Task 동시 호출 → 최종 카운터 == N
        var metrics = new ServerMetrics();
        const int N = 500;
        Parallel.For(0, N, _ => metrics.OnPacketReceived());
        Assert.Equal(N, metrics.TotalPacketsReceived);
    }
}
