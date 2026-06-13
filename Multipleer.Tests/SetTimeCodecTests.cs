using Multipleer.Network.CommandSync;
using Xunit;

public class SetTimeCodecTests
{
    [Fact]
    public void SetTimePayload_RoundTrips()
    {
        var src = new SetTimePayload { Paused = true, PresetIndex = 2 };
        var back = CommandCodec.DecodeSetTime(CommandCodec.EncodeSetTime(src));
        Assert.True(back.Paused);
        Assert.Equal(2, back.PresetIndex);
    }

    [Fact]
    public void SetTimePayload_Unpaused_ZeroIndex_RoundTrips()
    {
        var src = new SetTimePayload { Paused = false, PresetIndex = 0 };
        var back = CommandCodec.DecodeSetTime(CommandCodec.EncodeSetTime(src));
        Assert.False(back.Paused);
        Assert.Equal(0, back.PresetIndex);
    }
}
