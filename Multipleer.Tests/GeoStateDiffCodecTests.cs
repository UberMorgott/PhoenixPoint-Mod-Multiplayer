using Multipleer.Network.CommandSync;
using Xunit;

public class GeoStateDiffCodecTests
{
    [Fact]
    public void ChangedMaskBits_AreStable()
    {
        // changedMask bit values are serialized on the wire (0x35 GeoStateDiff, scope Vehicle) — never renumber.
        Assert.Equal(1, GeoStateMask.SurfacePos);
        Assert.Equal(2, GeoStateMask.SurfaceRot);
        Assert.Equal(4, GeoStateMask.RangeRemaining);
        Assert.Equal(8, GeoStateMask.Travelling);
        Assert.Equal(16, GeoStateMask.CurrentSite);
        Assert.Equal(32, GeoStateMask.DestinationSites);
        Assert.Equal(64, GeoStateMask.HitPoints);
    }

    [Fact]
    public void DefaultVehicleStateRecord_HasZeroChangedMask()
    {
        var r = new GeoVehicleStateRecord();
        Assert.Equal(0, r.ChangedMask);
    }
}
