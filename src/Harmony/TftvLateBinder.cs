using System;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Harmony
{
    // Late-binds the TFTV-gated [HarmonyPatch] GUARD classes that PatchAll SILENTLY skipped because TFTV's
    // assembly loads AFTER Multiplayer (PP enables "Morgott.Multiplayer" before "phoenixrising.tftv"). At
    // PatchAll time their TFTV target types are unresolvable -> Prepare() returns false -> the classes never
    // bind, so every TFTV guard was DEAD in production (the 126x geoscape-teardown NRE storm persisted).
    //
    // TWO stages, BOTH mandatory (regression 2026-07-12 — TypeInitializationException at startup):
    //   1. AppDomain.AssemblyLoad callback ONLY sets a pending flag. It must NEVER Patch() here: Harmony
    //      forces JIT/PrepareMethod on the TFTV target, which runs that TFTV type's static cctor BEFORE
    //      TFTV.OnModEnabled has populated TFTVMain.Repo/DefCache -> the cctor faults -> the type is
    //      permanently poisoned -> TFTV's own OnModEnabled then throws and the GAME crashes at startup.
    //   2. The actual PatchClassProcessor.Patch() runs one Unity FRAME later (Tick, driven by
    //      MultiplayerUI.Update): TFTV.OnModEnabled runs same-frame as the assembly load, so by the next
    //      frame Repo/DefCache are populated and the cctors are safe.
    // Idempotent (_done); if TFTV was already loaded before us, PatchAll bound them and Install() no-ops.
    //
    // ponytail: explicit list — a NEW TFTV-gated [HarmonyPatch] class MUST be added here or it silently dies
    // the same way. Auto-discovery would have to parse each Prepare's TFTV gate — not worth it.
    internal static class TftvLateBinder
    {
        // Every TFTV-type-gated guard class in this assembly EXCEPT the two log-redirect patches (already
        // late-bound by TftvLogDeferredInstaller). All target types live in the main TFTV assembly.
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
        private static volatile bool _pending; // set by the AssemblyLoad callback once TFTV is resolvable
        private static int _armedFrame = -1;    // frame Tick first saw _pending — bind on a LATER frame
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
                + " classes); waiting for TFTV assembly load (bind deferred one frame past load).");
        }

        private static bool TftvLoaded() => AccessTools.TypeByName("TFTV.TFTVMain") != null;

        // ARM ONLY — never Patch() here (would JIT TFTV cctors before TFTV.OnModEnabled populated Repo/DefCache
        // -> cctor poisoning -> startup crash). Actual bind happens in Tick() one frame later. Runs during the
        // CLR assembly-load stack, so the TFTV assembly (hence TypeByName) is already resolvable when it is TFTV.
        private static void OnAssemblyLoad()
        {
            try { if (!_pending && !_done && TftvLoaded()) _pending = true; }
            catch { /* never throw into an AppDomain event */ }
        }

        // Driven every frame by MultiplayerUI.Update (BEFORE its session gate, so it runs at the main menu
        // where TFTV loads). No-op until TFTV is pending, then binds on the NEXT frame. Cheap once _done.
        public static void Tick()
        {
            if (_done || !_pending) return;
            if (_armedFrame < 0)
            {
                _armedFrame = Time.frameCount; // wait one more frame so TFTV.OnModEnabled (same frame) finished
                Debug.Log("[Multiplayer] TFTV guard bind deferred to next frame");
                return;
            }
            if (Time.frameCount <= _armedFrame) return; // still the same frame — hold
            lock (_lock)
            {
                if (_done) return;
                BindAll();
                _done = true;
            }
            Unsubscribe();
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

        private static void Unsubscribe()
        {
            if (_handler == null) return;
            AppDomain.CurrentDomain.AssemblyLoad -= _handler;
            _handler = null;
        }
    }
}
