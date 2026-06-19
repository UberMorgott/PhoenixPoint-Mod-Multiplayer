using System;
using System.Collections.Generic;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) policy for the generic ability-INTENT relay (state-spine §3, Inc T2).
    ///
    /// A mirroring client suppresses a LOCAL gameplay-ability activation and relays a single
    /// <c>tac.intent.ability</c> (surface 0x87, reused — it already carries
    /// {shooterNetId, abilityDefGuid, targetNetId, targetPos, nonce}) to the host, which re-resolves the
    /// ability by def guid and <c>Activate</c>s it authoritatively. The outcome converges via the existing
    /// <c>ApplyDamage</c> → tac.damage funnel (plus the T1 per-actor state-delta for AP/WP/status).
    ///
    /// This class is the ALLOWLIST that decides which <c>TacticalAbility</c> subclasses ride the generic
    /// relay. It is matched by simple runtime type NAME so it stays Unity-free and unit-testable.
    ///
    /// INCLUDED (actor/position target + outcome replicates via the <c>ApplyDamage</c> → tac.damage funnel):
    ///   • <c>ShootAbility</c>  — ranged shot AND grenade throw (<c>ThrowGrenade_ShootAbilityDef</c> is a
    ///     ShootAbility); damage replicates via the <c>ApplyDamage</c> → tac.damage funnel.
    ///   • <c>BashAbility</c>   — melee (<c>IAttackAbility</c>/<c>IDamageDealer</c>); damage funnels the same way.
    ///
    /// EXCLUDED (have their OWN working dedicated sync paths, or no outcome surface carries their result yet):
    ///   • <c>MoveAbility</c>      — dedicated command-sync move + animation (tac.move.start / tac.move).
    ///   • <c>OverwatchAbility</c> — dedicated 0x8C/0x8D arm + cone; reaction-fire is host-driven.
    ///   • <c>ReloadAbility</c>    — its target carries an Equipment/ammo-clip ref, NOT an actor/pos this wire
    ///     can serialize; ammo state replicates via the T1 actor-state delta instead.
    ///   • <c>HealAbility</c>      — heals via <c>Health.Add(...)</c> DIRECTLY (HealAbility.HealTargetCrt:105/111),
    ///     NOT through <c>ApplyDamage</c>, so it raises no <c>tac.damage</c>; and the T1 actor-state delta carries
    ///     only {AP, WP, status-set} (no health) — a relayed heal would run on the host but never mirror the
    ///     client target's HP. EXCLUDED pending a health-carrying surface (health is a reserved fieldMask bit
    ///     in the T1 delta — a later increment adds it, then heal can rejoin).
    /// The exclusions ALSO guarantee no Harmony double-prefix: Move/Overwatch keep their own Activate patches
    /// and are never added to <see cref="RelayableAbilityTypeNames"/>, so the generic patch never binds them.
    /// </summary>
    public static class TacticalAbilityRelay
    {
        /// <summary>Type NAMES (Type.Name, not full namespace) of the TacticalAbility subclasses relayed via
        /// the generic ability-intent. The generic Harmony patch binds <c>Activate(object)</c> on exactly these
        /// types — adding a name here is the ONLY change needed to relay another target-taking gameplay ability.</summary>
        public static readonly string[] RelayableAbilityTypeNames =
        {
            "ShootAbility",   // shoot + grenade
            "BashAbility",    // melee
        };

        /// <summary>Type NAMES explicitly kept OFF the generic relay (dedicated path / wrong target shape).
        /// Documentation + asserted by tests so a future edit can't silently add a double-handled ability.</summary>
        public static readonly string[] ExcludedAbilityTypeNames =
        {
            "MoveAbility",       // dedicated move command-sync + animation
            "OverwatchAbility",  // dedicated 0x8C/0x8D arm + cone
            "ReloadAbility",     // equipment/ammo-clip target — not actor/pos; rides the actor-state delta
            "HealAbility",       // Health.Add(...) directly (no tac.damage); no health surface yet — pending
        };

        private static readonly HashSet<string> _relayable =
            new HashSet<string>(RelayableAbilityTypeNames, StringComparer.Ordinal);

        /// <summary>True when an ability whose runtime type name is <paramref name="abilityTypeName"/> should
        /// ride the generic client→host ability-intent relay. Unknown / excluded types return false (native
        /// path runs, or its dedicated sync handles it).</summary>
        public static bool IsRelayable(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _relayable.Contains(abilityTypeName);

        /// <summary>Feature C (client-side attack animation) — the ability types whose ATTACK ANIMATION the
        /// client replays via <c>tac.fire.start</c>. SHOOT + GRENADE only (both are <c>ShootAbility</c>):
        /// the client replays the native <c>TacticalLevelController.FireWeaponAtTargetCrt</c> coroutine, which
        /// is the SHOOT animation path. MELEE (<c>BashAbility</c>) is DELIBERATELY EXCLUDED here even though it
        /// is damage-relayable: <c>BashAbility : TacticalAbility</c> (not ShootAbility) animates via its OWN
        /// <c>BashCrt</c> (BashAbility.cs:199), NOT FireWeaponAtTargetCrt — replaying the shoot coroutine for a
        /// bash would mis-bind. Melee animation is a follow-on (it needs a BashCrt-shaped replay).</summary>
        public static readonly string[] FireStartAnimAbilityTypeNames =
        {
            "ShootAbility",   // shoot + grenade (ThrowGrenade_ShootAbilityDef is a ShootAbility)
        };

        private static readonly HashSet<string> _fireStartAnim =
            new HashSet<string>(FireStartAnimAbilityTypeNames, StringComparer.Ordinal);

        /// <summary>Feature C (client-side attack animation): PURE decision whether the HOST should broadcast a
        /// <c>tac.fire.start</c> for an attack of this ability type. Scoped to the SHOOT-coroutine animation set
        /// (<see cref="FireStartAnimAbilityTypeNames"/>) — shoot + grenade. Melee/Move/Overwatch/Reload/Heal/
        /// unknown return false (melee animation is a documented follow-on; the rest never use the shoot path).
        /// NOTE: this is a SUBSET of <see cref="IsRelayable"/> (which also relays melee DAMAGE) — damage and
        /// animation share the shoot subset, and melee damage still replicates via tac.damage with no anim.</summary>
        public static bool ShouldBroadcastFireStart(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _fireStartAnim.Contains(abilityTypeName);
    }
}
