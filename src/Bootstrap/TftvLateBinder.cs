using System;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Harmony
{
    // Late-binds the TFTV-gated [HarmonyPatch] GUARD classes that PatchAll SILENTLY skipped because TFTV's
    // assembly loaded AFTER Multiplayer (PP used to enable "Morgott.Multiplayer" before "phoenixrising.tftv").
    // At PatchAll time their TFTV target types are unresolvable -> Prepare() returns false -> the classes never
    // bind, so every TFTV guard was DEAD in production (the 126x geoscape-teardown NRE storm persisted).
    //
    // meta.json now declares "Dependencies": [ "phoenixrising.tftv" ], so PP's own mod manager loads TFTV
    // FIRST and this binder should find nothing to do. It stays as the belt to that braces: a player on a
    // hand-installed mod folder, or any future load-order change, must not silently lose every TFTV guard.
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
        // Every TFTV-type-gated guard class in this assembly. Add each new TFTV-gated [HarmonyPatch]
        // class here or it silently dies when TFTV loads after us (Prepare() false at PatchAll time).
        private static readonly Type[] _patchClasses =
        {
            typeof(Multiplayer.Network.Sync.PersonnelSync.TftvRedeployCapturePatch),
            typeof(Multiplayer.Network.Sync.PersonnelSync.TftvTrainDeployCapturePatch),
            typeof(Multiplayer.Network.Sync.PersonnelSync.TftvPromoteCapturePatch),
            typeof(Multiplayer.Tactical.TftvClientChampGuard),
            typeof(Multiplayer.Tactical.TftvErrorMirror),
            // BaseRework ASSIGNMENTS (AssignSync): four automation MUTES and four gesture CAPTURES. Every one
            // of them is the whole of that surface's client posture — an unbound mute lets a client simulate
            // its own assignments, an unbound capture drops the click on the floor.
            typeof(Multiplayer.Network.Sync.AssignSync.TftvPersonnelAutomationMute),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvRecruitRegenMute),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvTrainingClockMute),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvCompletedDeploymentMute),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvAssignWorkerCapture),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvUnassignWorkerCapture),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvTrainQueueCapture),
            typeof(Multiplayer.Network.Sync.AssignSync.TftvTrainFinalizeCapture),
        };

        /// <summary>What this peer can say about TFTV's patch state, for the diagnostics that ask
        /// (<c>RestartTrace</c>). "Bound" here means Harmony really produced a replacement method — the one
        /// question a reader of a mixed-mod incident log always has and no other line answers.</summary>
        internal static string BoundSummary { get; private set; } = "TFTV not loaded (no guard patches bound)";

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
                BoundSummary = "bound by PatchAll (TFTV was already loaded), " + _patchClasses.Length + " class(es)";
                MpLog.Log("[Multiplayer] TFTV already loaded at PatchAll; guard patches bound by PatchAll (no defer).");
                Multiplayer.Tactical.MirrorApplyGuard.Install(harmony);
                return;
            }

            _handler = (s, a) => OnAssemblyLoad();
            AppDomain.CurrentDomain.AssemblyLoad += _handler;
            MpLog.Log("[Multiplayer] deferred TFTV guard-patch binder armed (" + _patchClasses.Length
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
                MpLog.Log("[Multiplayer] TFTV guard bind deferred to next frame");
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
            var summary = new System.Text.StringBuilder("late-bound: ");
            foreach (var t in _patchClasses)
            {
                try
                {
                    // Runs Prepare -> TargetMethod -> patch for THIS one class, exactly as PatchAll would now
                    // (Prepare true because TFTV is loaded). Returns the created replacement methods, or empty.
                    var bound = new PatchClassProcessor(_harmony, t).Patch();
                    if (bound != null && bound.Count > 0)
                    {
                        summary.Append(t.Name).Append("=").Append(bound.Count).Append(" ");
                        MpLog.Log("[Multiplayer] TFTV patch BOUND (late): " + t.Name + " (" + bound.Count + " method)");
                    }
                    else
                    {
                        summary.Append(t.Name).Append("=NOTARGET ");
                        MpLog.LogWarning("[Multiplayer] TFTV patch late-bind NO target: " + t.Name
                            + " (Prepare false / method unresolved — TFTV renamed?)");
                    }
                }
                catch (Exception e)
                {
                    summary.Append(t.Name).Append("=FAILED ");
                    MpLog.LogWarning("[Multiplayer] TFTV patch late-bind FAILED: " + t.Name + " — " + e.Message);
                }
            }
            BoundSummary = summary.ToString().TrimEnd();
            // A3b's mirror-apply guard is late-bound for the SAME reason and at the SAME moment: it asks
            // Harmony which foreign patches sit on the four vanilla damage entries, and at PatchAll time TFTV
            // has installed none of them yet, so an early install would find nothing and bind nothing —
            // silently, which is this project's dominant bug class. Idempotent, so the startup call is free.
            try { Multiplayer.Tactical.MirrorApplyGuard.Install(_harmony); }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] mirror-apply guard late-install FAILED — foreign ref-DamageResult " +
                               "patches will mutate the host's already-resolved damage a second time on every " +
                               "client: " + e.Message);
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
