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

    // ---- Prepare()-side bind decision: TFTV type missing -> patch class skipped (no throw) ----

    [Fact]
    public void TftvTypeMissing_GuardDoesNotBind()
    {
        // TFTV not installed (or its patch class renamed): AccessTools.TypeByName returned null ->
        // Prepare() must return false so Harmony skips the guard class entirely (never PatchAll-bombs).
        Assert.False(ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(tftvTypeResolved: false));
    }

    [Fact]
    public void TftvTypeResolved_GuardBinds()
    {
        Assert.True(ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(tftvTypeResolved: true));
    }

    // ---- Reflection-binding name pins: one per guarded TFTV method ----
    // The guard patches bind by these EXACT strings (AccessTools.TypeByName / AccessTools.Method).
    // Nested classes use '+'; all names verified against real TFTV source (refs/TFTV-src, master).
    // Changing any constant silently un-binds a shipped guard -> these pins make that loud.

    [Fact]
    public void Pin_FoodMutagenTooltip_TargetNames()
    {
        // TFTV source: TFTVCapturePandoransGeoscape.cs:117 (public static, parameterless).
        Assert.Equal("TFTV.TFTVCapturePandoransGeoscape", ClientTftvTeardownGuardTargets.FoodMutagenTooltipType);
        Assert.Equal("RefreshFoodAndMutagenProductionTooltupUI", ClientTftvTeardownGuardTargets.FoodMutagenTooltipMethod);
    }

    [Fact]
    public void Pin_OdiMeter_TargetNames()
    {
        // TFTV source: TFTVUI/Geoscape/TopInforBar.cs:127-128 (postfix on UIModuleInfoBar.UpdatePopulation).
        Assert.Equal("TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch", ClientTftvTeardownGuardTargets.OdiMeterType);
        Assert.Equal("Postfix", ClientTftvTeardownGuardTargets.OdiMeterMethod);
    }

    [Fact]
    public void Pin_ContainedAliens_TargetNames()
    {
        // TFTV source: TFTVUI/Geoscape/TopInforBar.cs:85-86 (postfix on UIModuleInfoBar.UpdateContainedAliensData).
        Assert.Equal("TFTV.TFTVUI.Geoscape.TopInfoBar+UIModuleInfoBar_UpdateContainedAliensData_patch",
            ClientTftvTeardownGuardTargets.ContainedAliensType);
        Assert.Equal("Postfix", ClientTftvTeardownGuardTargets.ContainedAliensMethod);
    }

    [Fact]
    public void Pin_GeoObjectiveElement_TargetNames()
    {
        // TFTV source: TFTVHarmonyGeoscapeUI.cs:134-135 (prefix on GeoObjectiveElementController.SetObjective).
        Assert.Equal("TFTV.TFTVHarmonyGeoscapeUI+GeoObjectiveElementController_SetObjective_Patch",
            ClientTftvTeardownGuardTargets.GeoObjectiveElementType);
        Assert.Equal("Prefix", ClientTftvTeardownGuardTargets.GeoObjectiveElementMethod);
    }

    [Fact]
    public void Pin_GeoObjectivesInit_TargetNames()
    {
        // TFTV source: TFTVBaseDefenseGeoscape.cs:1644-1645 (prefix on UIModuleGeoObjectives.InitObjective,
        // nested in GeoObjective).
        Assert.Equal("TFTV.TFTVBaseDefenseGeoscape+GeoObjective+TFTV_UIModuleGeoObjectives_SetObjective_ExperimentPatch",
            ClientTftvTeardownGuardTargets.GeoObjectivesInitType);
        Assert.Equal("Prefix", ClientTftvTeardownGuardTargets.GeoObjectivesInitMethod);
    }

    [Fact]
    public void Pin_RelatedActors_TargetNames()
    {
        // TFTV source: TFTVHarmonyGeoscape.cs:286-287 (postfix on DiplomaticGeoFactionObjective.GetRelatedActors).
        Assert.Equal("TFTV.TFTVHarmonyGeoscape+TFTV_DiplomaticGeoFactionObjective_GetRelatedActors_ExperimentPatch",
            ClientTftvTeardownGuardTargets.RelatedActorsType);
        Assert.Equal("Postfix", ClientTftvTeardownGuardTargets.RelatedActorsMethod);
    }

    [Fact]
    public void Pin_AgendaTrackerUpdate_TargetNames()
    {
        // TFTV source: TFTVAAAgenda/AgendaPatches.cs:246-250 (prefix on UIModuleFactionAgendaTracker.UpdateData).
        Assert.Equal("TFTV.AgendaTracker.AgendaPatches+UIModuleFactionAgendaTracker_UpdateData_Patch",
            ClientTftvTeardownGuardTargets.AgendaTrackerUpdateType);
        Assert.Equal("Prefix", ClientTftvTeardownGuardTargets.AgendaTrackerUpdateMethod);
    }
}
