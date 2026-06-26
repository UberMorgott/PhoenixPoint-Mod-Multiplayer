using Multipleer.Harmony;
using Xunit;

// Save-load robustness (TFTV compat): unit-tests the pure gate behind the two PREFIX guards on TFTV's
// geoscape-UI methods (RefreshFoodAndMutagenProductionTooltupUI + TFTV_ODI_meter_patch.Postfix). The
// Harmony/reflection wiring is not unit-testable, but the decision -- "should TFTV's geoscape UI run
// normally, or is this the co-op client save-apply teardown window (level torn down) where it must be
// skipped?" -- is extracted into a pure static method. true => let TFTV run; false => skip its body.
public class ClientTftvGeoscapeUiTeardownGateTests
{
    [Fact]
    public void NoEngine_RunsNormally()
    {
        // Single-player / no NetworkEngine.Instance -> TFTV UI runs untouched (even if a level happens to be null).
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: false, isActive: false, isHost: false, currentLevelIsNull: true));
    }

    [Fact]
    public void EngineInactive_RunsNormally()
    {
        // Engine exists but no active session (lobby / torn down) -> TFTV UI runs untouched.
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: false, isHost: false, currentLevelIsNull: true));
    }

    [Fact]
    public void ActiveHost_RunsNormally_EvenIfLevelNull()
    {
        // Host is authoritative and is the save WRITER -> never the forced-teardown victim; TFTV UI runs.
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: true, currentLevelIsNull: true));
    }

    [Fact]
    public void ActiveClient_LevelLive_RunsNormally()
    {
        // Active CLIENT in normal play (level live) -> TFTV food/mutagen + ODI UI must NOT be suppressed.
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: false, currentLevelIsNull: false));
    }

    [Fact]
    public void ActiveClient_LevelTornDown_Skips()
    {
        // Active CLIENT during the save-apply teardown/rebuild (CurrentLevel null) -> SKIP the TFTV body
        // (this is the only case that suppresses): no NRE, no TFTVLogger.Error, no error popup.
        Assert.False(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: false, currentLevelIsNull: true));
    }
}
