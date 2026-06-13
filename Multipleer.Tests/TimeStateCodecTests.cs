using Multipleer.Network.CommandSync;
using Xunit;

public class TimeStateCodecTests
{
    [Fact]
    public void TimeStatePayload_RoundTrips()
    {
        var src = new TimeStatePayload
        {
            Paused = true,
            Scale = 1500f,
            StartTimeTicks = 123456789L,
            StartFixedTicks = 222L,
            OwnNowTicks = 987654321L,
            OwnFixedTicks = 333L
        };
        var back = CommandCodec.DecodeTimeState(CommandCodec.EncodeTimeState(src));
        Assert.True(back.Paused);
        Assert.Equal(1500f, back.Scale);
        Assert.Equal(123456789L, back.StartTimeTicks);
        Assert.Equal(222L, back.StartFixedTicks);
        Assert.Equal(987654321L, back.OwnNowTicks);
        Assert.Equal(333L, back.OwnFixedTicks);
    }
}
