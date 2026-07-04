using Multiplayer.Harmony;
using Xunit;

// Inc1 Task C: unit-tests the pure gate decision behind the client-only freeze of TFTV's
// AircraftReworkSpeedAndRange.AdjustAircraftSpeed. The Harmony wiring itself is not unit-testable,
// but the decision ("should TFTV's speed/re-navigate pass run normally?") is extracted into a pure
// static method so the four cases below are asserted directly. true => let TFTV run; false => skip
// it (client mirrors host-authoritative speed/path).
public class ClientTftvAircraftFreezeGateTests
{
    [Fact]
    public void NoEngine_RunsNormally()
    {
        // Single-player / no NetworkEngine.Instance -> TFTV must run untouched.
        Assert.True(ClientTftvAircraftFreezeGate.ShouldRunTftvNormally(engineExists: false, isActive: false, isHost: false));
    }

    [Fact]
    public void EngineInactive_RunsNormally()
    {
        // Engine exists but no session is active (lobby / torn down) -> TFTV runs untouched.
        Assert.True(ClientTftvAircraftFreezeGate.ShouldRunTftvNormally(engineExists: true, isActive: false, isHost: false));
    }

    [Fact]
    public void ActiveHost_RunsNormally()
    {
        // Host is the sole authoritative simulator -> TFTV's maintenance runs normally on the host.
        Assert.True(ClientTftvAircraftFreezeGate.ShouldRunTftvNormally(engineExists: true, isActive: true, isHost: true));
    }

    [Fact]
    public void ActiveClient_Suppresses()
    {
        // Active-session CLIENT: TFTV speed/re-navigate is suppressed; client mirrors host state.
        Assert.False(ClientTftvAircraftFreezeGate.ShouldRunTftvNormally(engineExists: true, isActive: true, isHost: false));
    }
}
