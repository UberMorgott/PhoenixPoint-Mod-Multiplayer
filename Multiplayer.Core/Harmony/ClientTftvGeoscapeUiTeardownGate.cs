namespace Multiplayer.Harmony
{
    // Save-load robustness (TFTV compat): pure, Unity-free gate decision behind the teardown guard patches.
    // Any geoscape teardown / level transition (co-op client applying a host save, the HOST's own save-load,
    // OR a plain single-player load with the mod installed) momentarily leaves NO current geoscape level.
    // During that window two TFTV geoscape-UI bodies fire on null backing data and NRE:
    //   * TFTV.TFTVCapturePandoransGeoscape.RefreshFoodAndMutagenProductionTooltupUI() -- GameUtl
    //     .CurrentLevel() is null -> NRE -> RE-THROWN to non-catching callers -> error popup (the loud one).
    //   * TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch.Postfix -- ____context.Level null -> NRE;
    //     self-caught but TFTVLogger.Error still logs (noisy popup).
    // The NRE is ALWAYS spurious here: with the level torn down there is nothing to render. So the decision
    // gates on the NULL-BACKING condition ALONE -- it is role-INDEPENDENT:
    //   * true  -> let TFTV run: a geoscape level IS live (currentLevelIsNull == false) -> normal play, TFTV
    //              food/mutagen/ODI UI fully working, NEVER suppressed (CurrentLevel() is non-null whenever a
    //              geo/tactical level is loaded; it is null ONLY in the brief between-levels teardown window).
    //   * false -> SKIP the TFTV UI body during teardown (currentLevelIsNull == true) for ANY role -- host,
    //              client, or single-player. (Earlier this guard wrongly returned true for the host and for
    //              any inactive session, so the popup re-surfaced on the host/main instance; see commit b49d7fd.)
    // The engine/role flags are still ACCEPTED so call sites and unit tests can assert this role-independence,
    // but they are intentionally NOT read by the decision.
    public static class ClientTftvGeoscapeUiTeardownGate
    {
        public static bool ShouldRunTftvUiNormally(bool engineExists, bool isActive, bool isHost, bool currentLevelIsNull)
        {
            // Role-independent: suppress iff the geoscape level is torn down (null backing data) -> spurious NRE.
            return !currentLevelIsNull;
        }
    }
}
