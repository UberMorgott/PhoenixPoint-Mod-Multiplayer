using Multipleer.Network;
using Xunit;

// Inc4 S0: unit-tests the pure freeze-decision gate for the CLIENT geoscape sim-freeze. The Harmony wiring
// (ClientGeoSimFreezePatch postfix on GeoscapeEventSystem.OnLevelStart) is not unit-testable, but the
// decision — "should the client geoscape sim be frozen?" — is extracted into ClientSimFreeze.ShouldFreeze
// (mirroring ClientTftvAircraftFreezeGate) so the truth table is asserted directly. Freeze ONLY on:
// flag ON + active session + NOT host. Also pins the S0 invariant that the flag ships default-OFF (inert).
public class ClientSimFreezeGateTests
{
    [Fact]
    public void FlagOff_NeverFreezes_EvenActiveClient()
    {
        // Feature flag OFF (S0/default, and the flag-OFF rollback path): never freeze, even an active client.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: false, engineExists: true, isActive: true, isHost: false));
    }

    [Fact]
    public void FlagOn_NoEngine_DoesNotFreeze()
    {
        // Single-player / no NetworkEngine.Instance -> run normally, no freeze.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: false, isActive: false, isHost: false));
    }

    [Fact]
    public void FlagOn_EngineInactive_DoesNotFreeze()
    {
        // Engine exists but no active session (lobby / torn down) -> no freeze.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: false, isHost: false));
    }

    [Fact]
    public void FlagOn_ActiveHost_DoesNotFreeze()
    {
        // Host is the sole authoritative simulator -> never freeze the host sim.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: true));
    }

    [Fact]
    public void FlagOn_ActiveClient_Freezes()
    {
        // Active-session CLIENT + flag ON -> freeze the local geoscape sim (mirror host-authoritative state).
        Assert.True(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: false));
    }

    [Fact]
    public void FlagDefaultsOff()
    {
        // S0 ships inert: the flag is default-OFF so the scaffolding is byte-unchanged in-game.
        Assert.False(ClientSimFreeze.Enabled);
    }

    // ─── Inc4 S1 (§3.2) — DISPLAY-clock vs SIM-clock split (WriteClock paused-field selection) ───

    [Fact]
    public void SimPaused_UnderFreeze_AlwaysTrue_RegardlessOfHost()
    {
        // Under the freeze the value written to TimingInstanceData.Paused (the sim _paused field) is ALWAYS
        // true — so producers stay Max'd — REGARDLESS of the host anchor's paused value. The host paused value
        // drives only the cosmetic glyph, never the sim clock. (Display readout still tracks host via StartTime.)
        Assert.True(ClientSimFreeze.SimPaused(freeze: true, hostPaused: false));
        Assert.True(ClientSimFreeze.SimPaused(freeze: true, hostPaused: true));
    }

    [Fact]
    public void SimPaused_NoFreeze_MirrorsHost()
    {
        // Flag OFF / not an active client (freeze=false): pre-S1 behaviour is preserved byte-for-byte — the
        // client sim clock's paused field mirrors the host anchor's paused value.
        Assert.False(ClientSimFreeze.SimPaused(freeze: false, hostPaused: false));
        Assert.True(ClientSimFreeze.SimPaused(freeze: false, hostPaused: true));
    }

    // ─── Inc4 S1 (§3.1) — load-completion freeze re-assert predicate ───

    [Fact]
    public void ReAssertPredicate_IsShouldFreeze_ClientInSessionOnly()
    {
        // The load-completion re-assert (ClientGeoSimFreezePatch.Postfix → TimeSyncManager.FreezeClientGeoSim)
        // fires iff ShouldFreeze: an active-session CLIENT with the flag ON — and NEVER the host, never
        // single-player, never with the flag OFF. It re-runs on EVERY (re)load (postfix on OnLevelStart), but
        // the DECISION is exactly this pure predicate.
        Assert.True(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: false));
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: true));
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: false, engineExists: true, isActive: true, isHost: false));
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: false, isActive: false, isHost: false));
    }
}
