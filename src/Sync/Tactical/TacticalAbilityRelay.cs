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

        /// <summary>Ability type NAMES whose <c>Activate(object)</c> a mirroring client SUPPRESSES via a dedicated
        /// Harmony prefix that is NOT the generic relay: <c>MoveAbility</c> (<c>MoveAbilityActivatePatch</c>) and
        /// <c>OverwatchAbility</c> (<c>OverwatchAbilityActivatePatch</c>). Combined with
        /// <see cref="RelayableAbilityTypeNames"/> (suppressed by <c>AbilityActivateRelayPatch</c>) this is the FULL
        /// set of client-suppressed activations. <c>ReloadAbility</c>/<c>HealAbility</c> are deliberately ABSENT —
        /// they have NO suppression patch and run locally on the client.</summary>
        private static readonly string[] DedicatedSuppressedAbilityTypeNames =
        {
            "MoveAbility",       // MoveAbilityActivatePatch
            "OverwatchAbility",  // OverwatchAbilityActivatePatch
        };

        private static readonly HashSet<string> _clientSuppressed =
            new HashSet<string>(StringComparer.Ordinal);

        static TacticalAbilityRelay()
        {
            foreach (var n in RelayableAbilityTypeNames) _clientSuppressed.Add(n);
            foreach (var n in DedicatedSuppressedAbilityTypeNames) _clientSuppressed.Add(n);
        }

        /// <summary>True when a mirroring client SUPPRESSES this ability's local <c>Activate(object)</c> (relays an
        /// intent to the host instead) — the exact union of the generic relay set (<see cref="IsRelayable"/>:
        /// ShootAbility, BashAbility) and the dedicated-patch set (MoveAbility, OverwatchAbility). This is the
        /// predicate the CLIENT view-freeze prefix (<c>SuppressedAbilityViewClearPrefix</c>) reuses to decide
        /// whether the native post-Activate <c>ClearStackAndPush</c> must be skipped (the suppressed Activate left
        /// the stack unchanged, so the native clear would empty the bare control state → HUD-less wedge). Unknown /
        /// non-suppressed types (Reload/Heal/EndTurn/…) return false — their Activate runs locally and legitimately
        /// drives the view, so it must NOT be intercepted.</summary>
        public static bool IsClientSuppressedActivation(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _clientSuppressed.Contains(abilityTypeName);

        /// <summary>View-state type NAMES of the PUSHED AIM SUB-STATES the game stacks ON TOP of the bare control
        /// state <c>UIStateCharacterSelected</c> (PushOnTop, UIStateCharacterSelected.cs:682-683/686-688) when an
        /// ability is selected for aiming: <c>UIStateOverwatchAbilitySelected</c> (overwatch) and
        /// <c>UIStateAbilitySelected</c> (bash + other non-shoot abilities). Confirming an ability from one of these
        /// activates with <c>ClearStackAndPush</c>, which the CLIENT mirror suppresses
        /// (<see cref="IsClientSuppressedActivation"/>). The after-suppress re-grey
        /// <c>TacticalView.ResetCharacterSelectedState</c> SELF-GUARDS on <c>CurrentState is UIStateCharacterSelected</c>
        /// (TacticalView.cs:308) → it NO-OPs while one of these sub-states is on top, so the sub-state's
        /// <c>ExitState</c> teardown (overwatch/aim visuals + <c>DoCameraChaseParam</c> camera, restored only by a fresh
        /// CharacterSelected) never runs → HUD-less, camera-locked wedge. (Shoot uses <c>UIStateShoot</c> but confirms
        /// with ReplaceTop — never ClearStackAndPush — so it never reaches the suppress gate.)</summary>
        public static readonly string[] AimSubStateViewNames =
        {
            "UIStateOverwatchAbilitySelected", // overwatch arm (PushOnTop; ExitState restores cones + StopDrawing)
            "UIStateAbilitySelected",          // bash + other non-shoot abilities (PushOnTop)
        };

        private static readonly HashSet<string> _aimSubStates =
            new HashSet<string>(AimSubStateViewNames, StringComparer.Ordinal);

        /// <summary>PURE decision for the CLIENT view-freeze recovery (<c>SuppressedAbilityViewClearPatch</c>): given
        /// the CURRENT tactical view-state type name at the moment the client suppresses a <c>ClearStackAndPush</c>
        /// ability activation, does HUD/camera recovery need a FULL stack-clear (force-exit the pushed aim sub-state by
        /// <c>ClearStackAndPush</c>-ing a fresh <c>UIStateCharacterSelected</c>) rather than the guarded
        /// <c>ResetCharacterSelectedState</c> re-grey? TRUE when the current state is a pushed aim sub-state
        /// (<see cref="AimSubStateViewNames"/> — overwatch/bash), because there the guarded reset NO-OPs and the
        /// sub-state never exits → wedge. FALSE for the bare <c>UIStateCharacterSelected</c> (e.g. a Move confirm,
        /// where the guarded reset works fine) and for null/unknown (conservative: keep today's guarded-reset path).</summary>
        public static bool NeedsFullStackRecovery(string currentViewStateName)
            => !string.IsNullOrEmpty(currentViewStateName) && _aimSubStates.Contains(currentViewStateName);

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

        /// <summary>Feature C (melee) — the ability types whose MELEE swing animation the client replays via
        /// <c>tac.melee.start</c> (0x91). MELEE only: <c>BashAbility : TacticalAbility</c> animates via its OWN
        /// <c>BashCrt</c> (BashAbility.cs:199), NOT <c>FireWeaponAtTargetCrt</c> — so it cannot ride the
        /// fire-start (shoot-coroutine) surface and gets its own. SHOOT/GRENADE stay on tac.fire.start; this
        /// set is the strict complement within the relayable attack set.</summary>
        public static readonly string[] MeleeStartAnimAbilityTypeNames =
        {
            "BashAbility",    // melee swing (animates via BashCrt, not the shoot coroutine)
        };

        private static readonly HashSet<string> _meleeStartAnim =
            new HashSet<string>(MeleeStartAnimAbilityTypeNames, StringComparer.Ordinal);

        /// <summary>Feature C (melee): PURE decision whether the HOST should broadcast a <c>tac.melee.start</c>
        /// for an attack of this ability type. Scoped to the MELEE set (<see cref="MeleeStartAnimAbilityTypeNames"/>)
        /// — BashAbility only. Shoot/grenade (fire-start) / Move / Overwatch / Reload / Heal / unknown return
        /// false. Like <see cref="ShouldBroadcastFireStart"/> this is a SUBSET of <see cref="IsRelayable"/>
        /// (which also relays melee DAMAGE) — damage still replicates via tac.damage, this adds only the swing.
        /// It is DISJOINT from the fire-start set, so a Bash broadcasts ONLY melee-start, never fire-start.</summary>
        public static bool ShouldBroadcastMeleeStart(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _meleeStartAnim.Contains(abilityTypeName);
    }
}
