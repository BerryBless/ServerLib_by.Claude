using DbPerfTest;
using Xunit;

namespace DbPerfTest.Tests;

public class LatencyRecorderTests
{
    [Fact]
    public void RecordWrite_SingleValue_P50P95P99AllSame()
    {
        var r = new LatencyRecorder();
        r.RecordWrite(1000L);
        var p = r.GetWritePercentiles();
        Assert.Equal(1L, p.Count);
        Assert.Equal(1000L, p.P50);
        Assert.Equal(1000L, p.P95);
        Assert.Equal(1000L, p.P99);
        Assert.Equal(1000L, p.Max);
    }

    [Fact]
    public void RecordRead_MultipleValues_CorrectPercentiles()
    {
        var r = new LatencyRecorder();
        // 100개 값: 1~100
        for (int i = 1; i <= 100; i++) r.RecordRead((long)i);
        var p = r.GetReadPercentiles();
        Assert.Equal(100L, p.Count);
        Assert.Equal(50L, p.P50);  // ceil(0.50*100)-1 = 49 → sorted[49] = 50
        Assert.Equal(95L, p.P95);  // ceil(0.95*100)-1 = 94 → sorted[94] = 95
        Assert.Equal(99L, p.P99);  // ceil(0.99*100)-1 = 98 → sorted[98] = 99
        Assert.Equal(100L, p.Max);
    }

    [Fact]
    public void GetPercentiles_Empty_ReturnsDefaultZero()
    {
        var r = new LatencyRecorder();
        var w = r.GetWritePercentiles();
        var rd = r.GetReadPercentiles();
        Assert.Equal(0L, w.Count);
        Assert.Equal(0L, rd.Count);
    }

    [Fact]
    public void WriteCount_ReadCount_AreIndependent()
    {
        var r = new LatencyRecorder();
        r.RecordWrite(100L);
        r.RecordWrite(200L);
        r.RecordRead(50L);
        Assert.Equal(2L, r.WriteCount);
        Assert.Equal(1L, r.ReadCount);
    }

    [Fact]
    public void WriteAndRead_DoNotCrossContaminate()
    {
        var r = new LatencyRecorder();
        r.RecordWrite(9999L);
        r.RecordRead(1L);
        Assert.Equal(1L, r.GetReadPercentiles().Max);
        Assert.Equal(9999L, r.GetWritePercentiles().Max);
    }
}
