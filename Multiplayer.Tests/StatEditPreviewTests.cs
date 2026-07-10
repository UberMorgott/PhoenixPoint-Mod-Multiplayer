using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

// Display-only stat-edit preview (co-op reactivity): the spender ships its uncommitted +/- buffer as a cosmetic
// broadcast (PacketType.StatEditPreview 0x6E); a watcher on the SAME soldier writes it into the panel labels
// only. These cover the pure wire codec round-trip + the watcher show/skip gate.
public class StatEditPreviewTests
{
    [Fact]
    public void Preview_RoundTrips_ShowWithSignedDeltas()
    {
        var bytes = SyncProtocol.EncodeStatEditPreview(unitId: 5, dStr: 2, dWill: -1, dSpeed: 0,
            soldierSP: 3, factionSP: 7, clear: false);
        Assert.True(SyncProtocol.TryDecodeStatEditPreview(bytes, out var p));
        Assert.Equal(5, p.UnitId);
        Assert.Equal(2, p.DStr);
        Assert.Equal(-1, p.DWill);
        Assert.Equal(0, p.DSpeed);
        Assert.Equal(3, p.SoldierSP);
        Assert.Equal(7, p.FactionSP);
        Assert.False(p.Clear);
    }

    [Fact]
    public void Preview_RoundTrips_ClearFlag()
    {
        var bytes = SyncProtocol.EncodeStatEditPreview(42, 0, 0, 0, 0, 0, clear: true);
        Assert.True(SyncProtocol.TryDecodeStatEditPreview(bytes, out var p));
        Assert.Equal(42, p.UnitId);
        Assert.True(p.Clear);
    }

    [Fact]
    public void Preview_GarbageBytes_FailGracefully()
    {
        Assert.False(SyncProtocol.TryDecodeStatEditPreview(new byte[] { 0x00, 0x01 }, out _)); // truncated
    }

    // ── watcher show/skip gate ──

    [Fact]
    public void Show_WhenPanelOpenOnSameUnit_NoLocalEdit()
    {
        Assert.True(StatEditPreviewDecision.ShouldShowOnWatcher(panelOpen: true, openUnitId: 5, previewUnitId: 5, watcherHasLocalEdit: false));
    }

    [Fact]
    public void Skip_WhenPanelClosed()
    {
        Assert.False(StatEditPreviewDecision.ShouldShowOnWatcher(false, 5, 5, false));
    }

    [Fact]
    public void Skip_WhenDifferentUnitOpen()
    {
        Assert.False(StatEditPreviewDecision.ShouldShowOnWatcher(true, 6, 5, false));
    }

    [Fact]
    public void Skip_WhenWatcherHasOwnLocalEdit()
    {
        // Never clobber a local editor's live buffer with a remote peer's preview.
        Assert.False(StatEditPreviewDecision.ShouldShowOnWatcher(true, 5, 5, true));
    }

    [Fact]
    public void Skip_WhenUnresolvedOpenUnit()
    {
        Assert.False(StatEditPreviewDecision.ShouldShowOnWatcher(true, 0, 0, false));
    }
}
