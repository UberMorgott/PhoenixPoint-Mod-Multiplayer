using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// BUG3b — CLIENT-mirror hurt-reaction FREEZE fix. An enemy with a damage-triggered hurt-reaction ability
    /// (e.g. PainChameleon = <c>RepositionAbility : TacticalHurtReactionAbility</c>) repositions + cloaks when it
    /// takes damage. On the client MIRROR the host-broadcast damage (<c>tac.damage</c> → <c>ApplyDamage</c>) raises
    /// the enemy's <c>Health.StatChangeEvent</c> → <c>TacticalHurtReactionAbility.OnActorDamaged</c> →
    /// <c>RegisterHurtReaction</c> → <c>TacticalLevelController.ExecuteHurtReactionAbilities</c> Activates the
    /// reaction LOCALLY (<c>RepositionAbility.cs:122-130</c> reads <c>ViewerFaction.Vision.GetKnownActors</c> while
    /// the <c>tac.vision</c> mirror is mutating <c>KnownActors</c> → NRE) → the <c>PlayingAction.CompleteAction</c>
    /// chain breaks → the in-flight fire action never completes → HUD + camera lock up (the reported post-shot freeze).
    ///
    /// FIX: a CLIENT-mirror-only prefix that NO-OPS the reaction's <c>Activate</c> (return false). Skipping Activate
    /// means no <c>PlayAction</c> is created, so <c>TacticalAbility.Execute</c>'s <c>if (IsExecuting)</c> is false and
    /// the level's <c>ExecuteHurtReactionAbilities</c> <c>Timing.Call(value.Execute(...))</c> COMPLETES IMMEDIATELY —
    /// no local coroutine, no NRE, no hang. The HOST owns enemy AI and runs the real reposition; its RESULT already
    /// replicates to the client via <c>tac.move</c> (final position) + <c>tac.vision</c> (visibility), so the client
    /// mirror needs none of the local reaction. Mirrors the existing client-suppress pattern (gate on
    /// <see cref="TacticalDeploySync.IsClientMirroring"/>, like <c>OverwatchAbilityActivatePatch</c> /
    /// <c>SuppressedAbilityViewClearPatch</c>). <c>Activate</c> is the <c>sealed override</c> declared on the abstract
    /// base <c>TacticalHurtReactionAbility</c>, so patching it covers EVERY subclass. Reflection target + Prepare()-
    /// gated on the type resolving so an engine rename can't PatchAll-bomb bootstrap. Diag-logged on bind + on fire.
    /// </summary>
    [HarmonyPatch]
    public static class HurtReactionActivateSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalHurtReactionAbility");
            if (t == null) { Debug.LogWarning("[Multiplayer][tac] hurt-reaction suppress: TacticalHurtReactionAbility not found — patch skipped"); return false; }
            // public sealed override void Activate(object parameter = null) — single 1-arg overload. EXACT param
            // match (AccessTools does exact-type matching; an overload/Property-vs-Method mismatch silently fails to bind).
            _target = AccessTools.Method(t, "Activate", new[] { typeof(object) });
            if (_target == null) { Debug.LogWarning("[Multiplayer][tac] hurt-reaction suppress: Activate(object) not found — patch skipped"); return false; }
            Debug.Log("[Multiplayer][tac] hurt-reaction suppress patch bound to TacticalHurtReactionAbility.Activate");
            return true;
        }

        public static MethodBase TargetMethod() => _target;

        // CLIENT MIRROR → skip the local hurt-reaction entirely (return false); host / single-player → run native.
        public static bool Prefix(object __instance)
        {
            try
            {
                if (!TacticalDeploySync.IsClientMirroring) return true;   // host / single-player → real reaction
                Debug.Log("[Multiplayer][tac] CLIENT suppressed enemy hurt-reaction (mirror) ability=" +
                          (__instance != null ? __instance.GetType().Name : "<null>"));
                return false;   // no PlayAction → IsExecuting false → level's Execute completes → no coroutine, no NRE
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] HurtReactionActivateSuppressPatch.Prefix failed: " + ex);
                return true;    // fail-open: never block the native reaction on an unexpected error
            }
        }
    }
}
