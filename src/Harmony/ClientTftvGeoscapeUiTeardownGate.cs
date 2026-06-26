namespace Multipleer.Harmony
{
    // Save-load robustness (TFTV compat): pure, Unity-free gate decision behind
    // ClientTftvGeoscapeUiTeardownPatch. On a co-op CLIENT, applying a host save-transfer forces a full
    // geoscape teardown+rebuild (EnterLevel -> FinishLevel). During that window two TFTV geoscape-UI
    // bodies fire on null backing data and NRE:
    //   * TFTV.TFTVCapturePandoransGeoscape.RefreshFoodAndMutagenProductionTooltupUI() -- GameUtl
    //     .CurrentLevel() is null -> NRE -> RE-THROWN to non-catching callers -> error popup (the loud one).
    //   * TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch.Postfix -- ____context.Level null -> NRE;
    //     self-caught but TFTVLogger.Error still logs (noisy popup).
    // This decides whether TFTV's geoscape UI should run NORMALLY:
    //   * true  -> let TFTV run: single-player / no session (engine absent or inactive), OR we are the host
    //              (authoritative, the geoscape level stays live), OR an active client whose level is LIVE
    //              (normal play -> TFTV food/mutagen/ODI UI fully working, NOT suppressed).
    //   * false -> SKIP the TFTV UI body: active-session CLIENT *and* the level is torn down
    //              (currentLevelIsNull) -- i.e. only the save-apply teardown/rebuild moment.
    public static class ClientTftvGeoscapeUiTeardownGate
    {
        public static bool ShouldRunTftvUiNormally(bool engineExists, bool isActive, bool isHost, bool currentLevelIsNull)
        {
            if (!engineExists || !isActive) return true; // single-player / no active session
            if (isHost) return true;                      // host: authoritative, geoscape level is live
            // active-session CLIENT: skip ONLY during the teardown/rebuild window (level == null). During
            // normal client play the level is live -> run TFTV's geoscape UI unchanged (self-scoping).
            return !currentLevelIsNull;
        }
    }
}
