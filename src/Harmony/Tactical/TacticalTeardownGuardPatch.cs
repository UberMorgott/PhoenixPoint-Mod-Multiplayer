using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// DEFENSIVE tac→geo teardown INSURANCE: a Harmony FINALIZER on <c>TacticalActor.OnExitPlay()</c>
    /// (TacticalActor.cs:804). During the tactical→geoscape transition the level tears every actor down
    /// (Level.SetState → SetCurrentCrt → per-actor OnExitPlay); a SINGLE actor throwing there (observed: an NRE on
    /// a half-torn evac actor) breaks the GeoscapeGameCrt coroutine chain ("Broken coroutine call chain") and
    /// STRANDS the whole load. This finalizer LOGS the offending actor + full exception LOUDLY and SWALLOWS it so
    /// the teardown loop continues — one broken actor can no longer hang the transition. Scoped to an ACTIVE MP
    /// session (single-player keeps vanilla behaviour = rethrow); a clean OnExitPlay (no exception) is untouched.
    /// Auto-register via <c>MultiplayerMain.PatchAll(GetExecutingAssembly())</c>; reflection-target lazily.
    /// </summary>
    [HarmonyPatch]
    public static class TacticalActorOnExitPlayTeardownGuardPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActor");
            if (t == null) return false;
            // public override void OnExitPlay()
            _target = AccessTools.Method(t, "OnExitPlay");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Finalizer: returning null SWALLOWS a thrown exception (keeps the teardown loop alive); returning the
        // original exception rethrows it. Only swallow inside an active co-op session, and only when there IS one.
        public static System.Exception Finalizer(System.Exception __exception, object __instance)
        {
            if (__exception == null) return null;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return __exception;   // not co-op → preserve vanilla behaviour
            Debug.LogError("[Multiplayer][tac] TacticalActor.OnExitPlay threw — SWALLOWED to keep the tac→geo " +
                           "teardown alive (one broken actor must not strand the load). actor=" + __instance +
                           " ex=" + __exception);
            return null;
        }
    }
}
