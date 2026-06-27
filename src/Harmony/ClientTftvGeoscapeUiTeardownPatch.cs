using System;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multipleer.Network;

namespace Multipleer.Harmony
{
    // Save-load robustness (TFTV compat). ANY geoscape teardown/level transition -- a co-op client applying a
    // host save-transfer (SaveTransferCoordinator -> EnterLevel -> FinishLevel), the HOST's own save-load, or
    // a plain single-player load -- momentarily leaves no current level. During that window two TFTV
    // geoscape-UI methods run on null backing data and throw a NullReferenceException, surfacing TFTV's own
    // error dialog. Multipleer (or just the mod's presence) is only the TRIGGER (the NRE is 100% inside TFTV).
    //
    // These two PREFIX patches skip the TFTV body during that teardown window ONLY -- whenever
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
    // TFTV is NEVER hard-referenced: types/methods resolve via AccessTools reflection; when TFTV is absent
    // Prepare() returns false and Harmony skips the patch entirely (zero impact, so non-TFTV installs still
    // load). Harmony auto-registers both classes via MultipleerMain's PatchAll.
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
            _tftvType = AccessTools.TypeByName("TFTV.TFTVCapturePandoransGeoscape");
            return _tftvType != null; // TFTV not loaded -> Harmony skips this class
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null; // defensive: TFTV absent -> no target
            // Exact, parameterless overload (Type.EmptyTypes) so AccessTools binds the right member.
            return AccessTools.Method(_tftvType, "RefreshFoodAndMutagenProductionTooltupUI", Type.EmptyTypes);
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
            _tftvType = AccessTools.TypeByName("TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch");
            return _tftvType != null; // TFTV not loaded (or class renamed) -> Harmony skips this class
        }

        public static MethodBase TargetMethod()
        {
            if (_tftvType == null) return null; // defensive: TFTV absent -> no target
            // Single Postfix in this nested class -> unambiguous by name.
            return AccessTools.Method(_tftvType, "Postfix");
        }

        // Returning false SKIPS TFTV's ODI postfix body (no NRE, no TFTVLogger.Error) during teardown.
        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }
}
