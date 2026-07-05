using Multiplayer.Network.Sync.State;
using Xunit;

// Host stale-Explore-button fix: pure decision for whether the host hides its open POI context menu after applying
// a client-relayed explore (see GeoSiteContextMenuDecision). Hides ONLY when the menu is open on the explored site.
public class GeoSiteContextMenuDecisionTests
{
    [Fact]
    public void Hides_WhenVisibleAndSameSite()
        => Assert.True(GeoSiteContextMenuDecision.ShouldHide(menuVisible: true, selectedSiteId: 7, exploredSiteId: 7));

    [Fact]
    public void NoHide_WhenMenuHidden()
        => Assert.False(GeoSiteContextMenuDecision.ShouldHide(menuVisible: false, selectedSiteId: 7, exploredSiteId: 7));

    [Fact]
    public void NoHide_WhenDifferentSite()
        => Assert.False(GeoSiteContextMenuDecision.ShouldHide(menuVisible: true, selectedSiteId: 3, exploredSiteId: 7));

    [Fact]
    public void NoHide_WhenExploredSiteInvalid()
        => Assert.False(GeoSiteContextMenuDecision.ShouldHide(menuVisible: true, selectedSiteId: -1, exploredSiteId: -1));

    [Fact]
    public void NoHide_WhenExploredSiteInvalidButSelectedValid()
        => Assert.False(GeoSiteContextMenuDecision.ShouldHide(menuVisible: true, selectedSiteId: 5, exploredSiteId: -1));
}
