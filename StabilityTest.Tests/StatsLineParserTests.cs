using StabilityTest;
using Xunit;

namespace StabilityTest.Tests;

public class StatsLineParserTests
{
    [Fact]
    public void TryParse_valid_line_extracts_all_fields()
    {
        var line = "[STATS] received=12345 test=-7 sessions=42 heapBytes=987654 gen2=3";
        Assert.True(StatsLineParser.TryParse(line, out var s));
        Assert.Equal(12345, s.Received);
        Assert.Equal(-7, s.Test);
        Assert.Equal(42, s.Sessions);
        Assert.Equal(987654, s.HeapBytes);
        Assert.Equal(3, s.Gen2);
    }

    [Fact]
    public void TryParse_line_with_leading_noise_still_parses()
    {
        var line = "garbage [STATS] received=1 test=2 sessions=3 heapBytes=4 gen2=5";
        Assert.True(StatsLineParser.TryParse(line, out var s));
        Assert.Equal(1, s.Received);
        Assert.Equal(5, s.Gen2);
    }

    [Theory]
    [InlineData("[Monitor] sessions=0 packets/1s=0")]
    [InlineData("[STATS] received=1 test=2")]
    [InlineData("[STATS] received=abc test=2 sessions=3 heapBytes=4 gen2=5")]
    [InlineData("")]
    public void TryParse_invalid_line_returns_false(string line)
    {
        Assert.False(StatsLineParser.TryParse(line, out _));
    }
}
