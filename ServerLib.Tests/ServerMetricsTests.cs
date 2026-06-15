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
}
