using Multiplayer.Harmony;
using Xunit;

// Save-load robustness (TFTV compat): unit-tests the pure gate behind the two PREFIX guards on TFTV's
// geoscape-UI methods (RefreshFoodAndMutagenProductionTooltupUI + TFTV_ODI_meter_patch.Postfix). The
// Harmony/reflection wiring is not unit-testable, but the decision -- "should TFTV's geoscape UI run
// normally, or is this a teardown window (level torn down) where it must be skipped?" -- is extracted
// into a pure static method. true => let TFTV run; false => skip its body.
//
// The decision is role-INDEPENDENT: it suppresses iff the geoscape level is torn down (currentLevelIsNull),
// for ANY role -- host, client, or single-player / no session. The engine/role flags are still accepted so
// these tests can vary them and PROVE they do not change the outcome (regression guard: re-adding role
// gating, as in the original client-only guard that let the popup re-surface on the host, fails these).
public class ClientTftvGeoscapeUiTeardownGateTests
{
    // ---- Level LIVE (non-null) -> always run TFTV UI normally, regardless of role ----

    [Fact]
    public void NoSession_LevelLive_RunsNormally()
    {
        // Single-player / no NetworkEngine session, level live -> TFTV UI runs untouched.
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: false, isActive: false, isHost: false, currentLevelIsNull: false));
    }

    [Fact]
    public void ActiveHost_LevelLive_RunsNormally()
    {
        // Host in normal play (level live) -> TFTV food/mutagen + ODI UI must NOT be suppressed.
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: true, currentLevelIsNull: false));
    }

    [Fact]
    public void ActiveClient_LevelLive_RunsNormally()
    {
        // Active CLIENT in normal play (level live) -> TFTV food/mutagen + ODI UI must NOT be suppressed.
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: false, currentLevelIsNull: false));
    }

    // ---- Level TORN DOWN (null) -> always skip the spurious-NRE TFTV body, regardless of role ----

    [Fact]
    public void ActiveHost_LevelTornDown_Skips()
    {
        // HOST save-load teardown (CurrentLevel null) -> SKIP. This is the recurrence the broaden fixes:
        // the old client-only guard returned true here, so the error popup surfaced on the host/main instance.
        Assert.False(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: true, currentLevelIsNull: true));
    }

    [Fact]
    public void ActiveClient_LevelTornDown_Skips()
    {
        // Active CLIENT during the save-apply teardown/rebuild (CurrentLevel null) -> SKIP the TFTV body:
        // no NRE, no TFTVLogger.Error, no error popup.
        Assert.False(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: true, isHost: false, currentLevelIsNull: true));
    }

    [Fact]
    public void InactiveSession_LevelTornDown_Skips()
    {
        // Engine present but session inactive (e.g. mid load/transition) with level torn down -> SKIP.
        // Old guard returned true on !isActive, so the NRE leaked through this window too.
        Assert.False(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: true, isActive: false, isHost: false, currentLevelIsNull: true));
    }

    [Fact]
    public void NoSession_LevelTornDown_Skips()
    {
        // Single-player / no NetworkEngine, level torn down (plain SP load with the mod installed) -> SKIP.
        Assert.False(ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
            engineExists: false, isActive: false, isHost: false, currentLevelIsNull: true));
    }
}
