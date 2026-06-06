using StabilityTest;
using Xunit;

namespace StabilityTest.Tests;

public class BurstSchedulerTests
{
    private static StabilityConfig Cfg(int seed) => new()
    {
        Seed = seed, BurstSeconds = 30,
        StormMinClients = 50, StormMaxClients = 500,
        SpikeMinPackets = 500, SpikeMaxPackets = 5000,
        GapMinMs = 200, GapMaxMs = 2000,
    };

    [Fact]
    public void Same_seed_produces_identical_timeline()
    {
        var a = new BurstScheduler(Cfg(42)).BuildTimeline();
        var b = new BurstScheduler(Cfg(42)).BuildTimeline();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_seed_produces_different_timeline()
    {
        var a = new BurstScheduler(Cfg(1)).BuildTimeline();
        var b = new BurstScheduler(Cfg(2)).BuildTimeline();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Timeline_is_nonempty_sorted_and_within_window()
    {
        var t = new BurstScheduler(Cfg(7)).BuildTimeline();
        Assert.NotEmpty(t);
        for (int i = 1; i < t.Count; i++)
            Assert.True(t[i].TimeOffsetMs >= t[i - 1].TimeOffsetMs, "오프셋은 비감소여야 함");
        Assert.All(t, e => Assert.True(e.TimeOffsetMs < 30_000, "오프셋은 폭주 구간 내"));
    }

    [Fact]
    public void Magnitudes_respect_configured_ranges()
    {
        var t = new BurstScheduler(Cfg(9)).BuildTimeline();
        Assert.All(t, e =>
        {
            if (e.Type == BurstEventType.ConnectionStorm)
                Assert.InRange(e.Magnitude, 50, 500);
            else
                Assert.InRange(e.Magnitude, 500, 5000);
        });
    }
}
