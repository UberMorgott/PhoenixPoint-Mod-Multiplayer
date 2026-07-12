using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// LIVE host-authoritative COMBAT/DAMAGE replication patches (spec §3, Inc 3a; generalized in Inc T2).
    /// Two Harmony patches, mirroring <see cref="MoveAbilityActivatePatch"/> / <see cref="MoveAbilityEndPatch"/>:
    ///   • CLIENT prefix on the GENERIC relayable ability set's <c>Activate(object)</c> (ShootAbility[shoot+
    ///     grenade] / BashAbility[melee] / HealAbility — see <see cref="TacticalAbilityRelay"/>) → send ONE
    ///     <c>tac.intent.ability</c> (surface 0x87, reused) and suppress the local activation; the host runs
    ///     it authoritatively and its outcome replicates via tac.damage + the T1 state-delta.
    ///   • postfix on <c>TacticalActorBase.ApplyDamage(DamageResult)</c> → on the HOST only, broadcast the
    ///     FINAL applied <c>DamageResult</c> as <c>tac.damage</c> (the funnel ALL damage flows through). The
    ///     method internally gates IsHost + the re-entrancy flag, so this binds on both sides harmlessly.
    /// Both delegate to <see cref="TacticalCombatSync"/>. Auto-register via PatchAll; reflection targets.
    /// </summary>
    [HarmonyPatch]
    public static class AbilityActivateRelayPatch
    {
        // GENERIC relay: one prefix bound to Activate(object) on EACH relayable TacticalAbility subclass.
        // Move/Overwatch are deliberately absent from TacticalAbilityRelay, so this patch never binds their
        // Activate methods → NO Harmony double-prefix with MoveAbilityActivatePatch / the overwatch patches.
        public static IEnumerable<MethodBase> TargetMethods()
        {
            const string ns = "PhoenixPoint.Tactical.Entities.Abilities.";
            foreach (var name in TacticalAbilityRelay.RelayableAbilityTypeNames)
            {
                var t = AccessTools.TypeByName(ns + name);
                if (t == null) { Debug.LogError("[Multiplayer][tac] relay: ability type not found: " + ns + name); continue; }
                // public override void Activate(object parameter) — EXACT (object) param match per subclass.
                var m = AccessTools.Method(t, "Activate", new[] { typeof(object) });
                if (m == null) { Debug.LogError("[Multiplayer][tac] relay: Activate(object) not found on " + name); continue; }
                yield return m;
            }
        }

        // Returns false to SUPPRESS the local activation on a mirroring client (intent already sent to host),
        // true otherwise (host / single-player run the real ability).
        public static bool Prefix(object __instance, object parameter)
        {
            try { return TacticalCombatSync.ClientInterceptAbility(__instance, parameter); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] AbilityActivateRelayPatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native ability on an unexpected error
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

        // Capture the target's netId BEFORE the body runs. A LETHAL hit triggers, synchronously inside
        // ApplyDamage, Health.Subtract → OnHealthChange → Die → TacticalLevel.ActorDied → the registry
        // Remove postfix — so by the time our Postfix fires the minted-id lookup already returns -1 and
        // the death broadcast is dropped. __state carries the pre-death id to Postfix (per-call, so the
        // return-melee reentrant ApplyDamage nests safely).
        public static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            try { __state = TacticalDeploySync.NetIdForLiveActor(__instance); }
            catch { __state = -1; }
        }

        // __0 is the DamageResult argument (boxed); pass it by object so the sync layer reflects its fields.
        public static void Postfix(object __instance, object __0, int __state)
        {
            try { TacticalCombatSync.OnHostApplyDamage(__instance, __0, __state); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ApplyDamagePatch.Postfix failed: " + ex);
            }
        }
    }
}
