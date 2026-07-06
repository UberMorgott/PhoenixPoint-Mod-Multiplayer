using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Batch-4 §P8 marketplace fix: the client's event-rail rebuild must select
/// <c>UIStateMarketplaceGeoscapeEvent</c> (the host's own state-class branch,
/// GeoscapeView.OnGeoscapeEventRaised:2046-2049) instead of the plain <c>UIStateGeoscapeEvent</c>
/// when the raised event IS the marketplace event. Only the pure selection gate is unit-testable;
/// the ctor pick in <see cref="EventDisplay"/>.Show binds live game types.
/// </summary>
public class MarketplaceStateSelectionTests
{
    private const string MarketId = "TheMarketplaceEvent";

    [Fact]
    public void MarketplaceEvent_SelectsMarketplaceState()
        => Assert.True(EventDisplay.ShouldUseMarketplaceState(MarketId, MarketId));

    [Fact]
    public void OrdinaryEvent_KeepsPlainEventState()
        => Assert.False(EventDisplay.ShouldUseMarketplaceState("PROG_FS2_WIN", MarketId));

    [Fact]
    public void SyntheticResultPage_EmptyEventId_KeepsPlainEventState()
        // Result pages ride Show with EventID == "" — never the marketplace state.
        => Assert.False(EventDisplay.ShouldUseMarketplaceState("", MarketId));

    [Fact]
    public void NullEventId_KeepsPlainEventState()
        => Assert.False(EventDisplay.ShouldUseMarketplaceState(null, MarketId));

    [Fact]
    public void UnresolvedMarketplaceId_FailsOpen_ToPlainEventState()
    {
        // DLC absent / mid-load level → no marketplace id resolvable → never guess.
        Assert.False(EventDisplay.ShouldUseMarketplaceState(MarketId, null));
        Assert.False(EventDisplay.ShouldUseMarketplaceState(MarketId, ""));
    }

    [Fact]
    public void Comparison_IsOrdinal_CaseSensitive()
        => Assert.False(EventDisplay.ShouldUseMarketplaceState("themarketplaceevent", MarketId));
}
