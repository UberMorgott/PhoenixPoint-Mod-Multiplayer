using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Feature C (heal) — client-only guard active ONLY for the duration of a <see cref="TacticalHealAnimSync"/>
    /// presentation replay (the flag is raised in <c>TacticalHealAnimSync.ClientOnHealStart</c>'s wrapper coroutine
    /// and lowered in its finally). The client drives <c>HealAbility.HealTargetCrt</c> DIRECTLY (bypassing
    /// <c>Activate</c> → no <c>ApplyCosts</c>, no AbilityActivated camera hint), so the host-authoritative effects
    /// left INSIDE HealTargetCrt to suppress are:
    ///   • HP — <see cref="HealHpNeuterPatch"/> Prefix-skips <c>BaseStat.Add(float)</c>, the SOLE heal apply for
    ///     BOTH the general heal (<c>other.Health.Add</c>, HealAbility.cs:111) and each bodypart heal
    ///     (<c>GetHealth().Add</c>, HealAbility.cs:105). HP stays owned by the 0x8F Health mirror, which applies via
    ///     <c>StatusStat.Set</c> (absolute) — and damage reduces HP via <c>StatusStat.Subtract</c> — so an
    ///     <c>Add</c>-only neuter can NEVER eat a concurrent host-authoritative HP write on the frozen mirror.
    ///   • CHARGE — handled by the SHARED <c>FireAmmoChargeNeuterPatch</c> (gated on
    ///     <see cref="FireReplayGate.AnyReplay"/>, extended to fire during the heal replay too): the heal's
    ///     <c>CommonItemData.ModifyCharges</c> (HealAbility.cs:125) is no-opped so the medkit's host-authoritative
    ///     charge never double-drains. NOT re-patched here (Harmony double-patch).
    ///   • CAMERA / AP-WP — the bypassed <c>Activate</c> is where the AbilityActivated hint + <c>ApplyCosts</c> run;
    ///     HealTargetCrt itself pushes no CameraDirector.Hint, so NO camera / cost guard is needed.
    /// KNOWN CEILING (deliberate): ConditionalHealEffects (<c>Effect.Apply</c>) and Contribution are NOT neutered — a
    /// heal-effect STATUS reconciles away via the next 0x8F flush and client Contribution never feeds authority. See
    /// <see cref="TacticalHealAnimSync"/>. The guard is HARD-GATED on <see cref="FireReplayGate.HealReplay"/> (replay
    /// flag AND not-host) so the host can NEVER be affected. Auto-register via PatchAll; reflection target so a
    /// method-rename can't hard-crash bootstrap.
    /// </summary>
    [HarmonyPatch]
    public static class HealHpNeuterPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("Base.Entities.Statuses.BaseStat");
            if (t == null) return false;
            // public void Add(float f) — single overload on BaseStat (StatusStat inherits it; the heal's actor +
            // bodypart HP adds both route through this one method). Name-only match is exact (verified vs decompile).
            _target = AccessTools.Method(t, "Add", new[] { typeof(float) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // host / normal client flow → real HP add; client heal replay → skip so the heal animates with ZERO HP add
        // (HP stays owned by the 0x8F Health mirror). Gated on HealReplay ONLY — never touches fire/melee replays.
        public static bool Prefix() => !FireReplayGate.HealReplay;
    }
}
