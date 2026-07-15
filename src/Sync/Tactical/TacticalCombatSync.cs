using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Harmony.Tactical;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// LIVE host-authoritative COMBAT/DAMAGE replication (spec §3, Inc 3a). Mirrors
    /// <see cref="TacticalMoveSync"/>'s shape (intent→host→outcome→all):
    ///   • CLIENT shoot intent: a mirroring client's <c>ShootAbility.Activate</c> is suppressed; instead it
    ///     sends a <c>tac.intent.ability</c> {shooterNetId, abilityDefGuid, targetNetId/targetPos, nonce} to
    ///     the host. The client's local roll chain is ALSO suppressed by the existing <c>FireWeaponPatch</c>,
    ///     so the client never rolls damage — it applies ONLY the host's broadcast result.
    ///   • HOST intent handler: resolve shooter+ability+target, re-invoke the real shot on the host sim.
    ///     The host's authoritative <c>FireWeaponAtTargetCrt</c> rolls → projectile → ApplyDamage as normal.
    ///   • HOST ApplyDamage broadcast: a postfix on <c>TacticalActorBase.ApplyDamage(DamageResult)</c> (the
    ///     funnel ALL damage flows through — shots, melee, overwatch, AI, death cascade) flattens the FINAL
    ///     applied <c>DamageResult</c> and broadcasts <c>tac.damage</c> to all peers.
    ///   • CLIENT damage apply: rebuild the <c>DamageResult</c> (defs resolved by guid via
    ///     <see cref="DefReflection"/>; live actor refs via the registry) and call <c>ApplyDamage</c> on the
    ///     target mirror, guarded by a re-entrancy flag so the client's own ApplyDamage postfix never
    ///     re-broadcasts. The shooter's spent AP/WP are set to the host-carried values.
    ///
    /// All game types are reached by name via <see cref="AccessTools"/> (this layer is the only reflection
    /// boundary). The PURE wire codec is <see cref="TacticalLiveCodec"/> (unit-tested).
    /// </summary>
    public static class TacticalCombatSync
    {
        private static uint _nonceCounter;
        private static uint NextNonce() => unchecked(++_nonceCounter);

        // Re-entrancy guard: true only while the CLIENT is applying a host-received DamageResult, so the
        // ApplyDamage postfix (OnHostApplyDamage) does NOT re-broadcast it. It is also IsHost-gated, but the
        // flag is defense-in-depth (and keeps the host's own apply-of-a-relayed-result clean).
        [ThreadStatic] private static bool _applyingRemote;

        // ─── FIX B (relayed-shot cosmetic-delay strip) ──────────────────────────────────────────────
        // Identity registry of CLIENT-ORIGIN shoots the HOST is currently executing authoritatively. Scopes
        // the inline (B1) + aim-up-skip (B2) strips to relayed shots ONLY — the host's OWN shots are never
        // registered and keep the full native cinematic. Registered in HostOnAbilityIntent before Activate;
        // cleared at the shot's OnPlayingActionEnd. Shared instance (the patches delegate here).
        internal static readonly RelayedHostShotRegistry RelayedShots = new RelayedHostShotRegistry();

        private static MethodInfo _playActionCached;
        private static Type _playActionOwner;

        /// <summary>B1 (called from <c>RelayedShootInlinePatch</c>, the prefix on
        /// <c>TacticalAbility.EnqueueAction</c>): if <paramref name="ability"/> is a REGISTERED relayed shoot,
        /// run its action IMMEDIATELY via <c>PlayAction</c> (the inline branch overwatch/point-blank already
        /// take) instead of the long-range <c>EnqueueAction(soloAfterCurrent)</c> + camera-blend defer, then
        /// return true so the patch SKIPS the native enqueue. Returns false (run native enqueue) for host-own /
        /// non-registered abilities or on any error (fail-open). Does NOT touch the damage roll.</summary>
        public static bool TryRunRelayedShootInline(object ability, object action, object parameter)
        {
            try
            {
                if (ability == null || !RelayedShots.IsAbilityActive(ability)) return false;
                var t = ability.GetType();
                if (_playActionCached == null || _playActionOwner != t)
                {
                    _playActionOwner = t;
                    // public void PlayAction(Func<PlayingAction,IEnumerator<NextUpdate>> action, object parameter,
                    // ActionChannel? channel = null) — single overload on TacticalAbility → name-only is exact.
                    _playActionCached = AccessTools.Method(t, "PlayAction");
                }
                if (_playActionCached == null)
                {
                    Debug.LogError("[Multiplayer][tac] B1: PlayAction not found on " + t.Name + " — relayed shoot stays enqueued");
                    return false;   // fail-open: native enqueue runs
                }
                // channel = null → PlayAction defaults it to ActorActions internally.
                _playActionCached.Invoke(ability, new object[] { action, parameter, null });
                Debug.Log("[Multiplayer][tac] B1 relayed shoot ran INLINE via PlayAction (stripped EnqueueAction+camera-blend defer) on " + t.Name);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] B1 inline reroute failed (falling back to native enqueue): " + ex);
                return false;
            }
        }

        /// <summary>B2 (called from <c>RelayedShootAimSkipPatch</c>, the postfix on
        /// <c>TacticalActor.CurrentlyAiming</c>): true when <paramref name="actor"/> is the shooter of an
        /// in-flight relayed shoot, so the host reports it as already aiming → <c>FireWeaponAtTargetCrt</c>
        /// skips the standing aim-up wait (same native path an already-aiming overwatch reaction shot takes).
        /// False for host-own / non-registered actors → full native aim-up. Damage roll untouched.</summary>
        public static bool ShouldForceAimingForRelayedShot(object actor)
            => actor != null && RelayedShots.IsActorActive(actor);

        /// <summary>Clear a relayed attack's registry entry at its <c>OnPlayingActionEnd</c> (hit / miss /
        /// fumble; shoot AND melee — the full 0x87 set). No-op for host-own / non-registered abilities. MISS
        /// FEEDBACK: if NOTHING landed on the attack's intent TARGET during the window (EffectSeen stays false —
        /// set by <see cref="OnHostApplyDamage"/> for any damage form incl. 0-HP paralysis/viral, and by
        /// <c>StatusApplyEffectMarkPatch</c> for a bare status apply), broadcast tac.shot.result so each client
        /// raises the native Missed bark the damage-neutered client presentation can't produce.</summary>
        public static void EndRelayedShot(object ability)
        {
            var shot = RelayedShots.End(ability);
            if (shot != null && shot.TargetNetId >= 0 && !shot.EffectSeen)
                TacticalFireAnimSync.HostBroadcastShotMiss(shot.Shooter, shot.TargetNetId);
        }

        // ─── CLIENT: intercept the local gameplay ability, send intent, suppress ───────────────────
        /// <summary>
        /// CLIENT (mirroring) entry from <c>AbilityActivateRelayPatch</c> (the generic per-subclass
        /// <c>Activate(object)</c> prefix on the RELAYABLE ability types — see
        /// <see cref="TacticalAbilityRelay"/>: ShootAbility[shoot+grenade] / BashAbility[melee] /
        /// HealAbility). Captures {actorNetId, abilityDefGuid, target(actor/pos)} and sends ONE generic
        /// <c>tac.intent.ability</c> (surface 0x87, reused) to the host. Returns false to SUPPRESS the local
        /// activation (the host runs the authoritative ability + its outcome surfaces — tac.damage etc. —
        /// replicate it). Returns true (let it run) only when this is NOT a mirroring client; a mirroring
        /// client still suppresses on a read-failure — it must never execute the ability locally.
        ///
        /// THE FIX (Inc T2): the def guid is read from the base <c>Ability.BaseDef</c> property — the actual
        /// def carrying <c>BaseDef.Guid</c> — NOT a non-existent <c>Def</c> member (the old read returned null
        /// → empty guid → the shot/grenade was suppressed WITHOUT ever sending the intent → host never fired).
        /// </summary>
        public static bool ClientInterceptAbility(object ability, object parameter)
        {
            if (!TacticalDeploySync.IsClientMirroring)
            {
                // HOST / single-player / non-mirror: the native ability runs unchanged. Feature C (melee): this
                // host choke begins a MELEE swing — broadcast tac.melee.start NOW so every client mirror plays
                // the swing CONCURRENTLY with the host. Melee animates INLINE via BashCrt (no enqueue/camera-blend
                // defer), so enqueue-time is the right moment for it.
                //
                // SHOOT/grenade fire-start is NO LONGER broadcast here. It was RE-TIMED to the host prefix on
                // FireWeaponAtTargetCrt (FireWeaponPatch) — the moment the host's REAL shot animation actually
                // begins. A long-range sniper shot is EnqueueAction(soloAfterCurrent)+camera-blend DEFERRED, so
                // broadcasting at this Activate/enqueue prefix made the client replay the shot early (before the
                // host's visible shot) → a sequential double-play. Broadcasting from the shot coroutine fixes the
                // timing for OWN shots, relayed client intents, AND overwatch/return-fire reactions in one place.
                // Animation-only; DAMAGE rides tac.damage. Fail-open: HostBroadcastMeleeStart logs + swallows,
                // never blocking the native attack.
                TacticalMeleeAnimSync.HostBroadcastMeleeStart(ability, parameter);
                return true;
            }
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return true;

            try
            {
                object actor = GetProp(ability, "TacticalActorBase");
                if (actor == null)
                {
                    Debug.LogError("[Multiplayer][tac] ability intent: no actor — suppressing local activation");
                    return false;
                }
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0)
                {
                    Debug.LogError("[Multiplayer][tac] ability intent: unknown actor netId — suppressing local activation");
                    return false;
                }
                // rca-evac-command-leak: never relay commands for a hidden evacuated mirror (still selectable).
                if (TacticalActorLifecycleSync.IsNetIdEvacuated(actorNetId))
                {
                    Debug.Log("[Multiplayer][tac] ability intent for evacuated netId " + actorNetId + " — not sent (soldier already evacuated)");
                    return false;
                }

                // FIX: read the def guid off Ability.BaseDef (the real guid-bearing def), not "Def" (null).
                string abilityGuid = DefReflection.GetGuid(GetProp(ability, "BaseDef"));
                if (string.IsNullOrEmpty(abilityGuid))
                {
                    // Should never happen now BaseDef is read; kept so we never SILENTLY suppress again.
                    Debug.LogError("[Multiplayer][tac] ability intent: no ability def guid (type=" +
                                   ability.GetType().Name + ") — suppressing local activation");
                    return false;
                }

                ReadTarget(parameter, out int targetNetId, out Vector3 targetPos);

                // FIX A (faithful shoot-aim relay): capture the player's EXACT selected body part (the parameter's
                // DamageReceiver) as an index into the target actor's snapper enumeration, so the host reproduces
                // the same limb + cover-aware ShootFromPos via native generation rather than snapping to the part
                // nearest center-of-mass. -1 = no selected body part (bare-ground / free shot / no-snap weapon).
                int bodyPartId = ComputeBodyPartId(parameter, targetNetId);

                byte[] payload = TacticalLiveCodec.EncodeIntentAbility(
                    actorNetId, abilityGuid, targetNetId, targetPos.x, targetPos.y, targetPos.z, bodyPartId, NextNonce());
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacIntentAbility, payload);
                Debug.Log("[Multiplayer][tac] CLIENT sent tac.intent.ability actor=" + actorNetId +
                          " type=" + ability.GetType().Name + " ability=" + abilityGuid +
                          " targetNetId=" + targetNetId + " bodyPartId=" + bodyPartId +
                          " pos=(" + targetPos.x.ToString("0.0") + "," + targetPos.y.ToString("0.0") + "," + targetPos.z.ToString("0.0") + ")");

                // NEW SHOOT CANON (origin-native shot): a SHOOT ability (ShootAbility = shoot+grenade) runs
                // NATIVELY on the ORIGIN client — real Activate → native camera + animation — while the host keeps
                // OUTCOME authority (damage via tac.damage, AP/WP via the 0x8F absolute flush). We still record the
                // shooter so the host's echoed tac.fire.start for it is de-duped (ConsumeIfPredicted) → the shot
                // animates EXACTLY ONCE (the native one, no predicted replay), and open the OriginNativeShot window
                // so the damage/ammo/projectile-event neuters fire LOCALLY (host stays sole authority) while the
                // native aim camera is left free (FireCameraHintGuardPatch does NOT cover this window). The window is
                // closed at ShootAbility.OnPlayingActionEnd (RelayedShootEndPatch). Non-shoot suppressed abilities
                // keep the old suppress-and-relay behavior (return false).
                if (TacticalAbilityRelay.ShouldBroadcastFireStart(ability.GetType().Name))
                {
                    TacticalFireAnimSync.RecordPredictedShooter(actorNetId);
                    FireReplayGate.EnterOriginNativeShot();
                    Debug.Log("[Multiplayer][tac] CLIENT origin-native shot (native camera+anim, host outcome authority) actor=" + actorNetId);
                    return true;    // run the shot NATIVELY on the origin client
                }
                return false;   // non-shoot: suppress local activation (host is authoritative)
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ClientInterceptAbility failed: " + ex);
                return false;   // a mirroring client must not run a local ability even on error
            }
        }

        // ─── HOST: a client shoot intent arrived → run the real shot ──────────────────────────────
        /// <summary>HOST inbound (<c>tac.intent.ability</c>): resolve shooter→ability(by guid)→target, then
        /// invoke the real shot on the host sim. The host's authoritative roll chain fires
        /// (FireWeaponAtTargetCrt → ApplyDamage), and each ApplyDamage broadcasts a tac.damage. No-op
        /// off-host / off-session.</summary>
        public static void HostOnAbilityIntent(ulong senderPeerId, byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeIntentAbility(payload, out var intent)) { Debug.LogError("[Multiplayer][tac] shoot intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(senderPeerId, TacticalSurfaceIds.TacIntentAbility, intent.Nonce)) return;

            object shooter = TacticalDeploySync.ResolveLiveActor(intent.ShooterNetId);
            Debug.Log("[Multiplayer][tac][DIAG] HOSTINTENT decoded shooter=" + intent.ShooterNetId +
                      " ability=" + intent.AbilityDefGuid + " targetNetId=" + intent.TargetNetId +
                      " shooterResolved=" + (shooter != null));
            if (shooter == null) { Debug.LogError("[Multiplayer][tac] shoot intent: no shooter for netId " + intent.ShooterNetId); return; }
            // rca-evac-command-leak (authoritative belt): an evacuated actor's animator is natively disabled —
            // a relayed Activate never finishes and wedges the host view at game-over. Same gate as move/generic.
            if (TacticalActorLifecycleSync.HasEvacuatedStatus(shooter))
            {
                Debug.LogError("[Multiplayer][tac] shoot intent for EVACUATED netId " + intent.ShooterNetId + " — dropped (client selection leak)");
                return;
            }

            try
            {
                object ability = ResolveAbilityByGuid(shooter, intent.AbilityDefGuid);
                if (ability == null) { Debug.LogError("[Multiplayer][tac] shoot intent: shooter has no ability with guid " + intent.AbilityDefGuid); return; }

                object target = BuildShootTarget(intent);
                if (target == null) { Debug.LogError("[Multiplayer][tac] shoot intent: could not build target"); return; }

                // BODY-PART SNAP (combat-bug fix, limb-faithful damage matching a host-LOCAL shot). The aim-point seed in
                // BuildShootTarget already guarantees a non-NaN center-of-mass aim (no more feet/Y=0 ground hit). This
                // refines it to the NEAREST body part by running the ability's OWN snapper — ShootAbility.GetShootTarget
                // (ShootAbility.cs:194; SnapToBodyparts at :198-224 reads target.PositionToApply, our non-NaN seed, to
                // pick the nearest TacticalItem) — so the relayed shot carries a TacticalItem exactly like the host's own
                // click. Best-effort + fail-safe: a non-ShootAbility (BashAbility melee) has no GetShootTarget → skipped;
                // any throw/null leaves the aim-point actor target (which still hits the body). See TrySnapShootTarget.
                object snapped = TrySnapShootTarget(ability, target);
                if (snapped != null) target = snapped;

                // DIAG (host combat path): confirm the relayed shot now aims at a BODY PART / non-zero-Y point rather than
                // the actor feet. Logs the snapped TacticalItem (or "noItem") + the resolved working position.
                LogShootAim(target);

                // public override void Activate(object parameter) — runs the real ability (shoot/grenade/
                // melee — the relayable set, all of which deal damage via ApplyDamage). The host is NOT
                // mirroring, so ClientInterceptAbility passes through; the ability's outcome → ApplyDamage
                // funnel then broadcasts tac.damage per hit (and the T1 per-actor state-delta carries the
                // resulting AP/WP/status). NOTE: heal is deliberately NOT relayed (it uses Health.Add directly,
                // raising no tac.damage, and no health surface exists yet) — see TacticalAbilityRelay.
                var activate = AccessTools.Method(ability.GetType(), "Activate", new[] { typeof(object) });
                if (activate == null) { Debug.LogError("[Multiplayer][tac] ability intent: Activate(object) not found"); return; }

                // Register this CLIENT-ORIGIN attack (the full 0x87 set: shoot + melee) for the miss-feedback
                // verdict, and — SHOOT only (FIX B, cosmetic-delay strip) — so the host runs it INLINE (B1, read
                // synchronously by the EnqueueAction prefix during Activate) and SKIPS the aim-up wait (B2, read
                // later by the CurrentlyAiming postfix inside the deferred shot coroutine). Melee (BashAbility)
                // already runs inline (PlayAction) and never reaches FireWeaponAtTargetCrt's aim-up, so it gets
                // verdict tracking only. The host's OWN attacks are never registered → full native cinematic.
                // Cleared at OnPlayingActionEnd (RelayedAttackEndPatch on the TacticalAbility base virtual).
                bool relayedShoot = TacticalAbilityRelay.ShouldBroadcastFireStart(ability.GetType().Name);
                bool relayedMelee = TacticalAbilityRelay.ShouldBroadcastMeleeStart(ability.GetType().Name);
                if (relayedShoot || relayedMelee)
                {
                    // Carry the intent's target netId so EndRelayedShot can broadcast the miss feedback when
                    // NOTHING landed on that target (a bare-position/ground target rides -1 → no feedback).
                    // Melee (BashAbility, incl. stun batons) registers VERDICT-ONLY (cosmeticStrips=false):
                    // it already animates inline via BashCrt, so B1/B2 must stay shoot-scoped.
                    RelayedShots.Begin(ability, shooter, intent.TargetNetId, cosmeticStrips: relayedShoot);
                    Debug.Log("[Multiplayer][tac] registered relayed attack (verdict" +
                              (relayedShoot ? "+B1/B2 inline+aim-skip" : "") + ") actor=" + intent.ShooterNetId +
                              " ability=" + ability.GetType().Name);
                }

                // BUG2: hold the host camera-follow guard across the relayed client ability's activate.Invoke so the
                // synchronous Activate camera hint can't fly the host camera to the client's actor. Covers melee/non-
                // shoot; harmlessly overlaps RelayedShots (HostRelayedShotActive) for shoot. try/finally pops on throw.
                FireReplayGate.EnterHostApply();
                try { activate.Invoke(ability, new[] { target }); }
                finally { FireReplayGate.ExitHostApply(); }
                Debug.Log("[Multiplayer][tac] HOST executed ability " + ability.GetType().Name +
                          " (guid=" + intent.AbilityDefGuid + ") for actor " + intent.ShooterNetId);

                // TASK 2 (host staleness): the host executed this relayed CLIENT action programmatically and never
                // UI-selected the soldier, so if the host UI currently has THIS actor selected its ability bar stays
                // lit despite the AP just spent (the cost is paid synchronously in Activate). Re-grey it the same
                // direct, state-independent way as the client. No-op unless the host has THIS actor selected.
                TacticalActorStateSync.RefreshHostSelectedBarForActor(intent.ShooterNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnAbilityIntent exec failed: " + ex); }
        }

        // ─── TS2: GENERIC (non shoot/melee) ability-intent relay (0x8E) ─────────────────────────────
        /// <summary>CLIENT (mirroring) entry from <c>GenericAbilityRelayPatch</c> (the <c>Activate(object)</c>
        /// prefix on the generic allowlist types — Heal/RecoverWill/Rally/PsychicScream, see
        /// <see cref="TacticalAbilityRelay.RelayableGenericAbilityTypeNames"/>). Captures {casterNetId,
        /// abilityDefGuid, target-by-kind} and relays ONE <c>tac.intent.generic</c> (0x8E) to the host, then
        /// SUPPRESSES the local activation (returns false) so the frozen client never runs the ability — the host
        /// runs it authoritatively and its outcome rides 0x8F (AP/WP/Health/status) + tac.damage + TS1 spawn.
        /// Returns true (run native) only when this is NOT a mirroring client, or the runtime type is not actually
        /// on the active allowlist (defends against the Harmony patch binding an inherited base <c>Activate</c> for
        /// an off-list sibling). A mirroring client SUPPRESSES even on a read-failure / unknown-kind
        /// (degrade-to-notify) — it must never execute the ability on the frozen sim.</summary>
        public static bool ClientInterceptGenericAbility(object ability, object parameter)
        {
            // rca-jetjump OBSERVER replay: this Activate is a NON-origin peer re-running a host-broadcast
            // origin-native move (JetJump) for a NON-owned actor (TacticalMoveSync.ClientOnNativeMove) — run it
            // NATIVELY, never relay a spurious intent for it. Must be the FIRST check (the observer IS a mirroring
            // client, so it would otherwise fall into the relay path below).
            if (TacticalMoveSync.NativeMoveReplayActive) return true;
            if (!TacticalDeploySync.IsClientMirroring)
            {
                // HOST / single-player: the native ability runs unchanged. rca-jetjump: an ORIGIN-NATIVE move
                // (JetJump) STARTING on the host — own click, enemy AI, OR a relayed client intent, all via the
                // patched JetJumpAbility.Activate — broadcasts tac.nativemove so every NON-origin peer plays the
                // real flight animation (not the 4 Hz 0x8F snap arc). Self-gates on IsOriginNativeMove + IsHost, so
                // a non-move generic (heal/reload/…) or single-player is a clean no-op. Fail-open (logs+swallows).
                TacticalMoveSync.HostBroadcastOriginNativeMove(ability, parameter);
                // Feature C (heal): a HEAL STARTING on the host — own click OR a relayed client heal re-Activated in
                // HostOnGenericIntent, both via this one branch — broadcasts tac.heal.start so every peer replays the
                // native heal animation (HP/charge stay host-authoritative; the replay neuters them). Self-gates on
                // HealAbility + IsHost, so any other generic (or single-player) is a clean no-op. Fail-open.
                TacticalHealAnimSync.HostBroadcastHealStart(ability, parameter);
                return true;   // host / single-player: native runs
            }
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return true;

            string typeName = ability != null ? ability.GetType().Name : "";
            // Only intercept abilities actually on the ACTIVE generic allowlist. (If the patch bound an inherited
            // base Activate shared with an off-list sibling, that sibling runs native — never suppressed.)
            if (!TacticalAbilityRelay.IsGenericRelayable(typeName)) return true;

            // gap-ability-allowlist-wave2: RepositionAbility is BOTH the player-clicked Dash (def TriggerOnDamage
            // = false) AND a damage-TRIGGERED hurt reaction (Umbra Vanish etc). A reaction-triggered Activate on
            // the mirror is the native reaction queue firing off the MIRRORED health StatChange — NOT a client
            // intent; the host runs its OWN authoritative reaction, so relaying would DOUBLE-Activate it. Pass
            // through instead (return true): HurtReactionActivateSuppressPatch (BUG3b, a second prefix on the
            // same sealed base Activate — all prefixes run) still suppresses the local reaction on the mirror.
            // Unreadable def → treated as reaction (never risk the double-fire; worst case = today's no-relay).
            if (TacticalAbilityRelay.RelaysOnlyDirectActivation(typeName) && IsReactionTriggeredDef(ability))
                return true;

            try
            {
                object actor = GetProp(ability, "TacticalActorBase");
                if (actor == null)
                {
                    NotifyGenericDegrade(typeName, "no caster actor");
                    return false;
                }
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0)
                {
                    NotifyGenericDegrade(typeName, "unknown caster netId");
                    return false;
                }
                // rca-evac-command-leak: never relay commands for a hidden evacuated mirror (still selectable).
                if (TacticalActorLifecycleSync.IsNetIdEvacuated(actorNetId))
                {
                    Debug.Log("[Multiplayer][tac] generic intent for evacuated netId " + actorNetId + " — not sent (soldier already evacuated)");
                    return false;
                }
                string abilityGuid = DefReflection.GetGuid(GetProp(ability, "BaseDef"));
                if (string.IsNullOrEmpty(abilityGuid))
                {
                    NotifyGenericDegrade(typeName, "no ability def guid");
                    return false;
                }

                byte kind = TacticalAbilityRelay.GenericTargetKindFor(typeName);
                if (!TryBuildGenericIntent(actorNetId, abilityGuid, typeName, kind, parameter, out var intent))
                {
                    NotifyGenericDegrade(typeName, "unsupported/unreadable target kind=" + kind);
                    return false;   // suppress + notify (never run on the frozen sim), never send a junk intent
                }

                byte[] payload = TacticalGenericIntentCodec.Encode(intent);
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacIntentGeneric, payload);
                Debug.Log("[Multiplayer][tac] CLIENT sent tac.intent.generic actor=" + actorNetId +
                          " type=" + typeName + " ability=" + abilityGuid + " kind=" + kind +
                          " targetNetId=" + intent.TargetNetId + " nonce=" + intent.Nonce);

                // rca-jetjump (ORIGIN-NATIVE special move, the 815634b shoot-canon pattern): the intent is
                // relayed (above) AND the origin runs the real Activate — native flight animation instead of
                // 4 Hz teleport snaps. Host keeps outcome authority: AP/WP local spend is overwritten by the
                // 0x8F absolute flush (the same guard the shoot canon uses — these moves have no ammo/damage
                // funnel); the 0x8F pos/facing mirror is suppressed for this actor while the native move plays
                // (latest host pos recorded) and reconciled at OnPlayingActionEnd (OriginNativeMovePatches).
                if (TacticalAbilityRelay.IsOriginNativeMove(typeName))
                {
                    TacticalMoveSync.OpenOriginNativeMoveWindow(actorNetId);
                    Debug.Log("[Multiplayer][tac] CLIENT origin-native move (native flight anim, host outcome authority) actor=" +
                              actorNetId + " type=" + typeName);
                    return true;    // run the move NATIVELY on the origin client
                }
                return false;   // suppress local activation (host is authoritative)
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ClientInterceptGenericAbility failed: " + ex);
                return false;   // a mirroring client must not run a local ability even on error
            }
        }

        /// <summary>Build the wire intent from the client's <c>TacticalAbilityTarget</c> parameter per target-kind
        /// (TS5b completes Slot + Object; gap-turret-crate-loot adds the deploy/drop/crate shapes). None → no target
        /// (self-derived). Actor → the target actor's netId (must resolve). Pos → PositionToApply, with the shield
        /// FACING fold (a Direction-only target anchors as casterPos+direction — <c>ChoosePosEncoding</c>). Slot →
        /// RELOAD: reload-others (an ally actor) rides an ACTOR intent so the host re-derives weapon+ammo, while
        /// reload-self carries the caster's own weapon equipment-slot index (or -1 = the selected weapon); DROP:
        /// the caster's own equipped item's slot index (-1 = SelectedEquipment, kept in sync by the 0x8B
        /// equip-select mirror). Object → INTERACT/CRATE: the ground object's shared registry netId (a crate
        /// auto-open passes the raw <c>CrateComponent</c> — resolved via its sibling <c>ItemContainer</c>).
        /// Returns false for a KNOWN kind whose target could not be read (caller degrades-to-notify).</summary>
        private static bool TryBuildGenericIntent(int actorNetId, string guid, string typeName, byte kind,
            object parameter, out TacticalGenericIntentCodec.GenericIntent intent)
        {
            intent = default(TacticalGenericIntentCodec.GenericIntent);
            uint nonce = NextNonce();
            switch (kind)
            {
                case TacticalGenericIntentCodec.KindNone:
                    intent = TacticalGenericIntentCodec.None(actorNetId, guid, nonce);
                    return true;
                case TacticalGenericIntentCodec.KindActor:
                {
                    ReadTarget(parameter, out int targetNetId, out _);
                    if (targetNetId < 0) return false;   // actor kind needs a resolvable target actor
                    intent = TacticalGenericIntentCodec.Actor(actorNetId, guid, targetNetId, nonce);
                    return true;
                }
                case TacticalGenericIntentCodec.KindPos:
                {
                    // gap-turret-crate-loot: encode per the pure decision — an exact PositionToApply (deploy cell)
                    // verbatim; a Direction-only shield target as casterPos+direction (the host re-derives the same
                    // facing from pos−casterPos, DeployShieldAbility.cs:36); neither → casterPos (native forward
                    // fallback). Only the caster-anchored modes need a resolvable caster (else degrade).
                    ReadPosAndDirection(parameter, out bool hasPos, out Vector3 pos, out bool hasDir, out Vector3 dir);
                    byte mode = TacticalAbilityRelay.ChoosePosEncoding(hasPos, hasDir);
                    Vector3 send = pos;
                    if (mode != TacticalAbilityRelay.PosEncodeUsePosition)
                    {
                        object caster = TacticalDeploySync.ResolveLiveActor(actorNetId);
                        if (!(GetProp(caster, "Pos") is Vector3 casterPos)) return false;   // no anchor → degrade
                        send = mode == TacticalAbilityRelay.PosEncodeCasterPlusDirection ? casterPos + dir : casterPos;
                    }
                    intent = TacticalGenericIntentCodec.Pos(actorNetId, guid, send.x, send.y, send.z, nonce);
                    return true;
                }
                case TacticalGenericIntentCodec.KindSlot:
                {
                    // Reload — two native shapes. reload-OTHERS (ability def Origin.TargetResult==Actor) targets an
                    // ALLY actor; the host re-derives that ally's weapon+ammo from GetReloadOthersWeaponTargets, so
                    // it needs ONLY the target actor → emit an ACTOR intent (reuses the KindActor host build).
                    // reload-SELF carries the caster's OWN weapon equipment-slot index (or -1 = the currently
                    // selected weapon, which ReloadCrt.ChooseEquipmentAndAmmo self-derives). The actor branch is
                    // reload-ONLY: a DROP target never carries an Actor, and must never be re-shaped as one.
                    if (string.Equals(typeName, "ReloadAbility", StringComparison.Ordinal))
                    {
                        ReadTarget(parameter, out int reloadTargetNetId, out _);
                        if (reloadTargetNetId >= 0 && reloadTargetNetId != actorNetId)
                        {
                            intent = TacticalGenericIntentCodec.Actor(actorNetId, guid, reloadTargetNetId, nonce);
                            return true;
                        }
                    }
                    // DROP (gap-turret-crate-loot) reads the same slot shape: its target carries the item in
                    // TacticalAbilityTarget.TacticalItem (DropItemAbility.cs:18-36) — ReadEquipmentSlotIndex
                    // resolves Equipment ?? TacticalItem to the caster's Equipments index; -1 = SelectedEquipment.
                    int slotIndex = ReadEquipmentSlotIndex(actorNetId, parameter);
                    intent = TacticalGenericIntentCodec.Slot(actorNetId, guid, slotIndex, nonce);
                    return true;
                }
                case TacticalGenericIntentCodec.KindObject:
                {
                    // Interact / crate — resolve the ground OBJECT actor's shared registry netId. The object is a
                    // TacticalActorBase (StructuralTarget for a console/crate, ItemContainer for ground loot), so it
                    // rides the SAME actor registry as any mirror actor: deploy-placed interactables register in the
                    // deploy actor-table, dropped loot via the TS1 spawn mirror. A client that never learned the
                    // object's netId (not on this side) returns -1 → false → degrade-to-notify (never guess an id).
                    int objNetId = ReadObjectNetId(parameter);
                    if (objNetId < 0) return false;
                    intent = TacticalGenericIntentCodec.Object(actorNetId, guid, objNetId, nonce);
                    return true;
                }
                default:
                    return false;   // KindUnknown → unmapped → degrade-to-notify
            }
        }

        /// <summary>HOST inbound (<c>tac.intent.generic</c> 0x8E): peer-dedup → resolve caster + ability(by guid) →
        /// build the target per kind → <c>Activate</c> authoritatively (held under the host camera-follow guard so
        /// the relayed activation can't fly the host camera to the client's actor). The outcome replicates via the
        /// existing surfaces (0x8F AP/WP/Health/status flush, tac.damage, TS1 spawn). Unknown/unsupported kind or
        /// unresolvable guid → degrade-to-notify (no crash, no local run — the client already suppressed). No-op
        /// off-host / off-session.</summary>
        public static void HostOnGenericIntent(ulong senderPeerId, byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalGenericIntentCodec.TryDecode(payload, out var intent)) { Debug.LogError("[Multiplayer][tac] generic intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(senderPeerId, TacticalSurfaceIds.TacIntentGeneric, intent.Nonce)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(intent.ActorNetId);
            if (actor == null) { Debug.LogError("[Multiplayer][tac] generic intent: no caster for netId " + intent.ActorNetId); return; }
            // rca-evac-command-leak (authoritative belt): same gate as move/shoot — see HasEvacuatedStatus.
            if (TacticalActorLifecycleSync.HasEvacuatedStatus(actor))
            {
                Debug.LogError("[Multiplayer][tac] generic intent for EVACUATED netId " + intent.ActorNetId + " — dropped (client selection leak)");
                return;
            }

            try
            {
                object ability = ResolveAbilityByGuid(actor, intent.AbilityDefGuid);
                if (ability == null) { Debug.LogError("[Multiplayer][tac] generic intent: caster has no ability with guid " + intent.AbilityDefGuid); return; }

                // Defense-in-depth: only Activate an ability that is on the generic allowlist (never blindly run an
                // arbitrary relayed guid). A guid that resolves to an off-list ability → degrade-to-notify.
                string typeName = ability.GetType().Name;
                if (!TacticalAbilityRelay.IsGenericRelayable(typeName))
                {
                    NotifyGenericDegrade(typeName, "resolved ability not on the generic allowlist");
                    return;
                }

                // gap-turret-crate-loot: the client mirror's equipment/inventory is NOT mirrored mid-mission, so
                // its stale UI can re-send an intent whose item the host already consumed (deploy/drop). Native
                // Activate never re-checks the disabled state — a blind invoke would NRE mid-coroutine
                // (DeployTurretAbility.cs:22 SelectedEquipment deref) or duplicate the action. Scoped to the new
                // set (RequiresHostEnabledCheck) so shipped relays stay byte-identical; fail-open on reflection.
                if (TacticalAbilityRelay.RequiresHostEnabledCheck(typeName) && !HostAbilityEnabled(ability))
                {
                    NotifyGenericDegrade(typeName, "ability disabled on host (stale client state?) — intent dropped");
                    return;
                }

                // gap-ability-allowlist-wave2 defense-in-depth (mirror of the client gate): a direct-activation-
                // only ability whose HOST def says TriggerOnDamage is a hurt REACTION the host already runs
                // authoritatively off its own damage — a relayed Activate would double-fire it. Drop the intent.
                if (TacticalAbilityRelay.RelaysOnlyDirectActivation(typeName) && IsReactionTriggeredDef(ability))
                {
                    NotifyGenericDegrade(typeName, "reaction-triggered def relayed (client regression?) — intent dropped");
                    return;
                }

                if (!TryBuildGenericTarget(typeName, intent, out object target))
                {
                    NotifyGenericDegrade(typeName, "unsupported target kind=" + intent.TargetKind + " on host");
                    return;
                }

                var activate = AccessTools.Method(ability.GetType(), "Activate", new[] { typeof(object) });
                if (activate == null) { Debug.LogError("[Multiplayer][tac] generic intent: Activate(object) not found on " + typeName); return; }

                // gap-turret-crate-loot: identify the item a relayed DROP is about to drop BEFORE Activate (the
                // slot target carries it; the self-derive path drops SelectedEquipment) so the container it lands
                // in can be content-refreshed afterwards (its 0x92 blob was serialized EMPTY at EnterPlay).
                object dropItem = null;
                if (string.Equals(typeName, "DropItemAbility", StringComparison.Ordinal))
                    dropItem = (target != null ? GetField(target, "TacticalItem") : null)
                               ?? GetProp(GetProp(actor, "Equipments"), "SelectedEquipment");

                // Hold the host camera-follow guard across the relayed Activate (BUG2 pattern from the shoot relay):
                // the synchronous Activate camera hint must not fly the host camera to the client's actor.
                FireReplayGate.EnterHostApply();
                try { activate.Invoke(ability, new[] { target }); }
                finally { FireReplayGate.ExitHostApply(); }
                Debug.Log("[Multiplayer][tac] HOST executed generic ability " + typeName +
                          " (guid=" + intent.AbilityDefGuid + ") for actor " + intent.ActorNetId + " kind=" + intent.TargetKind);

                if (dropItem != null) HostRefreshDroppedItemContainer(dropItem);

                // The host ran this relayed CLIENT action programmatically (never UI-selected the soldier), so re-grey
                // its ability bar if the host UI currently has it selected — same as the shoot relay. No-op otherwise.
                TacticalActorStateSync.RefreshHostSelectedBarForActor(intent.ActorNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnGenericIntent exec failed: " + ex); }
        }

        /// <summary>Build the <c>TacticalAbilityTarget</c> the host passes to a relayed generic <c>Activate</c>,
        /// per target-kind (TS5b completes Slot + Object; gap-turret-crate-loot adds the drop/crate shapes).
        /// None → null (the ability self-derives). Actor → an actor target (Actor + DamageReceiver + aim-point).
        /// Pos → a bare-position target. Slot → RELOAD-self: a target carrying the caster's own weapon Equipment
        /// at the slot index (or a BARE target for slot &lt; 0 → ReloadCrt self-derives the selected weapon);
        /// DROP: a target carrying the caster's equipped item in <c>TacticalItem</c> (slot &lt; 0 → null target,
        /// the native Activate self-derives SelectedEquipment — DropItemAbility.cs:19-35 — which 0x8B keeps in
        /// sync; an unresolvable slot ≥ 0 degrades — never drop a guessed item). Object → INTERACT: an actor
        /// target (the ability reads target.Actor as the StructuralTarget); OPEN-CRATE: the raw
        /// <c>CrateComponent</c> itself (the OpenCrate coroutine casts <c>action.Param</c> directly,
        /// OpenCrateAbility.cs:57 — NOT a TacticalAbilityTarget). Returns false for a KNOWN kind whose target
        /// could not be resolved (already left play / not on this side), so the caller degrades-to-notify.</summary>
        private static bool TryBuildGenericTarget(string typeName, TacticalGenericIntentCodec.GenericIntent intent,
            out object target)
        {
            target = null;
            switch (intent.TargetKind)
            {
                case TacticalGenericIntentCodec.KindNone:
                    target = null;   // Activate(null) — RecoverWill/Rally/PsychicScream/RetrieveShield self-derive
                    return true;
                case TacticalGenericIntentCodec.KindActor:
                {
                    object targetActor = TacticalDeploySync.ResolveLiveActor(intent.TargetNetId);
                    if (targetActor == null) return false;
                    target = BuildActorAbilityTarget(targetActor);
                    return target != null;
                }
                case TacticalGenericIntentCodec.KindPos:
                    target = BuildPositionAbilityTarget(new Vector3(intent.TX, intent.TY, intent.TZ));
                    return target != null;
                case TacticalGenericIntentCodec.KindSlot:
                {
                    object caster = TacticalDeploySync.ResolveLiveActor(intent.ActorNetId);
                    if (caster == null) return false;
                    if (string.Equals(typeName, "DropItemAbility", StringComparison.Ordinal))
                    {
                        if (intent.SlotIndex < 0) { target = null; return true; }   // self-derive SelectedEquipment
                        // KNOWN LIMIT (review 56558d2): the positional index trusts client/host Equipments order,
                        // but equipment is NOT mirrored mid-mission — a host-side mutation on the same soldier
                        // (deploy consumed the turret item, retrieve added one) can skew a still-valid index onto
                        // a DIFFERENT item, and drop is destructive. Mitigations: -1/self-derive is the common
                        // path, unresolvable degrades below. FOLLOW-UP (with the inventory-transfer intent
                        // surface): carry the ItemDef guid alongside the index and host-match by def.
                        object item = EquipmentAtSlot(caster, intent.SlotIndex);
                        if (item == null) return false;   // never drop a guessed item → degrade
                        target = BuildDropItemTarget(item);
                        return target != null;
                    }
                    // Reload-self: target the caster's OWN weapon Equipment at the slot index. slot < 0 ⇒ a bare
                    // (empty) target that ReloadCrt.ChooseEquipmentAndAmmo self-derives from the selected weapon.
                    // NEVER null for a resolvable caster (ReloadCrt dereferences action.Param for a self-reload).
                    target = BuildReloadSlotTarget(caster, intent.SlotIndex);
                    return target != null;
                }
                case TacticalGenericIntentCodec.KindObject:
                {
                    object obj = TacticalDeploySync.ResolveLiveActor(intent.TargetNetId);
                    if (obj == null) return false;
                    if (string.Equals(typeName, "OpenCrateAbility", StringComparison.Ordinal))
                    {
                        // OpenCrate's Activate param is the raw CrateComponent on the container's GameObject.
                        target = ResolveCrateComponent(obj);
                        return target != null;
                    }
                    // Interact: hand the ability an actor target (target.Actor = the StructuralTarget).
                    target = BuildActorAbilityTarget(obj);
                    return target != null;
                }
                default:
                    return false;   // KindUnknown → degrade
            }
        }

        /// <summary>gap-ability-allowlist-wave2: is this ability instance a damage-TRIGGERED hurt reaction?
        /// Reads <c>BaseDef.TriggerOnDamage</c> (public field on <c>TacticalHurtReactionAbilityDef</c>,
        /// TacticalHurtReactionAbilityDef.cs:13; <c>AccessTools.Field</c> walks base types, so the concrete
        /// <c>RepositionAbilityDef</c> resolves it). Fail-CLOSED toward "reaction" (unreadable → true = do NOT
        /// relay): wrongly skipping a Dash relay degrades to today's no-relay behavior, while wrongly relaying a
        /// reaction would double-Activate it on the host — the strictly worse failure.</summary>
        private static bool IsReactionTriggeredDef(object ability)
        {
            try
            {
                object def = GetProp(ability, "BaseDef");
                var f = def != null ? AccessTools.Field(def.GetType(), "TriggerOnDamage") : null;
                if (f == null) return true;   // not a hurt-reaction def shape → never relay a direct-only type blind
                return !(f.GetValue(def) is bool b) || b;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] IsReactionTriggeredDef read failed (treat as reaction): " + ex);
                return true;
            }
        }

        /// <summary>gap-turret-crate-loot: is the resolved ability actually usable on the host right now?
        /// Reflection <c>IsEnabled(filter:null)</c> (TacticalAbility.cs:367 — the same check the native UI runs
        /// before offering the ability). IsEnabled is declared on the TacticalAbility/Ability BASE — none of the
        /// gated leaf types (RequiresHostEnabledCheck set) declares or overrides it — so resolution MUST walk base
        /// types: <c>AccessTools.Method</c> does (FindIncludingBaseTypes); <c>AccessTools.FirstMethod</c> searches
        /// DECLARED-ONLY methods and returns null on every gated leaf (a silently dead, fail-open gate — the
        /// review-caught bug). Exactly ONE 1-param IsEnabled overload exists in the hierarchy and its param
        /// (IAbilityDisabledStatesFilter) is a reference type, so Invoke(null) is safe. Resolution is pinned by
        /// HostAbilityEnabledResolutionTests (Bridge.Tests, runs against the shipped 0Harmony.dll). Fail-OPEN
        /// (unreadable → true) so a reflection surprise can never wedge a legitimate relayed action; the native
        /// Activate is then no worse than before this gate existed.</summary>
        private static bool HostAbilityEnabled(object ability)
        {
            try
            {
                var m = AccessTools.Method(ability.GetType(), "IsEnabled");
                if (m == null || m.GetParameters().Length != 1) return true;   // fail-open
                return !(m.Invoke(ability, new object[] { null }) is bool b) || b;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] HostAbilityEnabled read failed (fail-open): " + ex);
                return true;
            }
        }

        /// <summary>Build an ACTOR-target <c>TacticalAbilityTarget</c> (Actor + DamageReceiver + aim-point
        /// PositionToApply). Typed 3-arg ctor first (like <see cref="BuildShootTarget"/>), default-ctor + field-set
        /// fallback. Used for Heal (and future actor-target generic abilities).</summary>
        private static object BuildActorAbilityTarget(object targetActor)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            Vector3 aim = GetActorAimPosition(targetActor, Vector3.zero);
            try
            {
                var actorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                var attackType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
                var ctor = (actorBaseType != null && attackType != null)
                    ? targetType.GetConstructor(new[] { actorBaseType, typeof(Vector3), attackType })
                    : null;
                if (ctor != null)
                {
                    object t = ctor.Invoke(new[] { targetActor, aim, DefaultAttackType(attackType) });
                    AccessTools.Field(targetType, "DamageReceiver")?.SetValue(t, targetActor);
                    return t;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildActorAbilityTarget ctor failed, falling back: " + ex); }

            object target = Activator.CreateInstance(targetType);
            AccessTools.Field(targetType, "Actor")?.SetValue(target, targetActor);
            AccessTools.Field(targetType, "DamageReceiver")?.SetValue(target, targetActor);
            object go = GetProp(targetActor, "gameObject");
            if (go != null) AccessTools.Field(targetType, "GameObject")?.SetValue(target, go);
            if (GetProp(targetActor, "Pos") is Vector3 ap) AccessTools.Field(targetType, "ActorGridPosition")?.SetValue(target, ap);
            AccessTools.Field(targetType, "PositionToApply")?.SetValue(target, aim);
            return target;
        }

        /// <summary>Build a bare-POSITION <c>TacticalAbilityTarget</c> (Vector3 ctor first, field-set fallback).
        /// Used for pos-target generic abilities (deploy-turret etc. — deferred allowlist, wire+build ready).</summary>
        private static object BuildPositionAbilityTarget(Vector3 pos)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            try
            {
                var posCtor = targetType.GetConstructor(new[] { typeof(Vector3) });
                if (posCtor != null) return posCtor.Invoke(new object[] { pos });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildPositionAbilityTarget ctor failed, falling back: " + ex); }
            object target = Activator.CreateInstance(targetType);
            AccessTools.Field(targetType, "PositionToApply")?.SetValue(target, pos);
            return target;
        }

        // ─── TS5b: reload (equipment-slot) + interact (ground-object) target helpers ─────────────────
        /// <summary>CLIENT: the equipment-slot index of a slot-target's OWN item — the position of
        /// <c>parameter.Equipment</c> (reload) ?? <c>parameter.TacticalItem</c> (drop, gap-turret-crate-loot) in
        /// the caster's <c>Equipments.Equipments</c> list, so the host acts on the EXACT item (a soldier may carry
        /// several). Returns <see cref="TacticalGenericIntentCodec.SlotIndexNone"/> (-1) when the parameter carries
        /// no specific item (auto-reload / drop-selected), or the caster / slot can't be read — the host then
        /// self-derives the selected weapon/equipment (ReloadCrt.ChooseEquipmentAndAmmo / DropItemAbility:19-35).</summary>
        private static int ReadEquipmentSlotIndex(int casterNetId, object parameter)
        {
            try
            {
                object equipment = GetField(parameter, "Equipment") ?? GetField(parameter, "TacticalItem");
                if (equipment == null) return TacticalGenericIntentCodec.SlotIndexNone;
                object caster = TacticalDeploySync.ResolveLiveActor(casterNetId);
                if (caster == null) return TacticalGenericIntentCodec.SlotIndexNone;
                if (GetProp(GetProp(caster, "Equipments"), "Equipments") is IEnumerable equipments)
                {
                    int i = 0;
                    foreach (var e in equipments)
                    {
                        if (ReferenceEquals(e, equipment)) return i;
                        i++;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReadEquipmentSlotIndex failed: " + ex); }
            return TacticalGenericIntentCodec.SlotIndexNone;
        }

        /// <summary>HOST: build the reload-self <c>TacticalAbilityTarget</c> for the caster's weapon at
        /// <paramref name="slotIndex"/> (its position in <c>Equipments.Equipments</c>). slot &gt;= 0 sets
        /// <c>Equipment</c> so ReloadCrt reloads THAT weapon; slot &lt; 0 / out-of-range returns a BARE (empty)
        /// target so ReloadCrt.ChooseEquipmentAndAmmo self-derives the selected weapon. NEVER null for a resolvable
        /// caster (ReloadCrt dereferences action.Param.Equipment for a self-reload — a null param would NRE);
        /// null ONLY when the caster itself is null → caller degrades-to-notify.</summary>
        private static object BuildReloadSlotTarget(object caster, int slotIndex)
        {
            if (caster == null) return null;
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            object target = Activator.CreateInstance(targetType);
            if (slotIndex >= 0)
            {
                object e = EquipmentAtSlot(caster, slotIndex);
                if (e != null) AccessTools.Field(targetType, "Equipment")?.SetValue(target, e);
            }
            return target;   // bare target for slot < 0 (or unresolved slot) → ReloadCrt reloads the selected weapon
        }

        /// <summary>The caster's equipped item at <paramref name="slotIndex"/> (its position in
        /// <c>Equipments.Equipments</c> — the same enumeration the client indexed against in
        /// <see cref="ReadEquipmentSlotIndex"/>). Null when out of range / unreadable.</summary>
        private static object EquipmentAtSlot(object caster, int slotIndex)
        {
            try
            {
                if (GetProp(GetProp(caster, "Equipments"), "Equipments") is IEnumerable equipments)
                {
                    int i = 0;
                    foreach (var e in equipments)
                    {
                        if (i == slotIndex) return e;
                        i++;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] EquipmentAtSlot read failed: " + ex); }
            return null;
        }

        /// <summary>gap-turret-crate-loot: the DROP target — a <c>TacticalAbilityTarget</c> whose
        /// <c>TacticalItem</c> field carries the exact item to drop (DropItemAbility.Activate reads
        /// <c>tacticalAbilityTarget.TacticalItem.Drop(...)</c>, DropItemAbility.cs:36).</summary>
        private static object BuildDropItemTarget(object item)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            object target = Activator.CreateInstance(targetType);
            var f = AccessTools.Field(targetType, "TacticalItem");
            if (f == null) return null;   // wrong item shape would NRE inside Activate — degrade instead
            try { f.SetValue(target, item); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildDropItemTarget set failed: " + ex); return null; }
            return target;
        }

        /// <summary>gap-turret-crate-loot: the <c>CrateComponent</c> living on a registry-resolved ground
        /// container's GameObject (crate visual/animator — CrateComponent.cs:8). Null when the object is not a
        /// crate (the caller degrades — an OpenCrate intent for a non-crate is a stale/garbled target).</summary>
        private static object ResolveCrateComponent(object containerActor)
        {
            try
            {
                var crateType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.CrateComponent");
                var comp = containerActor as Component;
                if (crateType == null || comp == null) return null;
                return comp.GetComponent(crateType);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveCrateComponent failed: " + ex); return null; }
        }

        /// <summary>gap-turret-crate-loot HOST: after a relayed DROP lands <paramref name="dropItem"/> in a ground
        /// container, re-broadcast that container's blob (despawn+respawn at the SAME netId — both existing TS1
        /// surfaces) so every client mirror shows the item: the container's 0x92 spawn blob was serialized EMPTY
        /// (EnterPlay fires BEFORE AddItem, TacticalItem.Drop:523→532), and a REUSED container never re-emits at
        /// all. Skips (log only) when the item's container can't be read or isn't refresh-safe — the drop itself
        /// is already host-authoritative, only the mirror's container contents stay stale.</summary>
        private static void HostRefreshDroppedItemContainer(object dropItem)
        {
            try
            {
                object container = GetProp(GetProp(dropItem, "InventoryComponent"), "Actor");
                if (container == null)
                {
                    Debug.LogWarning("[Multiplayer][tac] drop refresh: item has no container actor — mirror contents stale");
                    return;
                }
                TacticalActorLifecycleSync.HostRefreshActorMirror(container);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostRefreshDroppedItemContainer failed: " + ex); }
        }

        /// <summary>CLIENT: resolve the shared registry netId of the ground OBJECT a generic object-target ability
        /// (interact / crate) points at. The object is a <c>TacticalActorBase</c> (a StructuralTarget for a
        /// console/crate, an ItemContainer for ground loot), so it rides the SAME actor registry as any mirror
        /// actor. Reads the two native <c>TacticalAbilityTarget</c> shapes: <c>Actor</c> (interact StructuralTarget)
        /// then <c>ItemContainer</c> (crate/loot container actor). Returns -1 when nothing resolves (the object is
        /// not registered on this side) → the caller degrades-to-notify (a client must not guess an unshared id).</summary>
        private static int ReadObjectNetId(object parameter)
        {
            if (parameter == null) return -1;
            try
            {
                object actor = GetField(parameter, "Actor");
                if (actor != null)
                {
                    int net = TacticalDeploySync.NetIdForLiveActor(actor);
                    if (net >= 0) return net;
                }
                object container = GetField(parameter, "ItemContainer");
                if (container != null)
                {
                    int net = TacticalDeploySync.NetIdForLiveActor(container);
                    if (net >= 0) return net;
                }
                // gap-turret-crate-loot: the crate AUTO-OPEN activates with the raw CrateComponent (not a
                // TacticalAbilityTarget — OpenCrateAbility.OnActorAbilityExecuted:99). Resolve the ItemContainer
                // actor living on the same GameObject and use ITS registry netId.
                if (parameter is Component comp)
                {
                    var containerType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.ItemContainer");
                    object sibling = containerType != null ? comp.GetComponent(containerType) : null;
                    if (sibling != null)
                    {
                        int net = TacticalDeploySync.NetIdForLiveActor(sibling);
                        if (net >= 0) return net;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReadObjectNetId failed: " + ex); }
            return -1;
        }

        /// <summary>CLIENT (gap-turret-crate-loot): read a pos-kind target's BOTH native shapes off the
        /// <c>TacticalAbilityTarget</c> — <c>PositionToApply</c> (NaN = unset, TacticalAbilityTarget.cs:20/52)
        /// and the shield facing <c>Direction</c> (zero = unset, :26/54). Feeds
        /// <see cref="TacticalAbilityRelay.ChoosePosEncoding"/>.</summary>
        private static void ReadPosAndDirection(object parameter,
            out bool hasPos, out Vector3 pos, out bool hasDir, out Vector3 dir)
        {
            hasPos = false; pos = Vector3.zero; hasDir = false; dir = Vector3.zero;
            if (parameter == null) return;
            try
            {
                if (GetField(parameter, "PositionToApply") is Vector3 p &&
                    !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)))
                { hasPos = true; pos = p; }
                if (GetField(parameter, "Direction") is Vector3 d &&
                    !(float.IsNaN(d.x) || float.IsNaN(d.y) || float.IsNaN(d.z)) && d.sqrMagnitude > 1e-6f)
                { hasDir = true; dir = d; }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReadPosAndDirection failed: " + ex); }
        }

        /// <summary>Degrade-to-notify for the generic relay (spec §1 "degrade-to-notify, never silent desync,
        /// never crash"): a clear, taggable WARNING a UI layer can surface. Used when a generic ability's target
        /// kind is unsupported/unreadable (client suppresses without sending; host skips the Activate). The client
        /// already suppressed the local run, so this NEVER leaves the frozen sim executing the ability.</summary>
        private static void NotifyGenericDegrade(string abilityTypeName, string reason)
        {
            Debug.LogWarning("[Multiplayer][tac] generic ability relay DEGRADED (notify): type=" +
                             (abilityTypeName ?? "?") + " — " + reason +
                             " — suppressed locally, not executed (no desync).");
        }

        // ─── HOST: ApplyDamage funneled → broadcast the FINAL applied DamageResult ─────────────────
        /// <summary>HOST postfix on <c>TacticalActorBase.ApplyDamage(DamageResult)</c>: flatten the FINAL
        /// applied result + broadcast <c>tac.damage</c> to all peers. Gated: only on the host, only in a live
        /// session, and NOT while the host is itself applying a relayed result (<see cref="_applyingRemote"/>).
        /// Postfix so it captures the result AFTER the engine finalized it.</summary>
        public static void OnHostApplyDamage(object target, object damageResultBoxed, int capturedNetId = -1)
        {
            if (_applyingRemote) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;   // defensive: host is never mirroring
            if (target == null || damageResultBoxed == null) return;

            try
            {
                // Prefer the netId captured by the ApplyDamage PREFIX (before a lethal hit's synchronous
                // Die→ActorDied→Registry.Remove wiped the minted-id lookup); fall back to a live lookup for
                // any path that didn't route through the prefix.
                int targetNetId = capturedNetId >= 0 ? capturedNetId : TacticalDeploySync.NetIdForLiveActor(target);
                if (targetNetId < 0) return;   // an unregistered actor (e.g. transient destructible) — skip

                // MISS FEEDBACK: any host damage to an in-flight relayed attack's intent target = a HIT — this
                // funnel carries EVERY damage form (HP, paralysis, viral, acid, daze — a Neurazer hit is a 0-HP
                // DamageResult through here) — flag it so EndRelayedShot skips the tac.shot.result(miss).
                RelayedShots.MarkEffect(targetNetId);

                var p = FlattenDamage(damageResultBoxed);
                if (p == null) return;
                p.TargetNetId = targetNetId;

                // Resolve the damage source actor (the engine's own helper) → netId. Sentinel -1 if null.
                object sourceActor = ResolveSourceActor(GetField(damageResultBoxed, "Source"));
                p.SourceNetId = sourceActor != null ? TacticalDeploySync.NetIdForLiveActor(sourceActor) : TacticalLiveCodec.TargetNetIdNone;

                // If the source is a shooter actor WITH readable CharacterStats (only TacticalActor has them —
                // NOT a turret/vehicle/destructible/environmental source), carry its post-shot AP/WP so the
                // client mirror sets them exactly (the client never ran the cost path). A stat-less source
                // reads 0/0, which is INDISTINGUISHABLE from "real 0 AP" — broadcasting that would let the
                // client ZERO the actor's real AP/WP. So gate the carry on a SUCCESSFUL stat read: if the
                // source has no CharacterStats, send ShooterNetId = -1 and the client skips the AP/WP set.
                if (sourceActor != null && ReadApWp(sourceActor, out float ap, out float wp))
                {
                    p.ShooterNetId = p.SourceNetId;
                    p.ShooterApAfter = ap;
                    p.ShooterWpAfter = wp;
                }
                else p.ShooterNetId = TacticalLiveCodec.TargetNetIdNone;

                p.Seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacDamage);
                byte[] payload = TacticalLiveCodec.EncodeDamage(p);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacDamage, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.damage seq=" + p.Seq + " targetNetId=" + targetNetId +
                          " sourceNetId=" + p.SourceNetId + " HealthDamage=" + p.HealthDamage.ToString("0.0") +
                          " statuses=" + p.Statuses.Count + " effects=" + p.EffectGuids.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] OnHostApplyDamage failed: " + ex); }
        }

        // ─── CLIENT: apply the host damage outcome ─────────────────────────────────────────────────
        /// <summary>CLIENT inbound (<c>tac.damage</c>): rebuild the <c>DamageResult</c> (defs by guid, live
        /// refs via registry) and apply it to the target mirror. Guarded by the per-surface seq + the
        /// re-entrancy flag (so the client's own ApplyDamage postfix doesn't re-broadcast). Then set the
        /// shooter's AP/WP to the host-carried values. No-op off-client / off-session.</summary>
        public static void HandleDamage(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeDamage(payload, out var p)) { Debug.LogError("[Multiplayer][tac] tac.damage decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacDamage, p.Seq)) return;

            object target = TacticalDeploySync.ResolveLiveActor(p.TargetNetId);
            if (target == null) { Debug.LogError("[Multiplayer][tac] tac.damage: no actor for targetNetId " + p.TargetNetId); return; }

            try
            {
                object source = p.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(p.SourceNetId) : null;
                object damageResult = RebuildDamage(p, source);
                if (damageResult == null) { Debug.LogError("[Multiplayer][tac] tac.damage: could not rebuild DamageResult"); return; }

                var apply = AccessTools.Method(target.GetType(), "ApplyDamage", new[] { _damageResultType });
                if (apply == null) { Debug.LogError("[Multiplayer][tac] tac.damage: ApplyDamage(DamageResult) not found"); return; }

                _applyingRemote = true;
                try { apply.Invoke(target, new[] { damageResult }); }
                finally { _applyingRemote = false; }

                // Set the shooter's spent AP/WP to the host's post-shot values (the client never ran costs).
                if (p.ShooterNetId >= 0)
                {
                    object shooter = TacticalDeploySync.ResolveLiveActor(p.ShooterNetId);
                    if (shooter != null) SetApWp(shooter, p.ShooterApAfter, p.ShooterWpAfter);
                    // FIX B: the relayed-shot AP just landed on the client; if the client's UI has this shooter
                    // selected, re-grey its ability bar NOW so spent-AP buttons don't stay lit until the next 0x8F
                    // flush. Client-only (HandleDamage already returned early on the host). No-op if not selected.
                    TacticalActorStateSync.RefreshClientBarForActor(p.ShooterNetId);
                    // AIMED-SHOT EXIT moved to TacticalFireAnimSync.ReplayFireCrt's finally: the exit must fire at
                    // BURST completion, not on the first tac.damage (a burst spends AP once, so AP reads terminal from
                    // the first hit — exiting here snapped the camera out mid-burst while the tail was still replaying).
                }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacDamage, p.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.damage seq=" + p.Seq + " targetNetId=" + p.TargetNetId +
                          " HealthDamage=" + p.HealthDamage.ToString("0.0"));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleDamage failed: " + ex); }
        }

        // ─── Flatten / rebuild DamageResult ────────────────────────────────────────────────────────

        // DamageResult is a struct (PhoenixPoint.Tactical.Entities.DamageResult). Cached for reflection.
        private static Type _damageResultType;
        private static Type DamageResultType => _damageResultType ?? (_damageResultType =
            AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.DamageResult"));

        /// <summary>Read a boxed <c>DamageResult</c>'s numeric + def fields into a wire payload (no live refs
        /// except defs-by-guid + statuses/statmods, all resolved to guids). TargetNetId/SourceNetId/AP/WP are
        /// filled by the caller.</summary>
        private static TacticalLiveCodec.DamagePayload FlattenDamage(object dr)
        {
            try
            {
                var p = new TacticalLiveCodec.DamagePayload
                {
                    HealthDamage = (float)GetField(dr, "HealthDamage"),
                    ArmorDamage = (float)GetField(dr, "ArmorDamage"),
                    ArmorMitigatedDamage = (float)GetField(dr, "ArmorMitigatedDamage"),
                    StunValue = (float)GetField(dr, "StunValue"),
                    HealValue = (float)GetField(dr, "HealValue"),
                    ForceHurt = (bool)(GetField(dr, "forceHurt") ?? false),
                    DamageTypeDefGuid = DefReflection.GetGuid(GetField(dr, "DamageTypeDef")) ?? "",
                };
                Vector3 impactForce = ToVec3(GetField(dr, "ImpactForce"));
                Vector3 damageOrigin = ToVec3(GetField(dr, "DamageOrigin"));
                p.IfX = impactForce.x; p.IfY = impactForce.y; p.IfZ = impactForce.z;
                p.DoX = damageOrigin.x; p.DoY = damageOrigin.y; p.DoZ = damageOrigin.z;

                // ApplyStatuses: List<StatusApplication> { StatusDef, object StatusSource, object StatusTarget, float Value }.
                if (GetField(dr, "ApplyStatuses") is IEnumerable statuses)
                    foreach (var s in statuses)
                    {
                        if (s == null) continue;
                        string guid = DefReflection.GetGuid(GetField(s, "StatusDef"));
                        if (string.IsNullOrEmpty(guid)) continue;
                        object srcActor = ResolveSourceActor(GetField(s, "StatusSource"));
                        int srcNet = srcActor != null ? TacticalDeploySync.NetIdForLiveActor(srcActor) : TacticalLiveCodec.TargetNetIdNone;
                        p.Statuses.Add(new TacticalLiveCodec.DamageStatus
                        { DefGuid = guid, Value = (float)(GetField(s, "Value") ?? 0f), SourceNetId = srcNet });
                    }

                // ActorEffects: List<EffectDef>.
                if (GetField(dr, "ActorEffects") is IEnumerable effects)
                    foreach (var e in effects)
                    {
                        string guid = DefReflection.GetGuid(e);
                        if (!string.IsNullOrEmpty(guid)) p.EffectGuids.Add(guid);
                    }

                // StatModifications: List<StatModification> { StatModificationType Modification, string StatName, float Value, ... }.
                if (GetField(dr, "StatModifications") is IEnumerable mods)
                    foreach (var m in mods)
                    {
                        if (m == null) continue;
                        p.StatMods.Add(new TacticalLiveCodec.DamageStatMod
                        {
                            StatName = GetField(m, "StatName") as string ?? "",
                            ModKind = Convert.ToInt32(GetField(m, "Modification") ?? 0),
                            Value = (float)(GetField(m, "Value") ?? 0f),
                        });
                    }

                return p;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] FlattenDamage failed: " + ex); return null; }
        }

        /// <summary>Rebuild a boxed <c>DamageResult</c> struct from a wire payload: numeric fields + defs
        /// resolved by guid (null-skip) + source set to the resolved live actor (or null). <c>ImpactHit</c>
        /// is left default (local-FX only). Statuses/effects/statMods are rebuilt and the native ApplyDamage
        /// applies them.</summary>
        private static object RebuildDamage(TacticalLiveCodec.DamagePayload p, object sourceActor)
        {
            var t = DamageResultType;
            if (t == null) { Debug.LogError("[Multiplayer][tac] RebuildDamage: DamageResult type not found"); return null; }
            try
            {
                object dr = Activator.CreateInstance(t);   // struct default
                SetField(t, ref dr, "HealthDamage", p.HealthDamage);
                SetField(t, ref dr, "ArmorDamage", p.ArmorDamage);
                SetField(t, ref dr, "ArmorMitigatedDamage", p.ArmorMitigatedDamage);
                SetField(t, ref dr, "StunValue", p.StunValue);
                SetField(t, ref dr, "HealValue", p.HealValue);
                SetField(t, ref dr, "forceHurt", p.ForceHurt);
                SetField(t, ref dr, "ImpactForce", new Vector3(p.IfX, p.IfY, p.IfZ));
                SetField(t, ref dr, "DamageOrigin", new Vector3(p.DoX, p.DoY, p.DoZ));
                SetField(t, ref dr, "Source", sourceActor);

                object dmgTypeDef = DefReflection.GetDefByGuid(p.DamageTypeDefGuid);
                if (dmgTypeDef != null) SetField(t, ref dr, "DamageTypeDef", dmgTypeDef);

                // CANON inv 2 + 4 (bug C): tac.damage NO LONGER live-applies statuses on the client. Statuses are
                // host-authoritative DISPLAY state and ride the generic 0x8F spine as INERT mirrors only
                // (TacticalActorStateSync.ReconcileStatuses → InvokeApplyStatus, Applied=true + magnitude seed).
                // Live-applying them here broke one-writer (0x88 + 0x8F both wrote statuses) AND crashed: the wire
                // DamageStatus struct carries no StatusTarget (TacticalLiveCodec.cs:285) and FlattenDamage drops it
                // (TacticalCombatSync.cs:394-405), so the rebuilt StatusApplication.Target was null → native
                // ApplyDamage ran BleedStatus.OnApply LIVE (Applied=false) → GetTargetSlotName(null) →
                // BodyState.GetSlot(null)/GetSlotBleedValue NRE (BleedStatus.cs:133-135,226-229) AFTER it
                // subscribed AddonsManager.AddonDetaching (:124) → §5 half-mutation/addon-tree corruption that also
                // aborted the enemy's client ApplyDamage (no body-part doll). tac.damage keeps owning
                // HP/armor/stun/death only. (FlattenDamage's status encode + the DamageStatus wire field are now
                // vestigial; retiring them is deferred to the inc-4 one-writer convergence.)

                // ActorEffects: rebuild List<EffectDef>.
                if (p.EffectGuids != null && p.EffectGuids.Count > 0)
                {
                    var effectDefType = AccessTools.TypeByName("Base.Entities.Effects.EffectDef");
                    if (effectDefType != null)
                    {
                        var listType = typeof(List<>).MakeGenericType(effectDefType);
                        var list = (IList)Activator.CreateInstance(listType);
                        foreach (var g in p.EffectGuids)
                        {
                            object def = DefReflection.GetDefByGuid(g);
                            if (def != null) list.Add(def);
                        }
                        if (list.Count > 0) SetField(t, ref dr, "ActorEffects", list);
                    }
                }

                // StatModifications: rebuild List<StatModification> via its (modType, statName, value, source,
                // applicationValue) ctor.
                if (p.StatMods != null && p.StatMods.Count > 0)
                {
                    var statModType = AccessTools.TypeByName("Base.Entities.Statuses.StatModification");
                    var modKindType = AccessTools.TypeByName("Base.Entities.Statuses.StatModificationType");
                    if (statModType != null && modKindType != null)
                    {
                        var listType = typeof(List<>).MakeGenericType(statModType);
                        var list = (IList)Activator.CreateInstance(listType);
                        var ctor = statModType.GetConstructor(new[] { modKindType, typeof(string), typeof(float), typeof(object), typeof(float) });
                        foreach (var m in p.StatMods)
                        {
                            object kind = Enum.ToObject(modKindType, m.ModKind);
                            object sm = ctor != null
                                ? ctor.Invoke(new object[] { kind, m.StatName, m.Value, null, 0f })
                                : Activator.CreateInstance(statModType);
                            list.Add(sm);
                        }
                        if (list.Count > 0) SetField(t, ref dr, "StatModifications", list);
                    }
                }

                return dr;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] RebuildDamage failed: " + ex); return null; }
        }

        // ─── Engine reflection helpers ──────────────────────────────────────────────────────────────

        /// <summary>Resolve the actor that an ability/weapon/status/etc. "source" object belongs to via the
        /// engine's own <c>TacUtil.GetSourceTacticalActorBase(object)</c> (covers Weapon→actor,
        /// TacticalAbility→actor, TacStatus→actor, GameObject→actor, raw actor). Null if unresolvable.</summary>
        private static MethodInfo _getSourceActor;
        private static object ResolveSourceActor(object source)
        {
            if (source == null) return null;
            try
            {
                if (_getSourceActor == null)
                {
                    var tacUtil = AccessTools.TypeByName("PhoenixPoint.Tactical.TacUtil");
                    _getSourceActor = tacUtil != null ? AccessTools.Method(tacUtil, "GetSourceTacticalActorBase", new[] { typeof(object) }) : null;
                }
                if (_getSourceActor != null) return _getSourceActor.Invoke(null, new[] { source });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveSourceActor failed: " + ex); }
            return null;
        }

        /// <summary>Resolve a <c>TacticalAbility</c> on an actor whose Def.Guid matches. Enumerates
        /// <c>GetAbilities&lt;TacticalAbility&gt;()</c> and matches by guid (covers ShootAbility + variants).</summary>
        private static object ResolveAbilityByGuid(object actor, string abilityGuid)
        {
            try
            {
                var tacAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
                if (tacAbilityType == null) return null;
                var getAbilities = AccessTools.Method(actor.GetType(), "GetAbilities");
                if (getAbilities == null || !getAbilities.IsGenericMethodDefinition) return null;
                var gen = getAbilities.MakeGenericMethod(tacAbilityType);
                if (gen.GetParameters().Length != 0) return null;   // GetAbilities<T>() — the no-arg overload
                var result = gen.Invoke(actor, null) as IEnumerable;
                if (result == null) return null;
                foreach (var a in result)
                {
                    if (a == null) continue;
                    // FIX: match on Ability.BaseDef.Guid (the real def), not the non-existent "Def" member.
                    string g = DefReflection.GetGuid(GetProp(a, "BaseDef"));
                    if (g == abilityGuid) return a;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveAbilityByGuid failed: " + ex); }
            return null;
        }

        /// <summary>Build a <c>TacticalAbilityTarget</c> for the host shot from the intent. Mirrors the proven
        /// <c>TacticalMoveSync.BuildMoveTarget</c> pattern (TYPED-ctor first, default-ctor + field-set FALLBACK).
        ///
        /// AIM (combat-bug fix, CORRECTED): the wire only carries the actor's GROUND pos (height ~0). The RETIRED
        /// contract left an actor target's <c>PositionToApply</c> NaN on the false premise the host re-snaps the body-
        /// part on re-Activate — it does NOT (the snapper is only on the UI GetTargets path; <c>ShootAbility.Activate</c>
        /// casts the raw target as-is). A NaN actor target then falls through <c>GetWorkingPosition</c> to the actor
        /// ROOT/FEET (Y=0) → the shot hits the ground → 0 damage. So an ACTOR target now SEEDS <c>PositionToApply</c> with
        /// the actor's AIM POINT (center-of-mass, <c>GetAimPoint().position</c>, always NON-NaN) via the 3-arg ctor
        /// <c>(TacticalActorBase actor, Vector3 positionToApply, AttackType=Regular)</c>; a bare-GROUND shot (no actor)
        /// uses the <c>(Vector3 pos)</c> ctor. (HostOnAbilityIntent then best-effort snaps the actor target to the nearest
        /// body part via <c>GetShootTarget</c>, which the aim-point seed makes possible.) The pure
        /// <see cref="ShootTargetAimPolicy"/> pins this decision (tested).</summary>
        private static object BuildShootTarget(TacticalLiveCodec.IntentAbility intent)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            var groundPos = new Vector3(intent.TX, intent.TY, intent.TZ);
            object targetActor = intent.TargetNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(intent.TargetNetId) : null;
            var aimSource = ShootTargetAimPolicy.Decide(targetActor != null);

            // FIX A (faithful shoot-aim relay): when the client carried a SELECTED body part, resolve the SAME item
            // on the host (same index into the same GetHealthSlots().GetAimPointItem()+visible-Equipments.Items
            // enumeration native ShootAbility.GetShootTarget walks) and aim at ITS aim point — so the native snap
            // (HostOnAbilityIntent.TrySnapShootTarget → GetShootTarget → Weapon.TryGetShootTarget) picks the player's
            // actual limb and GetBestShootPositionFor yields the cover-aware ShootFromPos for it, instead of the
            // center-of-mass-nearest part. FAIL-OPEN to the center-of-mass aim path below when the index can't be
            // resolved on the host (limb destroyed between aim and apply / enum drift) — log a one-line warning.
            object selectedItem = null;
            if (intent.BodyPartId >= 0 && targetActor != null)
            {
                var candidates = BuildBodyPartCandidates(targetActor);
                if (intent.BodyPartId < candidates.Count) selectedItem = candidates[intent.BodyPartId];
                if (selectedItem == null)
                    Debug.LogWarning("[Multiplayer][tac] BuildShootTarget: bodyPartId=" + intent.BodyPartId +
                                     " unresolved on host (candidates=" + candidates.Count +
                                     ") — falling open to center-of-mass aim");
            }

            // For an actor target, seed with the SELECTED body part's aim point when resolved (so the snap picks the
            // exact limb), else the body-center aim point (non-NaN); ground fallback never used for actors.
            Vector3 aimPos = selectedItem != null ? GetItemAimPosition(selectedItem, targetActor, groundPos)
                : (targetActor != null ? GetActorAimPosition(targetActor, groundPos) : groundPos);

            try
            {
                if (aimSource == ShootTargetAimPolicy.AimSource.ActorAimPoint && targetActor != null)
                {
                    // ACTOR target → new TacticalAbilityTarget(TacticalActorBase actor, Vector3 positionToApply,
                    // AttackType=Regular). PositionToApply = the actor AIM POINT (center-of-mass, NON-NaN) so the working
                    // position is the BODY (not the feet) AND the host's GetShootTarget snap has a valid seed.
                    var actorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                    var attackType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
                    var ctor = (actorBaseType != null && attackType != null)
                        ? targetType.GetConstructor(new[] { actorBaseType, typeof(Vector3), attackType })
                        : null;
                    if (ctor != null)
                    {
                        object t = ctor.Invoke(new[] { targetActor, aimPos, DefaultAttackType(attackType) });
                        // FIX A (relayed-melee ZERO-damage): the (actor, pos, AttackType) ctor seeds Actor/
                        // GameObject/PositionToApply but NOT DamageReceiver. Native bash derefs target.DamageReceiver
                        // UNGUARDED (BashAbility.GetEffectTarget BashAbility.cs:538/547/565 + AimIK :449) → NRE AFTER
                        // the swing → the swing plays but the damage dies. Shoot is unaffected: its native UI target
                        // sets DamageReceiver too, and HostOnAbilityIntent's GetShootTarget snap rebuilds the target
                        // downstream. Mirror TacticalMeleeAnimSync.BuildMeleeTarget:258 — set it to the live actor
                        // (TacticalActorBase : IDamageReceiver).
                        AccessTools.Field(targetType, "DamageReceiver")?.SetValue(t, targetActor);
                        // FIX A: pin the player's selected body part. A SnapToBodyparts-ON weapon re-derives the
                        // SAME item from the aim-point seed in the snap below; pinning it ALSO covers a
                        // SnapToBodyparts-OFF weapon (whose snap leaves TacticalItem untouched).
                        if (selectedItem != null) AccessTools.Field(targetType, "TacticalItem")?.SetValue(t, selectedItem);
                        Debug.Log("[Multiplayer][tac] BuildShootTarget set DamageReceiver=actor" +
                                  (selectedItem != null ? " + TacticalItem=selectedBodyPart" : "") +
                                  " on actor target (relayed-melee NRE fix)");
                        return t;
                    }
                }
                else
                {
                    // new TacticalAbilityTarget(Vector3 pos) — bare-ground shot (no actor to snap to).
                    var posCtor = targetType.GetConstructor(new[] { typeof(Vector3) });
                    if (posCtor != null) return posCtor.Invoke(new object[] { groundPos });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildShootTarget ctor failed, falling back to field-set: " + ex); }

            // Fallback: default-ctor + field assignment (no typed ctor available / ctor threw). For an ACTOR target seed
            // Actor + GameObject + ActorGridPosition + PositionToApply(aim point) so GetWorkingPosition lands on the body
            // (never the NaN→feet fallthrough); a bare-ground target gets the explicit Vector3 ground pos.
            object target = Activator.CreateInstance(targetType);
            if (targetActor != null)
            {
                AccessTools.Field(targetType, "Actor")?.SetValue(target, targetActor);
                AccessTools.Field(targetType, "DamageReceiver")?.SetValue(target, targetActor);   // FIX A: see ctor branch
                object go = GetProp(targetActor, "gameObject");
                if (go != null) AccessTools.Field(targetType, "GameObject")?.SetValue(target, go);
                if (GetProp(targetActor, "Pos") is Vector3 actorPos)
                    AccessTools.Field(targetType, "ActorGridPosition")?.SetValue(target, actorPos);
                AccessTools.Field(targetType, "PositionToApply")?.SetValue(target, aimPos);
                if (selectedItem != null) AccessTools.Field(targetType, "TacticalItem")?.SetValue(target, selectedItem);   // FIX A: see ctor branch
            }
            else AccessTools.Field(targetType, "PositionToApply")?.SetValue(target, groundPos);
            return target;
        }

        /// <summary>Resolve the target actor's AIM POINT world position (<c>TacticalActorBase.GetAimPoint().position</c> —
        /// the default aim-slot / center-of-mass; TacticalActorBase.cs:754, TacticalActor.cs:1475). This NON-NaN point
        /// seeds the rebuilt shoot target's <c>PositionToApply</c>. Falls back to <paramref name="groundFallback"/> only
        /// if GetAimPoint is missing/throws/returns null (an actor normally always has one).</summary>
        private static Vector3 GetActorAimPosition(object targetActor, Vector3 groundFallback)
        {
            try
            {
                var getAim = AccessTools.Method(targetActor.GetType(), "GetAimPoint");
                if (getAim != null && getAim.Invoke(targetActor, null) is Transform tr && tr != null)
                    return tr.position;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] GetActorAimPosition failed: " + ex); }
            return groundFallback;
        }

        // Cached TacticalItem type (for the Equipments.Items OfType<TacticalItem> filter in the body-part walk).
        private static Type _tacticalItemType;
        private static Type TacticalItemType => _tacticalItemType ?? (_tacticalItemType =
            AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.TacticalItem"));

        /// <summary>FIX A (CLIENT capture): compute the index of the player-SELECTED body part — the
        /// <c>parameter.DamageReceiver</c> (a <c>TacticalItem</c> for a SnapToBodyparts shot, == item.OwnerItem) —
        /// within the target actor's body-part enumeration, walking the SAME order the host snapper uses in native
        /// <c>ShootAbility.GetShootTarget</c> (ShootAbility.cs:205-219: <c>BodyState.GetHealthSlots().GetAimPointItem()</c>
        /// then visible <c>Equipments.Items</c>). Returns <see cref="TacticalLiveCodec.BodyPartIdNone"/> (-1) when
        /// there is no actor target, no DamageReceiver, the receiver is not a TacticalItem (bare actor / structural /
        /// bare-ground), or it is not in the enumeration — the host then falls open to the center-of-mass aim path.</summary>
        private static int ComputeBodyPartId(object parameter, int targetNetId)
        {
            try
            {
                if (parameter == null || targetNetId < 0) return TacticalLiveCodec.BodyPartIdNone;
                object targetActor = GetField(parameter, "Actor");
                if (targetActor == null) return TacticalLiveCodec.BodyPartIdNone;
                object receiver = GetField(parameter, "DamageReceiver");
                if (receiver == null) return TacticalLiveCodec.BodyPartIdNone;
                var itemType = TacticalItemType;
                if (itemType == null || !itemType.IsInstanceOfType(receiver)) return TacticalLiveCodec.BodyPartIdNone; // bare actor / structural
                var candidates = BuildBodyPartCandidates(targetActor);
                for (int i = 0; i < candidates.Count; i++)
                    if (ReferenceEquals(candidates[i], receiver)) return i;
                return TacticalLiveCodec.BodyPartIdNone;   // not found → fall open (host uses center-of-mass)
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ComputeBodyPartId failed (sending -1 / center-of-mass): " + ex);
                return TacticalLiveCodec.BodyPartIdNone;
            }
        }

        /// <summary>Build the target actor's ordered body-part <c>TacticalItem</c> candidate list EXACTLY as native
        /// <c>ShootAbility.GetShootTarget</c> walks it (ShootAbility.cs:205-219): every health slot's
        /// <c>GetAimPointItem()</c> (non-null), THEN every visible <c>Equipments.Items</c> TacticalItem. Both sides
        /// build this from the SAME shared save (identical <c>GetHealthSlots()</c> insertion order + equipment list)
        /// → the index is a stable cross-side body-part identity. Used by the CLIENT to find the selected part's
        /// index and by the HOST to resolve the item at that index.</summary>
        private static List<object> BuildBodyPartCandidates(object actor)
        {
            var list = new List<object>();
            try
            {
                object bodyState = GetProp(actor, "BodyState");
                if (bodyState != null)
                {
                    var getHealthSlots = AccessTools.Method(bodyState.GetType(), "GetHealthSlots");
                    if (getHealthSlots?.Invoke(bodyState, null) is IEnumerable slots)
                        foreach (var slot in slots)
                        {
                            if (slot == null) continue;
                            var getAimItem = AccessTools.Method(slot.GetType(), "GetAimPointItem");
                            object item = getAimItem?.Invoke(slot, null);
                            if (item != null) list.Add(item);
                        }
                }
                object equipments = GetProp(actor, "Equipments");
                var itemType = TacticalItemType;
                if (equipments != null && itemType != null && GetProp(equipments, "Items") is IEnumerable items)
                    foreach (var it in items)
                    {
                        if (it == null || !itemType.IsInstanceOfType(it)) continue;
                        if (GetProp(it, "IsVisible") is bool vis && vis) list.Add(it);
                    }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildBodyPartCandidates failed: " + ex); }
            return list;
        }

        /// <summary>Resolve the SELECTED body-part item's world aim position (<c>TacticalItem.GetAimPoint().position</c>)
        /// — the exact point the player clicked — so the host snap pins this limb. Falls back to the actor
        /// center-of-mass aim, then the ground pos, if the item exposes no aim transform (so the shot still lands on
        /// the body rather than NaN→feet).</summary>
        private static Vector3 GetItemAimPosition(object item, object fallbackActor, Vector3 groundFallback)
        {
            try
            {
                var getAim = AccessTools.Method(item.GetType(), "GetAimPoint");
                if (getAim != null && getAim.Invoke(item, null) is Transform tr && tr != null)
                    return tr.position;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] GetItemAimPosition failed: " + ex); }
            return fallbackActor != null ? GetActorAimPosition(fallbackActor, groundFallback) : groundFallback;
        }

        /// <summary>The <c>AttackType.Regular</c> enum value (mirrors <c>TacticalMoveSync.DefaultAttackType</c>).</summary>
        private static object DefaultAttackType(Type attackType)
        {
            if (attackType == null) return null;
            try { return Enum.Parse(attackType, "Regular"); } catch { return Activator.CreateInstance(attackType); }
        }

        // Cached reflection for the GetShootTarget body-part snap (resolved lazily, once).
        private static MethodInfo _getShootTargetCached;
        private static Type _getShootTargetOwner;   // the ability type _getShootTargetCached was resolved for
        private static Type _tacAbilityTargetType;
        private static Type _tacTargetDataType;

        /// <summary>Best-effort body-part SNAP: run the ability's own <c>ShootAbility.GetShootTarget(target, null, null)</c>
        /// (ShootAbility.cs:194 — 3-param overload, the trailing two optional) so the relayed shot carries the nearest
        /// body part's <c>TacticalItem</c>, matching a host-local shot. Returns the snapped target, or null when there is
        /// nothing to snap (the caller then keeps the aim-point actor target):
        ///   • the ability is NOT a ShootAbility (e.g. BashAbility melee) → no GetShootTarget → null (no snap needed);
        ///   • GetShootTarget threw or returned null. NOTE: even on a null return the passed target may have been mutated
        ///     in place by SnapToBodyparts (TacticalItem set) — that's harmless, the aim-point/body target still hits.</summary>
        private static object TrySnapShootTarget(object ability, object target)
        {
            try
            {
                Type abilityType = ability.GetType();
                if (_getShootTargetCached == null || _getShootTargetOwner != abilityType)
                {
                    _tacAbilityTargetType = _tacAbilityTargetType ?? AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
                    _tacTargetDataType = _tacTargetDataType ?? AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalTargetData");
                    _getShootTargetOwner = abilityType;
                    _getShootTargetCached = (_tacAbilityTargetType != null && _tacTargetDataType != null)
                        // TacticalAbilityTarget GetShootTarget(TacticalAbilityTarget, Vector3?, TacticalTargetData) — EXACT match.
                        ? AccessTools.Method(abilityType, "GetShootTarget", new[] { _tacAbilityTargetType, typeof(Vector3?), _tacTargetDataType })
                        : null;
                }
                if (_getShootTargetCached == null) return null;   // not a ShootAbility → no snap (aim-point target stands)
                return _getShootTargetCached.Invoke(ability, new object[] { target, null, null });
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] GetShootTarget snap failed (using aim-point target): " + ex);
                return null;
            }
        }

        /// <summary>DIAG: log the relayed shot's aim — its snapped <c>TacticalItem</c> (body part) or "noItem", plus the
        /// resolved <c>GetWorkingPosition()</c> (TacticalAbilityTarget.cs:175). A non-zero Y / a real item name confirms
        /// the shot now aims at the BODY rather than the actor feet (the 0-damage bug).</summary>
        private static void LogShootAim(object target)
        {
            try
            {
                object item = GetField(target, "TacticalItem");
                string itemDesc = item == null ? "noItem" : item.ToString();
                var getWp = AccessTools.Method(target.GetType(), "GetWorkingPosition");
                Vector3 v = (getWp != null && getWp.Invoke(target, null) is Vector3 wp) ? wp : Vector3.zero;
                Debug.Log("[Multiplayer][tac] HOST shoot aim item=" + itemDesc +
                          " workingPos=(" + v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] LogShootAim failed: " + ex); }
        }

        /// <summary>Read {targetNetId, targetPos} from a <c>TacticalAbilityTarget</c>: the actor (if any) →
        /// netId, and PositionToApply (or the actor's Pos when PositionToApply is unset/NaN).</summary>
        private static void ReadTarget(object parameter, out int targetNetId, out Vector3 targetPos)
        {
            targetNetId = TacticalLiveCodec.TargetNetIdNone;
            targetPos = Vector3.zero;
            if (parameter == null) return;
            object actor = GetField(parameter, "Actor");
            if (actor != null)
            {
                int net = TacticalDeploySync.NetIdForLiveActor(actor);
                if (net >= 0) targetNetId = net;
            }
            object pos = GetField(parameter, "PositionToApply");
            if (pos is Vector3 v && !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z))) targetPos = v;
            else if (actor != null && GetProp(actor, "Pos") is Vector3 ap) targetPos = ap;
        }

        /// <summary>Read the actor's current AP/WP via <c>CharacterStats.ActionPoints</c> / <c>.WillPoints</c>
        /// (StatusStat, implicit float cast). Returns TRUE only when the actor actually has CharacterStats +
        /// both stats (i.e. a <c>TacticalActor</c> — NOT a turret/vehicle/destructible/environmental source).
        /// A FALSE result means "no readable stats" — the caller must NOT broadcast 0/0 as authoritative AP/WP
        /// (that would let the client zero the actor's real AP/WP).</summary>
        private static bool ReadApWp(object actor, out float ap, out float wp)
        {
            ap = 0f; wp = 0f;
            object stats = GetProp(actor, "CharacterStats");
            if (stats == null) return false;                       // TacticalActorBase w/o stats (turret/vehicle/etc.)
            object apStat = GetField(stats, "ActionPoints");
            object wpStat = GetField(stats, "WillPoints");
            if (apStat == null || wpStat == null) return false;
            ap = StatValue(apStat);
            wp = StatValue(wpStat);
            return true;
        }

        /// <summary>Set the actor's AP/WP via <c>BaseStat.Set(float)</c> on the AP/WP stats.</summary>
        private static void SetApWp(object actor, float ap, float wp)
        {
            try
            {
                object stats = GetProp(actor, "CharacterStats");
                if (stats == null) return;
                SetStat(GetField(stats, "ActionPoints"), ap);
                SetStat(GetField(stats, "WillPoints"), wp);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] SetApWp failed: " + ex); }
        }

        private static float StatValue(object stat)
        {
            if (stat == null) return 0f;
            // BaseStat has implicit operator float. Reflectively: read its ModifiableValue Value or call the op.
            try
            {
                var op = AccessTools.Method(stat.GetType(), "op_Implicit", new[] { stat.GetType() });
                if (op != null) return Convert.ToSingle(op.Invoke(null, new[] { stat }));
            }
            catch { }
            // Fallback: BaseStat.Value is a ModifiableValue; its float EndValue / implicit float.
            try { return Convert.ToSingle(GetProp(stat, "IntValue") ?? 0); } catch { return 0f; }
        }

        private static void SetStat(object stat, float value)
        {
            if (stat == null) return;
            var set = AccessTools.Method(stat.GetType(), "Set", new[] { typeof(float), typeof(bool) });
            if (set != null) { set.Invoke(stat, new object[] { value, true }); return; }
            var set1 = AccessTools.Method(stat.GetType(), "Set", new[] { typeof(float) });
            set1?.Invoke(stat, new object[] { value });
        }

        private static Vector3 ToVec3(object o) => o is Vector3 v ? v : Vector3.zero;

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static object GetField(object obj, string name)
        {
            if (obj == null) return null;
            var f = AccessTools.Field(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            var p = AccessTools.Property(obj.GetType(), name);
            return p != null ? p.GetValue(obj, null) : null;
        }

        // Struct field-set: a boxed struct must be re-boxed on each set (SetValue mutates the box in place
        // for reference-typed boxes, which a boxed struct IS — so a single boxed instance accumulates all
        // sets). The `ref` keeps the API explicit that `dr` is the live box.
        private static void SetField(Type type, ref object boxed, string name, object value)
        {
            var f = AccessTools.Field(type, name);
            if (f != null) f.SetValue(boxed, value);
            else Debug.LogError("[Multiplayer][tac] DamageResult field not found: " + name);
        }
    }
}
