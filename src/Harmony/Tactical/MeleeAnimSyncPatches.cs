using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Sync.Tactical;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// Feature C (melee) — client-only guards active ONLY for the duration of a
    /// <see cref="TacticalMeleeAnimSync"/> swing replay (the flag is raised in
    /// <c>TacticalMeleeAnimSync.ClientOnMeleeStart</c>'s wrapper coroutine and lowered in its finally). The
    /// client drives <c>BashAbility.BashCrt</c> DIRECTLY (bypassing <c>Activate</c> → no <c>ApplyCosts</c>, no
    /// AbilityActivated camera hint), so the only host-authoritative effects left INSIDE BashCrt to suppress
    /// are:
    ///   • DAMAGE — <see cref="MeleeDamageNeuterPatch"/> Prefix-skips <c>BashAbility.ApplyPayloadEffects</c>
    ///     (decompile <c>BashAbility.cs:501</c>, the SOLE melee damage application — covers the normal accum
    ///     path AND the rare <c>ProjectileVisuals</c> branch at <c>:622-636</c> since the whole method is
    ///     skipped). DAMAGE stays the host's authority and arrives via tac.damage (0x88).
    ///   • RETURN-FIRE — <see cref="MeleeReturnFireNeuterPatch"/> short-circuits
    ///     <c>TacticalLevelController.ReturnFire</c> (decompile <c>BashAbility.cs:543</c> →
    ///     <c>TacticalLevelController.cs:1490</c>) to an EMPTY coroutine so the client mirror never triggers
    ///     return-fire — that is host-authoritative and replays via the normal attack surfaces.
    ///   • AMMO/CHARGES — handled by the SHARED <see cref="FireAmmoChargeNeuterPatch"/> (gated on
    ///     <see cref="FireReplayGate.AnyReplay"/> so it also fires during the melee replay): the swing's
    ///     <c>CommonItemData.ModifyCharges</c> (decompile <c>BashAbility.cs:525</c>) is no-opped so the
    ///     client's host-authoritative charges never drift. NOT re-patched here (Harmony double-patch).
    ///   • CAMERA — BashCrt raises NO <c>CameraDirector.Hint</c> (verified against the decompile) and the
    ///     AbilityActivated hint lives in the bypassed <c>Activate</c>, so NO camera guard is needed for melee.
    ///   • AP/WP — the bypassed <c>Activate</c> is where <c>ApplyCosts</c> runs, so NO cost guard is needed.
    /// Both guards are HARD-GATED on <see cref="FireReplayGate.MeleeReplay"/> (replay flag AND not-host) so the
    /// host can NEVER be affected. Auto-register via PatchAll; reflection targets so a method-rename can't
    /// hard-crash bootstrap.
    /// </summary>
    [HarmonyPatch]
    public static class MeleeDamageNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.BashAbility");
            if (t == null) return false;
            // private void ApplyPayloadEffects(TacticalAbilityTarget target, DamagePredictor predictor) —
            // single overload in BashAbility, name-only match is exact (verified against the decompile).
            _target = AccessTools.Method(t, "ApplyPayloadEffects");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // host / normal client flow → real melee damage; client melee replay → skip so ZERO client damage.
        public static bool Prefix() => !FireReplayGate.MeleeReplay;
    }

    /// <summary>Client melee replay: short-circuit
    /// <c>TacticalLevelController.ReturnFire(TacticalActor, Weapon, TacticalAbilityTarget, ShootAbility,
    /// List&lt;TacticalActor&gt;, Func&lt;ReturnFireAbility,bool&gt;)</c> to an EMPTY
    /// <c>IEnumerator&lt;NextUpdate&gt;</c> so the post-swing return-fire (decompile <c>BashAbility.cs:543</c>)
    /// does NOT run on the client — return-fire is host-authoritative. Single <c>ReturnFire(...)</c> method on
    /// the controller (no overloads; <c>GetReturnFireAbilities</c> is a different name), so the name-only
    /// <c>AccessTools.Method</c> binds unambiguously. Reuses <see cref="EmptyCrt"/>. HARD-GATED on
    /// <see cref="FireReplayGate.MeleeReplay"/> exactly like the damage guard.</summary>
    [HarmonyPatch]
    public static class MeleeReturnFireNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            _target = AccessTools.Method(t, "ReturnFire");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!FireReplayGate.MeleeReplay) return true;   // host / normal flow → real return-fire
            var empty = EmptyCrt.Empty();
            if (empty == null) return true;   // couldn't build empty crt → safest to let it run
            __result = empty;
            return false;   // skip the original return-fire coroutine during the replay
        }
    }

    /// <summary>Client melee replay: NEUTER the shared known-counter / reveal mutation
    /// <c>TacticalFactionVision.IncrementKnownCounterToAll(TacticalActorBase, KnownState, int, bool)</c> that
    /// <c>BashCrt</c> runs for a NON-silent swing (decompile <c>BashAbility.cs:494-497</c> →
    /// <c>TacticalFactionVision.cs:508</c>, single overload). On the client mirror this would double-count the
    /// SHARED known-counter and fire <c>FactionKnowledgeChanged</c>, conflicting with the host-authoritative
    /// vision surface (tac.vision / 0x89). Suppressing it wholesale during the replay is correct: the method is
    /// purely the reveal/known-counter mutator and the host drives vision. Single overload → name-only
    /// <c>AccessTools.Method</c> binds exactly. HARD-GATED on <see cref="FireReplayGate.MeleeReplay"/> ONLY (NOT
    /// AnyReplay) — fire's behavior is unverified and is NOT changed by this patch.</summary>
    [HarmonyPatch]
    public static class MeleeKnownCounterNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFactionVision");
            if (t == null) return false;
            // public static void IncrementKnownCounterToAll(TacticalActorBase, KnownState, int, bool) —
            // single overload (verified against the decompile), name-only match is exact.
            _target = AccessTools.Method(t, "IncrementKnownCounterToAll");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // host / normal client flow → real reveal/known-counter mutation; client melee replay → skip (host owns vision).
        public static bool Prefix() => !FireReplayGate.MeleeReplay;
    }
}
