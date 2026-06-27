using DbPerfTest;
using Xunit;

namespace DbPerfTest.Tests;

public class DbPerfOptionsTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsDefaults()
    {
        var o = DbPerfOptions.Parse([]);
        Assert.Equal(20, o.Clients);
        Assert.Equal(30, o.DurationSeconds);
        Assert.Equal(5,  o.WarmupSeconds);
        Assert.Equal(80, o.ReadParts);
        Assert.Equal(20, o.WriteParts);
    }

    [Fact]
    public void Parse_ReadWriteRatio_ParsesCorrectly()
    {
        var o = DbPerfOptions.Parse(["--read-write-ratio", "95:5"]);
        Assert.Equal(95, o.ReadParts);
        Assert.Equal(5,  o.WriteParts);
    }

    [Fact]
    public void Parse_PresetReadHeavy_Sets95_5()
    {
        var o = DbPerfOptions.Parse(["--preset", "read-heavy"]);
        Assert.Equal(95, o.ReadParts);
        Assert.Equal(5,  o.WriteParts);
    }

    [Fact]
    public void Parse_PresetBalanced_Sets50_50()
    {
        var o = DbPerfOptions.Parse(["--preset", "balanced"]);
        Assert.Equal(50, o.ReadParts);
        Assert.Equal(50, o.WriteParts);
    }

    [Fact]
    public void IsReadOp_80_20_Ratio_ReturnsCorrectDistribution()
    {
        var o = DbPerfOptions.Parse(["--read-write-ratio", "80:20"]);
        int reads = 0, writes = 0;
        for (int i = 0; i < 100; i++)
        {
            if (o.IsReadOp(i)) reads++;
            else writes++;
        }
        Assert.Equal(80, reads);
        Assert.Equal(20, writes);
    }

    [Fact]
    public void Parse_TargetThroughput_ParsesCorrectly()
    {
        var o = DbPerfOptions.Parse(["--target-throughput", "500"]);
        Assert.Equal(500L, o.TargetThroughput);
    }
}
