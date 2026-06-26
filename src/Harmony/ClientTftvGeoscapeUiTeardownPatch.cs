using System;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multipleer.Network;
using UnityEngine;

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
    // the type never resolves so the guard never patches (zero impact, so non-TFTV installs still load).
    //
    // BINDING (load-order race fix). PP enables mods alphabetically, so "Morgott.Multipleer" runs its
    // PatchAll BEFORE "phoenixrising.tftv" is loaded. At PatchAll time these [HarmonyPatch] classes hit
    // their Prepare() TFTV-present gate (TypeByName == null) and Harmony PERMANENTLY drops them -> the guard
    // never bound and the teardown NRE popup returned (commit 1e26a7f's guard never took effect). So instead
    // of relying on PatchAll+Prepare, ClientTftvGeoscapeUiTeardownDeferredInstaller (below) defers the bind
    // via AppDomain.AssemblyLoad and applies these prefixes the instant the TFTV assembly loads -- the SAME
    // mechanism TftvLogDeferredInstaller already uses for the log redirect. If TFTV is ALREADY loaded at
    // PatchAll time (load order changed) the [HarmonyPatch]/Prepare() path binds eagerly and the installer
    // no-ops (no double patch). The Prepare()/TargetMethod() gate is kept as defense for that eager path and
    // so a non-TFTV install never patches.
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
        // Resolve the TFTV type FRESH on each call (no Prepare()-cached field) so the deferred installer can
        // call TargetMethod() standalone the instant the TFTV assembly loads -- exactly like the log patch.
        public static bool Prepare()
        {
            return AccessTools.TypeByName("TFTV.TFTVCapturePandoransGeoscape") != null; // TFTV absent -> Harmony skips
        }

        public static MethodBase TargetMethod()
        {
            var tftvType = AccessTools.TypeByName("TFTV.TFTVCapturePandoransGeoscape");
            if (tftvType == null) return null; // defensive: TFTV absent (or not yet loaded) -> no target
            // Exact, parameterless overload (Type.EmptyTypes) so AccessTools binds the right member.
            return AccessTools.Method(tftvType, "RefreshFoodAndMutagenProductionTooltupUI", Type.EmptyTypes);
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
        // Resolve the TFTV type FRESH on each call (no Prepare()-cached field) so the deferred installer can
        // call TargetMethod() standalone the instant the TFTV assembly loads -- exactly like the log patch.
        public static bool Prepare()
        {
            return AccessTools.TypeByName("TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch") != null; // absent/renamed -> skip
        }

        public static MethodBase TargetMethod()
        {
            var tftvType = AccessTools.TypeByName("TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch");
            if (tftvType == null) return null; // defensive: TFTV absent (or not yet loaded) -> no target
            // Single Postfix in this nested class -> unambiguous by name.
            return AccessTools.Method(tftvType, "Postfix");
        }

        // Returning false SKIPS TFTV's ODI postfix body (no NRE, no TFTVLogger.Error) during teardown.
        public static bool Prefix()
        {
            return ClientTftvGeoscapeUiTeardownDecision.RunTftvUiNormally();
        }
    }

    // Deferred installer for the two TFTV geoscape-UI teardown guards above.
    //
    // WHY: PP enables mods alphabetically, so MultipleerMain.OnModEnabled runs harmony.PatchAll BEFORE
    // "phoenixrising.tftv" is loaded. At PatchAll time TFTV.TFTVCapturePandoransGeoscape /
    // TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch are NOT yet loaded, so both [HarmonyPatch] guard
    // classes hit their Prepare() null-gate and are SILENTLY dropped forever -- the guards never bind and
    // TFTV's teardown NRE popup ("An error has occurred in the Terror from the Void mod") returns on a co-op
    // save-load. This is the SAME alphabetical PatchAll race the log redirect already solved.
    //
    // FIX: subscribe to AppDomain.AssemblyLoad (identical to TftvLogDeferredInstaller). The event fires
    // synchronously while the CLR loads TFTV's assembly, which necessarily precedes any geoscape teardown.
    // The moment each guarded type resolves we install the SAME existing Prefix via harmony.Patch -- no
    // forked decision logic, the runtime gate (ShouldRunTftvUiNormally) is unchanged. A static guard per
    // target makes the patch idempotent across the multiple AssemblyLoad events.
    //
    // If TFTV is ALREADY loaded when we install (unexpected load order), the [HarmonyPatch]/PatchAll path
    // above already covered it; we set the guards and do nothing, so there is never a double patch. If TFTV
    // is absent entirely the type never resolves -> we never patch (non-TFTV installs unaffected).
    internal static class ClientTftvGeoscapeUiTeardownDeferredInstaller
    {
        private static readonly object _lock = new object();
        private static HarmonyLib.Harmony _harmony;
        private static AssemblyLoadEventHandler _handler;
        private static bool _foodTooltipPatched;
        private static bool _odiMeterPatched;

        // Called from MultipleerMain.OnModEnabled right after PatchAll, with the mod's Harmony instance.
        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (harmony == null)
                return;

            _harmony = harmony;

            // Already loaded (e.g. load order changed)? Then PatchAll's [HarmonyPatch] guard classes already
            // bound (Prepare() returned true) -- mark handled and do NOT patch again (avoid a duplicate prefix).
            if (AccessTools.TypeByName("TFTV.TFTVCapturePandoransGeoscape") != null)
            {
                _foodTooltipPatched = true;
                _odiMeterPatched = true;
                Debug.Log("[Multipleer] TFTV already loaded at PatchAll time; geoscape teardown guard handled by PatchAll.");
                return;
            }

            _handler = (sender, args) => OnAssemblyLoad();
            AppDomain.CurrentDomain.AssemblyLoad += _handler;
            Debug.Log("[Multipleer] deferred TFTV geoscape teardown guard armed; waiting for TFTV assembly load.");
        }

        private static void OnAssemblyLoad()
        {
            // NEVER throw into an AppDomain event -- that can destabilize the host. Swallow everything.
            try
            {
                lock (_lock)
                {
                    TryPatchFoodTooltipGuard();
                    TryPatchOdiMeterGuard();

                    if (_foodTooltipPatched && _odiMeterPatched)
                        Unsubscribe();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multipleer] deferred TFTV geoscape teardown guard handler failed: " + e.Message);
            }
        }

        private static void TryPatchFoodTooltipGuard()
        {
            if (_foodTooltipPatched)
                return;

            var target = ClientTftvFoodMutagenTooltipTeardownGuardPatch.TargetMethod(); // null until TFTV type resolves.
            if (target == null)
                return;

            _harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(ClientTftvFoodMutagenTooltipTeardownGuardPatch),
                    nameof(ClientTftvFoodMutagenTooltipTeardownGuardPatch.Prefix)));

            _foodTooltipPatched = true;
            Debug.Log("[Multipleer] deferred TFTV food/mutagen tooltip teardown guard installed.");
        }

        private static void TryPatchOdiMeterGuard()
        {
            if (_odiMeterPatched)
                return;

            var target = ClientTftvOdiMeterTeardownGuardPatch.TargetMethod(); // null until the nested ODI type resolves.
            if (target == null)
                return;

            _harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(ClientTftvOdiMeterTeardownGuardPatch),
                    nameof(ClientTftvOdiMeterTeardownGuardPatch.Prefix)));

            _odiMeterPatched = true;
            Debug.Log("[Multipleer] deferred TFTV ODI meter teardown guard installed.");
        }

        private static void Unsubscribe()
        {
            if (_handler == null)
                return;
            AppDomain.CurrentDomain.AssemblyLoad -= _handler;
            _handler = null;
        }
    }
}
