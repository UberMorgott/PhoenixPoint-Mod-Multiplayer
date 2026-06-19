using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// LIVE host-authoritative OVERWATCH-ARM replication patches (Inc Overwatch). Two Harmony patches,
    /// mirroring <see cref="ShootAbilityActivatePatch"/> / <see cref="ApplyDamagePatch"/>:
    ///   • CLIENT prefix on <c>OverwatchAbility.Activate(object)</c> → send <c>tac.intent.overwatch</c> (with
    ///     the flattened watch cone read from the <c>TacticalAbilityTarget</c> parameter) and SUPPRESS the local
    ///     arm (return false). On the host / single-player, or while re-applying a host outcome, it passes
    ///     through (return true) so the host runs the authoritative arm.
    ///   • POSTFIX on <c>OverwatchStatus.SetCone(Cone?)</c> — the single cone funnel for BOTH arm (real cone,
    ///     from Activate→StartOverwatch) AND clear (null/default cone, from OnUnapply, fired by EVERY status
    ///     removal). On the HOST it broadcasts <c>tac.overwatch.state</c>.
    ///     <see cref="TacticalOverwatchSync.OnHostSetCone"/> internally gates IsHost + the re-entrancy flag, so
    ///     binding the postfix on the client is a harmless no-op.
    /// Both delegate to <see cref="TacticalOverwatchSync"/>. Auto-register via PatchAll; reflection targets so an
    /// engine rename never PatchAll-bombs (Prepare returns false → the class is skipped).
    /// </summary>
    [HarmonyPatch]
    public static class OverwatchAbilityActivatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.OverwatchAbility");
            if (t == null) return false;
            // public override void Activate(object parameter)
            _target = AccessTools.Method(t, "Activate", new[] { typeof(object) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Returns false to SUPPRESS the local arm on a mirroring client (intent already sent to host),
        // true otherwise (host / single-player run the real arm; the SetCone postfix then broadcasts on the host).
        public static bool Prefix(object __instance, object parameter)
        {
            try { return TacticalOverwatchSync.ClientInterceptArm(__instance, parameter); }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] OverwatchAbilityActivatePatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native arm on an unexpected error
            }
        }
    }

    /// <summary>
    /// Postfix on <c>OverwatchStatus.SetCone(Cone?)</c> (OverwatchStatus.cs:158) — the single funnel BOTH the
    /// arm (SetCone(realCone)) and the clear (SetCone(null), from OnUnapply: consume-after-reaction, next-turn
    /// expiry, manual cancel) pass through. On the HOST it broadcasts the now-armed/cleared overwatch state as
    /// <c>tac.overwatch.state</c>. <see cref="TacticalOverwatchSync.OnHostSetCone"/> internally gates IsHost +
    /// the re-entrancy flag, so binding on the client is a no-op.
    /// </summary>
    [HarmonyPatch]
    public static class OverwatchStatusSetConePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Statuses.OverwatchStatus");
            if (t == null) return false;
            var coneType = AccessTools.TypeByName("Base.Utils.Maths.Cone");
            if (coneType == null) return false;
            // public void SetCone(Cone? cone) — EXACT param match (Nullable<Cone>).
            var nullableConeType = typeof(Nullable<>).MakeGenericType(coneType);
            _target = AccessTools.Method(t, "SetCone", new[] { nullableConeType });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 is the Nullable<Cone> argument (boxed → null, or a boxed Cone). Pass by object so the sync layer
        // reflects its fields. Host-gated inside.
        public static void Postfix(object __instance, object __0)
        {
            try { TacticalOverwatchSync.OnHostSetCone(__instance, __0); }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] OverwatchStatusSetConePatch.Postfix failed: " + ex);
            }
        }
    }
}
