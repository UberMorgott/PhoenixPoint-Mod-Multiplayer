using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// LIVE host-authoritative COMBAT/DAMAGE replication patches (spec §3, Inc 3a). Two Harmony patches,
    /// mirroring <see cref="MoveAbilityActivatePatch"/> / <see cref="MoveAbilityEndPatch"/>:
    ///   • CLIENT prefix on <c>ShootAbility.Activate(object)</c> → send <c>tac.intent.ability</c>, suppress
    ///     the local shot (the existing <see cref="Multipleer.Harmony.FireWeaponPatch"/> already suppresses
    ///     the client roll chain; this stops the client even queuing the shot).
    ///   • postfix on <c>TacticalActorBase.ApplyDamage(DamageResult)</c> → on the HOST only, broadcast the
    ///     FINAL applied <c>DamageResult</c> as <c>tac.damage</c> (the funnel ALL damage flows through). The
    ///     method internally gates IsHost + the re-entrancy flag, so this binds on both sides harmlessly.
    /// Both delegate to <see cref="TacticalCombatSync"/>. Auto-register via PatchAll; reflection targets.
    /// </summary>
    [HarmonyPatch]
    public static class ShootAbilityActivatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.ShootAbility");
            if (t == null) return false;
            // public override void Activate(object parameter)
            _target = AccessTools.Method(t, "Activate", new[] { typeof(object) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Returns false to SUPPRESS the local shot on a mirroring client (intent already sent to host),
        // true otherwise (host / single-player roll the real shot).
        public static bool Prefix(object __instance, object parameter)
        {
            try { return TacticalCombatSync.ClientInterceptShoot(__instance, parameter); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multipleer][tac] ShootAbilityActivatePatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native shot on an unexpected error
            }
        }
    }

    /// <summary>
    /// Postfix on <c>TacticalActorBase.ApplyDamage(DamageResult)</c> (TacticalActorBase.cs:985) — the single
    /// funnel ALL damage flows through (shots, melee, overwatch, AI, death cascade). On the HOST it broadcasts
    /// the FINAL applied result as <c>tac.damage</c>. <see cref="TacticalCombatSync.OnHostApplyDamage"/>
    /// internally gates IsHost + the re-entrancy flag, so binding on the client is a no-op. Postfix so it
    /// captures the result AFTER the engine finalized it.
    /// </summary>
    [HarmonyPatch]
    public static class ApplyDamagePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
            if (t == null) return false;
            var dr = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.DamageResult");
            if (dr == null) return false;
            // public void ApplyDamage(DamageResult damageResult) — EXACT param match (DamageResult struct).
            _target = AccessTools.Method(t, "ApplyDamage", new[] { dr });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 is the DamageResult argument (boxed); pass it by object so the sync layer reflects its fields.
        public static void Postfix(object __instance, object __0)
        {
            try { TacticalCombatSync.OnHostApplyDamage(__instance, __0); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multipleer][tac] ApplyDamagePatch.Postfix failed: " + ex);
            }
        }
    }
}
