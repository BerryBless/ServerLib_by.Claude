using Server.Auth;
using Xunit;

namespace ServerLib.Tests;

public class DbMetricsTests
{
    [Fact]
    public void RecordMysqlSelect_IncrementsCountAndAccumulates()
    {
        var m = new DbMetrics();
        m.RecordMysqlSelect(100L);
        m.RecordMysqlSelect(200L);
        var s = m.GetSnapshot();
        Assert.Equal(2, s.MysqlCount);
        Assert.Equal(150L, s.MysqlSelectAvgUs); // (100+200)/2
    }

    [Fact]
    public void RecordRedisSet_IncrementsCountAndAccumulates()
    {
        var m = new DbMetrics();
        m.RecordRedisSet(50L);
        var s = m.GetSnapshot();
        Assert.Equal(1, s.RedisSetCount);
        Assert.Equal(50L, s.RedisSetAvgUs);
    }

    [Fact]
    public void RecordRedisGet_IncrementsCountAndAccumulates()
    {
        var m = new DbMetrics();
        m.RecordRedisGet(10L);
        m.RecordRedisGet(20L);
        m.RecordRedisGet(30L);
        var s = m.GetSnapshot();
        Assert.Equal(3, s.RedisGetCount);
        Assert.Equal(20L, s.RedisGetAvgUs); // (10+20+30)/3
    }

    [Fact]
    public void GetSnapshot_ZeroCounts_ReturnsZeroAverages()
    {
        var m = new DbMetrics();
        var s = m.GetSnapshot();
        Assert.Equal(0, s.MysqlSelectAvgUs);
        Assert.Equal(0, s.RedisSetAvgUs);
        Assert.Equal(0, s.RedisGetAvgUs);
    }
}
