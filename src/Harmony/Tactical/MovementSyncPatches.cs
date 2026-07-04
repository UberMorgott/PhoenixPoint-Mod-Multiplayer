using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// LIVE soldier-MOVE replication patches (spec §3.4, Inc 2). Two Harmony patches on
    /// <c>MoveAbility</c> — one capture point covers player AND AI moves (both route through
    /// <c>MoveAbility.Activate → Move</c>):
    ///   • CLIENT prefix on <c>Activate(object)</c> → send <c>tac.intent.move</c>, suppress the local move.
    ///   • HOST postfix on <c>OnPlayingActionEnd(PlayingAction)</c> → broadcast the FINAL landed pose.
    /// Both delegate to <see cref="TacticalMoveSync"/>; the host is never suppressed. Auto-registers via
    /// PatchAll. Reflection-target (TypeByName) so it binds lazily like the existing TacticalPatches.
    /// </summary>
    [HarmonyPatch]
    public static class MoveAbilityActivatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.MoveAbility");
            if (t == null) return false;
            // public override void Activate(object parameter)
            _target = AccessTools.Method(t, "Activate", new[] { typeof(object) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Returns false to SUPPRESS the local move on a mirroring client (intent already sent to host),
        // true otherwise (host / single-player run the real move).
        public static bool Prefix(object __instance, object parameter)
        {
            try { return TacticalMoveSync.ClientInterceptMove(__instance, parameter); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] MoveAbilityActivatePatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native move on an unexpected error
            }
        }
    }

    /// <summary>
    /// HOST postfix on <c>MoveAbility.OnPlayingActionEnd(PlayingAction)</c> (MoveAbility.cs:101) — fires when
    /// the move action ends, with <c>TacticalActor.Pos</c> at the FINAL landed cell and
    /// <c>TacticalNav.StopReason</c> resolved. Broadcasts <c>tac.move</c> to all peers. No-op off-host.
    /// </summary>
    [HarmonyPatch]
    public static class MoveAbilityEndPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.MoveAbility");
            if (t == null) return false;
            // protected override void OnPlayingActionEnd(PlayingAction action)
            _target = AccessTools.Method(t, "OnPlayingActionEnd");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try { TacticalMoveSync.HostBroadcastMoveOutcome(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] MoveAbilityEndPatch.Postfix failed: " + ex);
            }
        }
    }
}
