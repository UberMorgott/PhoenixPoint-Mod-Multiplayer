using System;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;

namespace Multiplayer.Harmony
{
    // Save-load robustness (TFTV compat). ANY geoscape teardown/level transition -- a co-op client applying a
    // host save-transfer (SaveTransferCoordinator -> EnterLevel -> FinishLevel), the HOST's own save-load, or
    // a plain single-player load -- momentarily leaves no current level. During that window several TFTV
    // geoscape-UI methods run on null backing data and throw a NullReferenceException, surfacing TFTV's own
    // error dialog (the '~14x' popup storm). Multiplayer (or just the mod's presence) is only the TRIGGER
    // (the NRE is 100% inside TFTV).
    //
    // These PREFIX patches skip the TFTV body during that teardown window ONLY -- whenever
    // GameUtl.CurrentLevel() is null -- for ANY role (host, client, single-player). This is role-INDEPENDENT
    // (earlier it was gated client-only, so the popup re-surfaced on the host/main instance; commit b49d7fd).
    // Outside that window (any role in normal play with a live level) the prefix returns true and TFTV's
    // food/mutagen + ODI UI run unchanged -- CurrentLevel() is null ONLY in the brief between-levels moment.
    //
    // Why PREFIX, not finalizer: both TFTV methods call TFTVLogger.Error(e) INSIDE the body before any
    // rethrow, and the ODI postfix self-catches (no rethrow) -- a finalizer would run AFTER the body and
    // can neither stop the RefreshFood rethrow popup nor the ODI logged popup. A prefix-skip pre-empts the
    // body entirely -> no NRE, no TFTVLogger.Error, no popup.
    //
    // TFTV is NEVER hard-referenced: types/methods resolve via AccessTools reflection (exact names live in
    // ClientTftvTeardownGuardTargets, pinned by unit tests); when TFTV is absent Prepare() returns false and
    // Harmony skips the patch entirely (zero impact, so non-TFTV installs still load). Harmony
    // auto-registers all guard classes via MultiplayerMain's PatchAll.
    internal static class ClientTftvGeoscapeUiTeardownDecision
    {
        // Shared runtime decision for both prefixes. Returns true => let the TFTV body run; false => skip it.
        // The decision is role-INDEPENDENT: it suppresses iff GameUtl.CurrentLevel() is null (teardown). The
        // engine/role flags are passed only to document context + let unit tests assert role-independence;
        // ShouldRunTftvUiNormally deliberately ignores them. Best-effort + fail-OPEN: any failure reading state
        // -> let TFTV run (its own try/catch handles it), so this guard can never throw into the game.
        internal static bool RunTftvUiNormally()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                bool currentLevelIsNull = GameUtl.CurrentLevel() == null;
                return ClientTftvGeoscapeUiTeardownGate.ShouldRunTftvUiNormally(
                    engineExists: engine != null,
                    isActive: engine != null && engine.IsActive,
                    isHost: engine != null && engine.IsHost,
                    currentLevelIsNull: currentLevelIsNull);
            }
            catch
            {
                return true; // fail-open: never suppress on an unexpected error, never throw into the game
            }
        }
    }

    // Guards TFTV.TFTVCapturePandoransGeoscape.RefreshFoodAndMutagenProductionTooltupUI() -- a public static
    // parameterless method whose body dereferences GameUtl.CurrentLevel() (null during teardown -> NRE ->
    // rethrown to non-catching callers -> the loud "~14x" error popup). Verified vs real TFTV source
    // TFTVCapturePandoransGeoscape.cs:117 (namespace TFTV, internal class).
    [HarmonyPatch]
    public static class ClientTftvFoodMutagenTooltipTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.FoodMutagenTooltipType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null); // TFTV not loaded -> Harmony skips this class
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null; // defensive: TFTV absent -> no target
            // Exact, parameterless overload (Type.EmptyTypes) so AccessTools binds the right member.
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.FoodMutagenTooltipMethod, Type.EmptyTypes);
        }

        // Returning false SKIPS TFTV's body (no NRE, no rethrow, no popup) during the client teardown window.
        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // Guards TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch.Postfix -- TFTV's own Harmony postfix on
    // UIModuleInfoBar.UpdatePopulation, whose body dereferences ____context.Level (null during teardown ->
    // NRE; self-caught but TFTVLogger.Error still logs a popup). We patch the postfix METHOD directly: a
    // prefix on the native UpdatePopulation would NOT stop TFTV's postfix (Harmony postfixes still run when
    // a prefix returns false). Verified vs real TFTV source TopInforBar.cs:127-130 (namespace
    // TFTV.TFTVUI.Geoscape, internal class TopInfoBar, nested public static class TFTV_ODI_meter_patch).
    [HarmonyPatch]
    public static class ClientTftvOdiMeterTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.OdiMeterType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null); // TFTV not loaded (or class renamed) -> Harmony skips this class
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null; // defensive: TFTV absent -> no target
            // Single Postfix in this nested class -> unambiguous by name.
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.OdiMeterMethod);
        }

        // Returning false SKIPS TFTV's ODI postfix body (no NRE, no TFTVLogger.Error) during teardown.
        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // ---- rca-1 teardown-window sweep: remaining NRE-prone TFTV geoscape-UI hooks ----------------------
    // Same root cause, four more vectors found by sweeping real TFTV source (refs/TFTV-src, master) for
    // static TFTV bodies that (a) are reachable during the between-levels window and (b) dereference
    // GameUtl.CurrentLevel()/GeoLevelController/context.Level with no null guard. TFTVLogger.Error ALWAYS
    // shows TFTV's error dialog (TFTVLogger.cs:59-73), so EVERY such body popups once per invocation --
    // several of these fire once per UI element per refresh, which is the "~14x" popup storm.
    // All five guarded bodies are pure UI/refresh code (no save-data migration, no game-state writes that
    // must survive a load); when the level is null they can do nothing but throw, so skipping them during
    // the window is strictly quieter with identical net state. Same pattern as above: reflection-only
    // binding, Prepare() false when TFTV absent, shared role-independent fail-open decision.

    // Guards TFTV.TFTVUI.Geoscape.TopInfoBar+UIModuleInfoBar_UpdateContainedAliensData_patch.Postfix --
    // TFTV's postfix on UIModuleInfoBar.UpdateContainedAliensData. Same per-frame dirty-flag pump as the
    // guarded ODI meter (UIModuleInfoBar.Update() decompile:216-252 dispatches both). Body derefs
    // ____context.ViewerFaction => context.Level.ViewerFaction (GeoscapeViewContext decompile:41) -> NRE
    // when the level is torn down; its catch RE-THROWS after TFTVLogger.Error (loud popup). Verified vs
    // real TFTV source TopInforBar.cs:85-113.
    [HarmonyPatch]
    public static class ClientTftvContainedAliensTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.ContainedAliensType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null);
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null;
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.ContainedAliensMethod);
        }

        // Skips TFTV's void Postfix body during teardown; the native UpdateContainedAliensData is unaffected.
        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // Guards TFTV.TFTVHarmonyGeoscapeUI+GeoObjectiveElementController_SetObjective_Patch.Prefix -- TFTV's
    // VOID prefix on GeoObjectiveElementController.SetObjective (objectives-panel refresh path:
    // UIModuleGeoObjectives.RefreshObjectives -> InitObjective -> SetObjective, re-run on every
    // objectives-changed event, including those raised during the save-apply/teardown window). First
    // statement derefs GameUtl.CurrentLevel().GetComponent<GeoLevelController>() -> guaranteed NRE when the
    // level is null -> TFTVLogger.Error popup (caught, not rethrown). Verified vs real TFTV source
    // TFTVHarmonyGeoscapeUI.cs:134-169. TFTV's prefix returns void, so skipping it can never block the
    // native SetObjective -- the vanilla objectives UI renders unchanged.
    [HarmonyPatch]
    public static class ClientTftvGeoObjectiveElementTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.GeoObjectiveElementType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null);
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null;
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.GeoObjectiveElementMethod);
        }

        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // Guards TFTV.TFTVBaseDefenseGeoscape+GeoObjective+TFTV_UIModuleGeoObjectives_SetObjective_ExperimentPatch
    // .Prefix -- TFTV's BOOL prefix on UIModuleGeoObjectives.InitObjective (same objectives-refresh path).
    // Body derefs GameUtl.CurrentLevel().GetComponent<GeoLevelController>() exactly when ____level is not
    // yet cached -- which is precisely the first refresh after a load -- and its catch RE-THROWS (loud
    // popup). Verified vs real TFTV source TFTVBaseDefenseGeoscape.cs:1644-1774 (derefs at :1682/:1734).
    // NOTE on skip semantics: TFTV's prefix returns bool; when our guard skips it, it yields default(false),
    // so Harmony ALSO skips the native InitObjective for that element during the window. That is intended:
    // the native body itself derefs GameUtl.CurrentLevel() when its _level cache is empty
    // (UIModuleGeoObjectives decompile:126) and would NRE anyway -- there is nothing to render in a dead
    // level. Outside the window the guard returns true and TFTV + native run unchanged.
    [HarmonyPatch]
    public static class ClientTftvGeoObjectivesInitTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.GeoObjectivesInitType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null);
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null;
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.GeoObjectivesInitMethod);
        }

        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // Guards TFTV.TFTVHarmonyGeoscape+TFTV_DiplomaticGeoFactionObjective_GetRelatedActors_ExperimentPatch
    // .Postfix -- TFTV's postfix on DiplomaticGeoFactionObjective.GetRelatedActors, which the objectives
    // refresh calls per objective (native InitObjective decompile:143 + tooltip lookup:109). Delegates to
    // TFTVBaseDefenseGeoscape.GeoObjective.AddUnderAttackBaseToObjective whose FIRST statement derefs
    // GameUtl.CurrentLevel().GetComponent<GeoLevelController>() (TFTVBaseDefenseGeoscape.cs:1565) -> NRE in
    // the window; the postfix catch RE-THROWS (loud popup). Skipping leaves __result exactly as the throw
    // path would (the throw fires before any __result mutation). Verified vs real TFTV source
    // TFTVHarmonyGeoscape.cs:286-303.
    [HarmonyPatch]
    public static class ClientTftvRelatedActorsTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.RelatedActorsType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null);
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null;
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.RelatedActorsMethod);
        }

        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // Guards TFTV.AgendaTracker.AgendaPatches+UIModuleFactionAgendaTracker_UpdateData_Patch.Prefix --
    // TFTV's BOOL prefix on UIModuleFactionAgendaTracker.UpdateData(element). The tracker refreshes every
    // element ONCE PER SECOND via a Timing coroutine (UIModuleFactionAgendaTracker decompile:100,123-127),
    // and Timing still pumps through the save-apply window -- so ____context.Level derefs (site-timer
    // branches, AgendaPatches.cs:347/354/362/365/373) NRE once per tracked element per pass: the main
    // popup-storm MULTIPLIER (TFTVLogger.Error popup each time, prefix then fails open with 'return true').
    // Same bool-prefix skip semantics as the InitObjective guard: during the window the native UpdateData is
    // also skipped (default(false)), which only defers a display refresh of a dying tracker; __result=false
    // means "not expired", so no element is wrongly removed. Verified vs real TFTV source
    // TFTVAAAgenda/AgendaPatches.cs:246-378.
    [HarmonyPatch]
    public static class ClientTftvAgendaTrackerUpdateTeardownGuardPatch
    {
        private static Type _tftvType; // resolved once in Prepare(); used by TargetMethod()

        public static bool Prepare()
        {
            _tftvType = AccessTools.TypeByName(ClientTftvTeardownGuardTargets.AgendaTrackerUpdateType);
            return ClientTftvGeoscapeUiTeardownGate.ShouldBindTftvGuard(_tftvType != null);
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null;
            return AccessTools.Method(_tftvType, ClientTftvTeardownGuardTargets.AgendaTrackerUpdateMethod);
        }

        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }
}
