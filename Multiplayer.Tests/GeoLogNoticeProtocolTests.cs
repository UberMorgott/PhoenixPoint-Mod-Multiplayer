using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// GeoLogNotice (0x6D) wire codec — the host ships each geoscape-log toast as a pre-resolved line + priority flag
/// so the frozen-sim client (whose native GeoscapeLog handlers never fire) can mirror it.
/// </summary>
public class GeoLogNoticeProtocolTests
{
    [Fact]
    public void GeoLogNotice_RoundTrips_HighPriority()
    {
        var bytes = SyncProtocol.EncodeGeoLogNotice("Phoenix Base destroyed by Pandorans", true);
        Assert.True(SyncProtocol.TryDecodeGeoLogNotice(bytes, out var text, out var high));
        Assert.Equal("Phoenix Base destroyed by Pandorans", text);
        Assert.True(high);
    }

    [Fact]
    public void GeoLogNotice_RoundTrips_LowPriority()
    {
        var bytes = SyncProtocol.EncodeGeoLogNotice("Research complete: Advanced Ballistics", false);
        Assert.True(SyncProtocol.TryDecodeGeoLogNotice(bytes, out var text, out var high));
        Assert.Equal("Research complete: Advanced Ballistics", text);
        Assert.False(high);
    }

    [Fact]
    public void GeoLogNotice_EmptyText_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeGeoLogNotice("", false);
        Assert.True(SyncProtocol.TryDecodeGeoLogNotice(bytes, out var text, out var high));
        Assert.Equal("", text);
        Assert.False(high);
    }

    [Fact]
    public void GeoLogNotice_UnicodePreserved()
    {
        var bytes = SyncProtocol.EncodeGeoLogNotice("Гавань уничтожена где-то", true);
        Assert.True(SyncProtocol.TryDecodeGeoLogNotice(bytes, out var text, out var high));
        Assert.Equal("Гавань уничтожена где-то", text);
        Assert.True(high);
    }

    [Fact]
    public void GeoLogNotice_GarbageBytes_FailsGracefully()
    {
        Assert.False(SyncProtocol.TryDecodeGeoLogNotice(new byte[] { 0x01 }, out _, out _)); // truncated string len
    }
}
