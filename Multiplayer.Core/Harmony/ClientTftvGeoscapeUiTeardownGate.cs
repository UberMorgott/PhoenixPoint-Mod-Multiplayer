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

        // Prepare()-side decision, pure: bind a teardown guard iff its TFTV target type resolved via
        // reflection. TFTV absent (or the class renamed by an update) -> false -> Harmony skips the whole
        // patch class silently (never PatchAll-bombs, zero impact on non-TFTV installs).
        public static bool ShouldBindTftvGuard(bool tftvTypeResolved)
        {
            return tftvTypeResolved;
        }
    }

    // Reflection-binding catalog for every TFTV geoscape-UI teardown guard: the EXACT AccessTools.TypeByName
    // / AccessTools.Method names the guard patches bind to, single source of truth consumed by the patch
    // classes in src/Harmony/ClientTftvGeoscapeUiTeardownPatch.cs and PINNED by unit tests (a typo or an
    // accidental rename fails the pin test instead of silently never binding). All verified against real
    // TFTV source (refs/TFTV-src, branch master) — file:line cited at each guard class.
    public static class ClientTftvTeardownGuardTargets
    {
        // Existing guards (in-game verified).
        public const string FoodMutagenTooltipType = "TFTV.TFTVCapturePandoransGeoscape";
        public const string FoodMutagenTooltipMethod = "RefreshFoodAndMutagenProductionTooltupUI";
        public const string OdiMeterType = "TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch";
        public const string OdiMeterMethod = "Postfix";

        // Teardown-window sweep (rca-1): remaining NRE-prone TFTV geoscape-UI hooks.
        public const string ContainedAliensType = "TFTV.TFTVUI.Geoscape.TopInfoBar+UIModuleInfoBar_UpdateContainedAliensData_patch";
        public const string ContainedAliensMethod = "Postfix";
        public const string GeoObjectiveElementType = "TFTV.TFTVHarmonyGeoscapeUI+GeoObjectiveElementController_SetObjective_Patch";
        public const string GeoObjectiveElementMethod = "Prefix";
        public const string GeoObjectivesInitType = "TFTV.TFTVBaseDefenseGeoscape+GeoObjective+TFTV_UIModuleGeoObjectives_SetObjective_ExperimentPatch";
        public const string GeoObjectivesInitMethod = "Prefix";
        public const string RelatedActorsType = "TFTV.TFTVHarmonyGeoscape+TFTV_DiplomaticGeoFactionObjective_GetRelatedActors_ExperimentPatch";
        public const string RelatedActorsMethod = "Postfix";
        public const string AgendaTrackerUpdateType = "TFTV.AgendaTracker.AgendaPatches+UIModuleFactionAgendaTracker_UpdateData_Patch";
        public const string AgendaTrackerUpdateMethod = "Prefix";
    }
}
