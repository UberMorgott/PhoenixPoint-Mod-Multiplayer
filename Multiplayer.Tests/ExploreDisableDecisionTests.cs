using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// Pure decision behind the DELIBERATE "grey the Explore button while exploring" UX. Pins the source-selection:
/// an active CLIENT reads the mirrored 0xA7 flag; the HOST / single-player reads the native IsExploringSite — and
/// the OTHER side's value is ignored so a stale cross-source reading can never leak in. The game-bound reflection
/// that produces the two booleans + writes the disabled state lives in ExploreButtonDisablePatch (not unit-testable).
/// </summary>
public class ExploreDisableDecisionTests
{
    [Theory]
    // onActiveClient, hostNativeExploring, clientMirrorExploring, expectedDisable
    [InlineData(false, true,  false, true)]   // HOST exploring → disable (native source)
    [InlineData(false, false, false, false)]  // HOST not exploring → enabled
    [InlineData(false, false, true,  false)]  // HOST: client-mirror value IGNORED (host uses native)
    [InlineData(true,  false, true,  true)]   // CLIENT exploring (mirror) → disable
    [InlineData(true,  false, false, false)]  // CLIENT not exploring → enabled
    [InlineData(true,  true,  false, false)]  // CLIENT: host-native value IGNORED (client uses mirror)
    public void ShouldDisable_SelectsAuthoritativeSourcePerSide(
        bool onActiveClient, bool hostNativeExploring, bool clientMirrorExploring, bool expected)
        => Assert.Equal(expected,
            ExploreDisableDecision.ShouldDisable(onActiveClient, hostNativeExploring, clientMirrorExploring));
}
