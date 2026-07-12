using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Harmony
{
    // Late-binds the TFTV-gated [HarmonyPatch] GUARD classes that PatchAll SILENTLY skipped because TFTV's
    // assembly loads AFTER Multiplayer (PP enables "Morgott.Multiplayer" before "phoenixrising.tftv"). At
    // PatchAll time their TFTV target types are unresolvable -> Prepare() returns false -> the classes never
    // bind, so every TFTV guard was DEAD in production (Player.log: "TFTV ... null-guard skipped (TFTV type
    // absent)"; the 126x NRE storm persisted). Same proven mechanism as TftvLogDeferredInstaller
    // (AppDomain.AssemblyLoad) — the two log-redirect patches already ride that installer, so this binder owns
    // the REMAINING guards. When TFTV's assembly appears, PatchClassProcessor each listed class ONCE (its own
    // Prepare now returns true). Idempotent via _done; if TFTV was already loaded at init, PatchAll bound them
    // and Install() no-ops.
    //
    // ponytail: explicit list — a NEW TFTV-gated [HarmonyPatch] class MUST be added here or it silently dies
    // the same way. Auto-discovery would have to parse each Prepare's TFTV gate — not worth it.
    internal static class TftvLateBinder
    {
        // Every TFTV-type-gated guard class in this assembly EXCEPT the two log-redirect patches (already
        // late-bound by TftvLogDeferredInstaller). All target types live in the main TFTV assembly
        // (namespace "TFTV"), so one marker load makes them all resolvable together.
        private static readonly Type[] _patchClasses =
        {
            // ClientTftvGeoscapeUiTeardownPatch.cs — geoscape-UI teardown NRE guards (the reported bug).
            typeof(ClientTftvFoodMutagenTooltipTeardownGuardPatch),
            typeof(ClientTftvOdiMeterTeardownGuardPatch),
            typeof(ClientTftvContainedAliensTeardownGuardPatch),
            typeof(ClientTftvGeoObjectiveElementTeardownGuardPatch),
            typeof(ClientTftvGeoObjectivesInitTeardownGuardPatch),
            typeof(ClientTftvRelatedActorsTeardownGuardPatch),
            typeof(ClientTftvAgendaTrackerUpdateTeardownGuardPatch),
            // ClientTftvAircraftFreezePatch.cs — client-mirror aircraft self-adjust suppression.
            typeof(ClientTftvAircraftFreezePatch),
            // ClientTftvTacticalScriptGuards.cs — client-mirror TFTV mission-script suppression.
            typeof(ClientTftvVisionTurnStartScriptGuard),
            typeof(ClientTftvActorEnteredPlayScriptGuard),
            typeof(ClientTftvActorDiedScriptGuardPrefix),
            typeof(ClientTftvActorDiedScriptGuardPostfix),
            typeof(ClientTftvApplyDamageScriptGuard),
            typeof(ClientTftvStatusAddScriptGuard),
        };

        private static readonly object _lock = new object();
        private static HarmonyLib.Harmony _harmony;
        private static AssemblyLoadEventHandler _handler;
        private static bool _done;

        // Called from MultiplayerMain.OnModEnabled right after PatchAll, with the mod's Harmony instance.
        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (harmony == null) return;
            _harmony = harmony;

            // TFTV already present (load order changed)? PatchAll already bound these -> nothing to defer.
            if (TftvLoaded())
            {
                _done = true;
                Debug.Log("[Multiplayer] TFTV already loaded at PatchAll; guard patches bound by PatchAll (no defer).");
                return;
            }

            _handler = (s, a) => OnAssemblyLoad();
            AppDomain.CurrentDomain.AssemblyLoad += _handler;
            Debug.Log("[Multiplayer] deferred TFTV guard-patch binder armed (" + _patchClasses.Length
                + " classes); waiting for TFTV assembly load.");
        }

        private static bool TftvLoaded() => AccessTools.TypeByName("TFTV.TFTVMain") != null;

        private static void OnAssemblyLoad()
        {
            // NEVER throw into an AppDomain event — that can destabilize the host. Swallow everything.
            try
            {
                if (_done) return;
                if (!TftvLoaded()) return; // cheap marker gate: no per-class Prepare (or its log) until TFTV is present
                lock (_lock)
                {
                    if (_done) return;
                    BindAll();
                    _done = true;
                    if (_handler != null) { AppDomain.CurrentDomain.AssemblyLoad -= _handler; _handler = null; }
                }
            }
            catch (Exception e) { Debug.LogWarning("[Multiplayer] TFTV late-bind handler failed: " + e.Message); }
        }

        private static void BindAll()
        {
            foreach (var t in _patchClasses)
            {
                try
                {
                    // Runs Prepare -> TargetMethod -> patch for THIS one class, exactly as PatchAll would now
                    // (Prepare true because TFTV is loaded). Returns the created replacement methods, or empty.
                    var bound = new PatchClassProcessor(_harmony, t).Patch();
                    if (bound != null && bound.Count > 0)
                        Debug.Log("[Multiplayer] TFTV patch BOUND (late): " + t.Name + " (" + bound.Count + " method)");
                    else
                        Debug.LogWarning("[Multiplayer] TFTV patch late-bind NO target: " + t.Name
                            + " (Prepare false / method unresolved — TFTV renamed?)");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Multiplayer] TFTV patch late-bind FAILED: " + t.Name + " — " + e.Message);
                }
            }
        }
    }
}
