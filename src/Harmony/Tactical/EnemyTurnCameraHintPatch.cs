using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// TS7 — HOST enemy-turn camera-follow trigger (<c>tac.camerahint</c> 0x97). Postfix on the BASE
    /// <c>TacticalAbility.Activate(object)</c> (TacticalAbility.cs:1078) — the single site where the native camera
    /// follow fires <c>CameraDirectorHint.AbilityActivated</c> (gated <c>TrackWithCamera</c>, TacticalAbility.cs:1102-1108).
    /// Every ability subclass (Shoot/Bash/Move/Psychic/…) calls <c>base.Activate</c>, so one postfix on the base
    /// method sees them ALL — including AI-driven enemy actions. On the frozen mirror the enemy replay coroutines
    /// bypass Activate, so the client never gets this native follow; the host tags the acting actor so the client
    /// can chase it.
    ///
    /// ALL gating lives in <see cref="TacticalEnemyTurnCamera.HostBroadcastCameraHint"/> (host + co-op active +
    /// TrackWithCamera + the actor is an ENEMY + VISIBLE to the player faction → broadcast the netId). Off-host /
    /// single-player / friendly / invisible / non-tracked → no-op (native byte-identical). Reflective target so an
    /// engine rename never PatchAll-bombs (Prepare false → class skipped); best-effort try/catch — never blocks the
    /// native activation. Auto-registers via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class EnemyTurnCameraHintPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
            if (t == null) return false;
            // public override void Activate(object parameter = null) — single overload on TacticalAbility; the base
            // method every subclass routes through via base.Activate. Name-only match binds it exactly.
            _target = AccessTools.Method(t, "Activate");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance is the TacticalAbility that just activated. The gate (host / enemy / visible / TrackWithCamera)
        // lives in HostBroadcastCameraHint so a stray activation is a cheap no-op.
        public static void Postfix(object __instance)
        {
            try { TacticalEnemyTurnCamera.HostBroadcastCameraHint(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] EnemyTurnCameraHintPatch.Postfix failed: " + ex);
            }
        }
    }
}
