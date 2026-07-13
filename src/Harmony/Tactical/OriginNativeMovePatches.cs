using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// rca-jetjump — ORIGIN-NATIVE special moves (the 815634b shoot canon extended to movement). The generic
    /// intercept (<see cref="TacticalCombatSync.ClientInterceptGenericAbility"/>) relays the intent AND lets an
    /// <see cref="TacticalAbilityRelay.IsOriginNativeMove"/> ability (JetJump) run its REAL Activate on the
    /// origin client — native flight animation — while the host keeps outcome authority (AP/WP via the 0x8F
    /// absolute flush; POSITION reconciled below). These three JetJumpAbility patches keep that native run
    /// mirror-safe on the client:
    ///   • <see cref="JetJumpEndReconcilePatch"/> — postfix on <c>OnPlayingActionEnd</c> (JetJumpAbility.cs:119,
    ///     own override): close the origin-native-move window opened at the intercept and snap to the LATEST
    ///     host pos recorded while the native flight suppressed the 0x8F mirror — the host stays position
    ///     authority (a host-side fumble / dropped intent converges here, and an identical landing is a no-op
    ///     via the PositionEpsilon gate).
    ///   • <see cref="JetJumpClientNavSettingsPatch"/> — postfix on the private <c>GetNavSettings</c>
    ///     (JetJumpAbility.cs:299, single method): on a mirroring client force <c>TriggerOverwatch=false</c>.
    ///     The native jump navigates with TriggerOverwatch=true (:307); on the CLIENT that would fire LOCAL
    ///     enemy overwatch reactions the host already runs authoritatively off its own jump → double reaction
    ///     shots. Same neuter the mirror move rail always used (GetMirrorNavSettings).
    ///   • <see cref="JetJumpClientFumbleNeuterPatch"/> — postfix on <c>FumbleActionCheck</c>
    ///     (JetJumpAbility.cs:136): on a mirroring client never fumble locally — the origin run is
    ///     PRESENTATION; the host rolls the authoritative fumble and its outcome converges via the recorded-pos
    ///     reconcile above.
    /// All client-mirroring-gated (a host/single-player JetJump is byte-identical), NRE-guarded, fail-open.
    /// ponytail: other-clients presentation replay rail (spectators still see 4 Hz snaps) is OUT OF SCOPE —
    /// add a host→all tac.specialmove replay when it matters.
    /// </summary>
    [HarmonyPatch]
    public static class JetJumpEndReconcilePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.JetJumpAbility");
            if (t == null) return false;
            // protected override void OnPlayingActionEnd(PlayingAction action) — own override (JetJumpAbility.cs:119),
            // fires on every action end (landed / fumbled / interrupted).
            _target = AccessTools.Method(t, "OnPlayingActionEnd");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try
            {
                if (!TacticalDeploySync.IsClientMirroring) return;   // windows only ever open on the origin client
                object actor = AccessTools.Property(__instance.GetType(), "TacticalActorBase")?.GetValue(__instance, null);
                int netId = actor != null ? TacticalDeploySync.NetIdForLiveActor(actor) : -1;
                if (netId < 0) return;
                if (TacticalMoveSync.TryCloseOriginNativeMoveWindow(netId, out Vector3 lastHostPos))
                {
                    // Native nav already cancelled by the original method (JetJumpAbility.cs:124) → snap applies.
                    bool applied = TacticalMoveSync.ApplyMirrorPosition(actor, lastHostPos, forceSnap: true);
                    Debug.Log("[Multiplayer][tac] CLIENT origin-native move END netId=" + netId +
                              " reconciled-to-host-pos=" + applied);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] JetJumpEndReconcilePatch.Postfix failed: " + ex);
            }
        }
    }

    [HarmonyPatch]
    public static class JetJumpClientNavSettingsPatch
    {
        private static MethodBase _target;
        private static FieldInfo _fTriggerOverwatch;   // NavigationSettings.TriggerOverwatch (public field)

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.JetJumpAbility");
            if (t == null) return false;
            // private NavigationSettings GetNavSettings(Vector3 target) — single method, name-only bind is exact.
            _target = AccessTools.Method(t, "GetNavSettings");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __result)
        {
            try
            {
                if (__result == null || !TacticalDeploySync.IsClientMirroring) return;
                if (_fTriggerOverwatch == null)
                    _fTriggerOverwatch = AccessTools.Field(__result.GetType(), "TriggerOverwatch");
                _fTriggerOverwatch?.SetValue(__result, false);   // never trigger LOCAL overwatch on the mirror
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] JetJumpClientNavSettingsPatch.Postfix failed: " + ex);
            }
        }
    }

    [HarmonyPatch]
    public static class JetJumpClientFumbleNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.JetJumpAbility");
            if (t == null) return false;
            // protected override bool FumbleActionCheck() — own override (JetJumpAbility.cs:136).
            _target = AccessTools.Method(t, "FumbleActionCheck");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(ref bool __result)
        {
            // Mirroring client → the origin-native run never fumbles locally (presentation only; the host's
            // authoritative fumble converges via the end-reconcile snap). Host/single-player untouched.
            if (__result && TacticalDeploySync.IsClientMirroring) __result = false;
        }
    }
}
