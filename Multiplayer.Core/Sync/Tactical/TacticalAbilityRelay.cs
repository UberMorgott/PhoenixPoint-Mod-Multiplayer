using System;
using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
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

        /// <summary>Type NAMES explicitly kept OFF the 0x87 shoot/melee DAMAGE relay (dedicated path / not a
        /// damage-dealer). Asserted by tests (never in <see cref="IsRelayable"/>) so a future edit can't silently
        /// add a double-handled ability to the 0x87 set. NOTE: Reload/Heal are excluded from 0x87 but DO ride the
        /// SEPARATE 0x8E generic relay (<see cref="RelayableGenericAbilityTypeNames"/>) — the two relays are
        /// disjoint by surface, so an entry here can legitimately appear in the generic set.</summary>
        public static readonly string[] ExcludedAbilityTypeNames =
        {
            "MoveAbility",       // dedicated move command-sync + animation
            "OverwatchAbility",  // dedicated 0x8C/0x8D arm + cone
            "ReloadAbility",     // not a 0x87 damage-dealer — rides the 0x8E generic relay (ammo via 0x8F)
            "HealAbility",       // Health.Add(...) directly (no tac.damage) — rides the 0x8E generic relay (HP via 0x8F)
        };

        private static readonly HashSet<string> _relayable =
            new HashSet<string>(RelayableAbilityTypeNames, StringComparer.Ordinal);

        /// <summary>True when an ability whose runtime type name is <paramref name="abilityTypeName"/> should
        /// ride the generic client→host ability-intent relay. Unknown / excluded types return false (native
        /// path runs, or its dedicated sync handles it).</summary>
        public static bool IsRelayable(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _relayable.Contains(abilityTypeName);

        // ─── TS2: GENERIC (non shoot/melee) ability-intent relay on 0x8E ──────────────────────────────
        /// <summary>Type NAMES of the OWN-soldier abilities (beyond the 0x87 shoot/melee damage-dealers) a
        /// mirroring client SUPPRESSES + relays via the generic <c>tac.intent.generic</c> (0x8E). These are the
        /// abilities whose OUTCOME already rides an ALREADY-SHIPPED surface, so the host runs them
        /// authoritatively and every peer converges with no new outcome path:
        ///   • <c>HealAbility</c>        — target actor's HP mirrors via the 0x8F <c>ActorFieldHealth</c> bit (live).
        ///   • <c>RecoverWillAbility</c> — the caster's WP mirrors via the 0x8F <c>ActorFieldWp</c> bit (live).
        ///   • <c>RallyAbility</c>       — the squad's WP mirrors via 0x8F WP (the ability self-derives its friend
        ///     targets → the host reproduces them identically from the shared sim state; no per-target payload).
        ///   • <c>PsychicScreamAbility</c>— an <c>IDamageDealer</c> AoE: its damage/WP/status ride tac.damage (0x88)
        ///     + 0x8F, exactly like a shot, so the host activate replicates fully.
        ///   • <c>ReloadAbility</c>    — (TS5b) reload-self mirrors the reloaded weapon's charges via the 0x8F
        ///     <c>ActorFieldAmmo</c> bit; reload-others (an ally's weapon) rides the same relay as an ACTOR intent,
        ///     the host re-deriving the ally's weapon+ammo (ReloadAbility.GetReloadOthersWeaponTargets).
        ///   • <c>InteractWithObjectAbility</c> — (TS5b) activates a console/crate StructuralTarget; the applied
        ///     console status mirrors via the 0x8F status delta and objective progress via TS4. Its target is a
        ///     ground OBJECT resolved through the shared actor registry (StructuralTarget is a TacticalActorBase).
        ///   • <c>ExitMissionAbility</c> — (gap-evac) evacuate ONE soldier standing in an unlocked exit zone. It
        ///     SELF-DERIVES the zone from the caster's position (ExitMissionAbility.GetZonesContainingActor) →
        ///     KindNone. Outcome mirrors via already-shipped surfaces: <c>EvacuatedStatus</c> rides the 0x8F inert
        ///     status delta (force-included — its InitVisualState hides the mirror natively), AP via 0x8F, the
        ///     client zone-lock state via the 0x99 ZONE_UNLOCK records, mission end via TS4 0x95 (host-authoritative).
        ///   • <c>EvacuateMountedActorsAbility</c> — (gap-evac) evacuate a vehicle + every mounted passenger in one
        ///     action. Also self-derives its exit zone → KindNone; the host re-resolves the passengers natively
        ///     (Vehicle.Passengers), so the relay carries only the acting VEHICLE actor id. Same outcome surfaces
        ///     (per-passenger + vehicle EvacuatedStatus on 0x8F, TS4 conclusion).
        /// Each of these DECLARES its own <c>Activate(object)</c> override (verified vs the decompile), so the
        /// generic Harmony patch binds each type EXACTLY (no base-method over-patch). Abilities whose outcome
        /// needs a not-yet-shipped surface or has a UI/authority hazard (deploy-turret→pos/SpawnActorAbility base
        /// binding, open-crate→host InventoryAbility.Activate would hijack the HOST into the inventory view +
        /// auto-open already rides the relayed move, drop→object-registry, mind-control→faction/TS5) are
        /// DELIBERATELY absent from this active set — they are KNOWN to <see cref="GenericTargetKindFor"/> (wire +
        /// host build support their kind) but stay OFF the relay until safe (degrade-to-notify meanwhile, spec R1).</summary>
        public static readonly string[] RelayableGenericAbilityTypeNames =
        {
            "HealAbility",              // actor target → HP via 0x8F Health
            "RecoverWillAbility",       // self         → WP via 0x8F Wp
            "RallyAbility",             // self/squad   → WP via 0x8F Wp
            "PsychicScreamAbility",     // self AoE     → damage via tac.damage 0x88
            // TS5b: ground-registry-backed target kinds now shipped (slot + object builds in TacticalCombatSync).
            "ReloadAbility",            // equip-slot target → ammo via 0x8F (self weapon; ally reload-others → actor)
            "InteractWithObjectAbility",// ground-object target → console status via 0x8F + objective via TS4
            // gap-evac: evacuation intents (both DECLARE their own Activate(object) — ExitMissionAbility.cs:32,
            // EvacuateMountedActorsAbility.cs:57 — so the generic patch binds each EXACTLY).
            "ExitMissionAbility",       // self (zone self-derived) → EvacuatedStatus via 0x8F + mission end via TS4
            "EvacuateMountedActorsAbility", // self (vehicle + passengers, host-derived) → same surfaces
        };

        private static readonly HashSet<string> _genericRelayable =
            new HashSet<string>(RelayableGenericAbilityTypeNames, StringComparer.Ordinal);

        /// <summary>True when an OWN-soldier ability rides the generic 0x8E relay (client suppress + send intent →
        /// host authoritative Activate). DISJOINT from <see cref="IsRelayable"/> (the 0x87 shoot/melee set) — an
        /// ability is never handled by both relays (asserted by tests). Unknown / off-list types return false.</summary>
        public static bool IsGenericRelayable(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _genericRelayable.Contains(abilityTypeName);

        /// <summary>PURE ability→target-KIND map for the 0x8E generic intent (the discriminator the client writes +
        /// the host reads to build the <c>TacticalAbilityTarget</c>). Covers the ACTIVE relay set AND the
        /// deferred-but-known abilities (so the wire is complete + the map is unit-testable ahead of activation):
        ///   Heal → actor; RecoverWill / Rally / PsychicScream / ExitMission / EvacuateMounted → none(self);
        ///   DeployTurret / ThrowTurret → pos; Reload → slot; Interact / OpenCrate / DropItem → object;
        ///   MindControl / InstilFrenzy → actor.
        /// Returns <see cref="TacticalGenericIntentCodec.KindUnknown"/> for anything unmapped — the client then
        /// degrades-to-notify (never sends an unresolvable intent). Kept in sync with the audit's ability domains
        /// (D8 heal/support, D10 psychic, D12 deployables, D18 loot, D13 mind-control). NOTE: the spec groups
        /// "heal/rally→actor-or-self"; grounded per-ability it is Heal→actor (needs a specific target) and
        /// Rally→none (self-derives its squad), which this map encodes precisely.</summary>
        public static byte GenericTargetKindFor(string abilityTypeName)
        {
            if (string.IsNullOrEmpty(abilityTypeName)) return TacticalGenericIntentCodec.KindUnknown;
            switch (abilityTypeName)
            {
                // none / self (no target payload — the ability self-derives)
                case "RecoverWillAbility":
                case "RallyAbility":
                case "PsychicScreamAbility":
                // gap-evac: both evac abilities self-derive their exit zone from the caster's position
                // (GetZonesContainingActor over the shared map state), and EvacuateMounted re-derives its
                // passengers from Vehicle.Passengers on the host — nothing to carry beyond the caster.
                case "ExitMissionAbility":
                case "EvacuateMountedActorsAbility":
                    return TacticalGenericIntentCodec.KindNone;
                // actor target
                case "HealAbility":
                case "InstilFrenzyAbility":
                case "MindControlAbility":
                    return TacticalGenericIntentCodec.KindActor;
                // position target (deploy cell)
                case "DeployTurretAbility":
                case "ThrowTurretAbility":
                case "DeployShieldAbility":
                    return TacticalGenericIntentCodec.KindPos;
                // equipment-slot target (reload — TS5 ammo surface)
                case "ReloadAbility":
                    return TacticalGenericIntentCodec.KindSlot;
                // ground-object target (loot / crate — TS5 object registry)
                case "InteractWithObjectAbility":
                case "OpenCrateAbility":
                case "DropItemAbility":
                    return TacticalGenericIntentCodec.KindObject;
                default:
                    return TacticalGenericIntentCodec.KindUnknown;
            }
        }

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
            // TS2: the generic 0x8E relay set is ALSO client-suppressed (the client sends the intent + suppresses),
            // so the SuppressedAbilityViewClearPatch HUD/camera recovery covers them too (they confirm from the
            // pushed UIStateAbilitySelected aim sub-state, whose guarded reset would otherwise no-op → wedge).
            foreach (var n in RelayableGenericAbilityTypeNames) _clientSuppressed.Add(n);
        }

        /// <summary>True when a mirroring client SUPPRESSES this ability's local <c>Activate(object)</c> (relays an
        /// intent to the host instead) — the exact union of the 0x87 shoot/melee relay set (<see cref="IsRelayable"/>:
        /// ShootAbility, BashAbility), the 0x8E generic relay set (<see cref="IsGenericRelayable"/>: Heal / RecoverWill /
        /// Rally / PsychicScream) and the dedicated-patch set (MoveAbility, OverwatchAbility). This is the predicate the
        /// CLIENT view-freeze prefix (<c>SuppressedAbilityViewClearPrefix</c>) reuses to decide whether the native
        /// post-Activate <c>ClearStackAndPush</c> must be skipped (the suppressed Activate left the stack unchanged, so
        /// the native clear would empty the bare control state → HUD-less wedge). Unknown / non-suppressed types
        /// (Reload/EndTurn/… — abilities with no suppression patch) return false — their Activate runs locally and
        /// legitimately drives the view, so it must NOT be intercepted.</summary>
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

        /// <summary>Feature C (RE-TIMING fix): PURE decision whether the HOST should broadcast a
        /// <c>tac.fire.start</c> at the SHOT-ANIMATION-START chokepoint — the host prefix on
        /// <c>TacticalLevelController.FireWeaponAtTargetCrt</c> — for a shot of this ability type + attack type.
        /// The broadcast was MOVED here from the <c>Activate</c>/enqueue prefix so the client's animation replay
        /// coincides with the host's REAL (deferred, post camera-blend) shot rather than firing early at enqueue
        /// (the sequential client-replay→late-host-shot→damage double-play bug). This chokepoint runs ONCE per
        /// shoot action — the whole burst loops inside the single coroutine — so one broadcast covers a burst.
        ///
        /// TRUE for the shoot/grenade set (<see cref="ShouldBroadcastFireStart"/>) on every REAL host attack
        /// type: Regular, Burst, Overwatch (reaction fire — the client now ALSO sees the reaction-shot
        /// animation), ReturnFire, etc. FALSE for the <c>Synced</c> attack type — that is the CLIENT's OWN
        /// damage-less replay of this very coroutine (<see cref="TacticalFireAnimSync"/>), which must never
        /// re-broadcast a fire-start (defense-in-depth alongside the IsHost gate at the call site, or the
        /// animation surface would loop). FALSE for non-shoot abilities / null / empty.</summary>
        public static bool ShouldBroadcastFireStartAtShotStart(string abilityTypeName, string attackTypeName)
            => ShouldBroadcastFireStart(abilityTypeName)
               && !string.Equals(attackTypeName, "Synced", StringComparison.Ordinal);

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
