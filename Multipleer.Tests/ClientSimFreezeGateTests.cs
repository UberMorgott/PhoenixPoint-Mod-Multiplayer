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
}
