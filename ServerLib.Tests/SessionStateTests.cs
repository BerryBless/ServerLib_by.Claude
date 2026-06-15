using Xunit;
using ServerLib.Interface;

namespace ServerLib.Tests;

public class SessionStateTests
{
    [Fact]
    public void Predefined_values_have_correct_integer_codes()
    {
        Assert.Equal(0, SessionState.Connecting.Value);
        Assert.Equal(1, SessionState.Connected.Value);
        Assert.Equal(2, SessionState.Authenticated.Value);
        Assert.Equal(3, SessionState.Disconnecting.Value);
        Assert.Equal(4, SessionState.Disconnected.Value);
    }

    [Fact]
    public void Equality_operator_returns_true_for_same_value()
    {
        Assert.True(SessionState.Connected == new SessionState(1));
    }

    [Fact]
    public void Inequality_operator_returns_true_for_different_values()
    {
        Assert.True(SessionState.Connected != SessionState.Disconnected);
    }

    [Fact]
    public void Equals_method_matches_operator()
    {
        var a = SessionState.Connected;
        var b = new SessionState(1);
        var c = SessionState.Disconnected;

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a.Equals(c));
        Assert.False(a == c);
    }

    [Fact]
    public void GetHashCode_equals_value()
    {
        Assert.Equal(1, SessionState.Connected.GetHashCode());
    }

    [Fact]
    public void ToString_returns_named_predefined()
    {
        Assert.Equal("Connecting", SessionState.Connecting.ToString());
        Assert.Equal("Connected", SessionState.Connected.ToString());
        Assert.Equal("Authenticated", SessionState.Authenticated.ToString());
        Assert.Equal("Disconnecting", SessionState.Disconnecting.ToString());
        Assert.Equal("Disconnected", SessionState.Disconnected.ToString());
    }

    [Fact]
    public void ToString_returns_Custom_format_for_custom_values()
    {
        var custom = SessionState.Custom(10);
        Assert.Equal("Custom(10)", custom.ToString());
    }

    [Fact]
    public void Custom_below_or_equal_reserved_max_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionState.Custom(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionState.Custom(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionState.Custom(-1));
    }

    [Fact]
    public void Custom_above_reserved_max_succeeds()
    {
        var custom = SessionState.Custom(5);
        Assert.Equal(5, custom.Value);
    }
}
