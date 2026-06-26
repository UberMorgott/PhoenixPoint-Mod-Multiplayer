using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// FIX B (relayed-shot cosmetic-delay strip): make a CLIENT-ORIGIN shot the host executes authoritatively
    /// reach the NATIVE damage roll almost immediately, WITHOUT re-rolling damage. Three host-side patches, all
    /// gated on <see cref="TacticalCombatSync.RelayedShots"/> (populated only in <c>HostOnAbilityIntent</c>, so
    /// the host's OWN shots / the client / single-player are never affected):
    ///   • B1 <see cref="RelayedShootInlinePatch"/> — prefix on <c>TacticalAbility.EnqueueAction</c>: reroute a
    ///     registered relayed shoot's long-range <c>EnqueueAction(soloAfterCurrent)</c> + camera-blend defer to
    ///     an immediate <c>PlayAction</c> (the inline branch overwatch / return-fire / point-blank already use).
    ///   • B2 <see cref="RelayedShootAimSkipPatch"/> — postfix on <c>TacticalActor.CurrentlyAiming</c>: report a
    ///     registered shooter as already aiming so <c>FireWeaponAtTargetCrt</c> SKIPS the standing aim-up wait
    ///     (the same native path an already-aiming overwatch reaction shot takes).
    ///   • <see cref="RelayedShootEndPatch"/> — postfix on <c>ShootAbility.OnPlayingActionEnd</c>: clear the
    ///     registry entry when the shot ends (hit / miss / fumble).
    /// The NATIVE roll (<c>ShootAndWaitRF</c> → <c>ApplyDamage</c>) and burst/grenade/overwatch logic are
    /// UNTOUCHED — only the host's pre-damage cosmetic scheduling is stripped for relayed shots. Reflection
    /// targets + fail-open, exactly like the sibling combat patches; auto-register via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class RelayedShootInlinePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
            if (t == null) return false;
            // public void EnqueueAction(Func<PlayingAction,IEnumerator<NextUpdate>> action, object parameter,
            // bool soloAfterCurrent) — single EnqueueAction, name-only avoids rebuilding the Func<,> generic.
            _target = AccessTools.Method(t, "EnqueueAction");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the ability; __0 = action delegate; __1 = parameter (the TacticalAbilityTarget). For a
        // REGISTERED relayed shoot, run the action inline (PlayAction) and return false to SKIP the native
        // enqueue+camera-blend defer; otherwise return true (native enqueue). Fail-open on error.
        public static bool Prefix(object __instance, object __0, object __1)
        {
            try { return !TacticalCombatSync.TryRunRelayedShootInline(__instance, __0, __1); }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] RelayedShootInlinePatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native enqueue
            }
        }
    }

    [HarmonyPatch]
    public static class RelayedShootAimSkipPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActor");
            if (t == null) return false;
            // public bool CurrentlyAiming { get; } — patch the getter.
            _target = AccessTools.PropertyGetter(t, "CurrentlyAiming");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Postfix: while a relayed shoot is in flight for this actor, force CurrentlyAiming=true so the
        // FireWeaponAtTargetCrt aim-up wait (gated on !CurrentlyAiming) is skipped. Only flips false→true, never
        // the reverse, and only for a registered (client-origin) shooter — host's OWN shots are untouched.
        public static void Postfix(object __instance, ref bool __result)
        {
            try
            {
                if (!__result && TacticalCombatSync.ShouldForceAimingForRelayedShot(__instance))
                {
                    __result = true;
                    Debug.Log("[Multipleer][tac] B2 aim-up skipped for relayed shoot (CurrentlyAiming forced true)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] RelayedShootAimSkipPatch.Postfix failed: " + ex);
            }
        }
    }

    [HarmonyPatch]
    public static class RelayedShootEndPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.ShootAbility");
            if (t == null) return false;
            // protected override void OnPlayingActionEnd(PlayingAction action) — single override on ShootAbility
            // (covers shoot + grenade); fires from ClearPlayingAction on every action end (hit/miss/fumble).
            _target = AccessTools.Method(t, "OnPlayingActionEnd");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the ShootAbility whose action just ended → drop its registry entry so B1/B2 stop applying.
        public static void Postfix(object __instance)
        {
            try { TacticalCombatSync.EndRelayedShot(__instance); }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] RelayedShootEndPatch.Postfix failed: " + ex);
            }
        }
    }
}
