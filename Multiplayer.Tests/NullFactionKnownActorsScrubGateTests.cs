using Multiplayer.Harmony.Tactical;
using Xunit;

// Pure gate behind NullFactionKnownActorsScrubPatch (host deploy-snapshot null-faction guard). The live
// Harmony prefix scrubs null-faction KnownActors entries via reflection (in-game verified), but the decision
// ("should we scrub before the native TacticalFactionVision.RecordInstanceData runs?") is a pure predicate:
// scrub only inside an active synced session; outside a session leave the native untouched.
public class NullFactionKnownActorsScrubGateTests
{
    [Fact]
    public void NoSession_DoesNotScrub()
    {
        // Single-player / no active session → native RecordInstanceData runs untouched (byte-identical).
        Assert.False(NullFactionKnownActorsScrubGate.ShouldScrub(inActiveSession: false));
    }

    [Fact]
    public void ActiveSession_Scrubs()
    {
        // Active synced session → scrub phantom null-faction known-actors before the native throw site
        // (TacticalFactionVision.RecordInstanceData) so the host deploy snapshot captures + broadcasts.
        Assert.True(NullFactionKnownActorsScrubGate.ShouldScrub(inActiveSession: true));
    }
}
