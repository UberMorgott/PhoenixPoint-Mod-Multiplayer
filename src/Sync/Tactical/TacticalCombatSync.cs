using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using Multipleer.Network.Sync;
using UnityEngine;

namespace Multipleer.Sync.Tactical
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
                // HOST / single-player / non-mirror: the native ability runs unchanged. Feature C — this is the
                // SINGLE host choke for a relayable attack BEGINNING (the host's own click AND a relayed client
                // intent re-Activated in HostOnAbilityIntent both run through the patched Activate(object), so
                // they both trip this once). Broadcast tac.fire.start NOW so every client mirror plays the
                // shooting/throw animation CONCURRENTLY with the host. Animation-only; DAMAGE rides tac.damage.
                // Fail-open: HostBroadcastFireStart logs + swallows, never blocking the native attack.
                TacticalFireAnimSync.HostBroadcastFireStart(ability, parameter);
                // Feature C (melee): the same host choke also begins a MELEE swing. The fire-start and
                // melee-start gates are DISJOINT (shoot/grenade → fire 0x90; BashAbility → melee 0x91), so at
                // most ONE of these broadcasts per attack — a Bash emits melee-start only, never fire-start.
                // Animation-only; DAMAGE rides tac.damage. Fail-open: logs + swallows, never blocks the attack.
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
                    Debug.LogError("[Multipleer][tac] ability intent: no actor — suppressing local activation");
                    return false;
                }
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0)
                {
                    Debug.LogError("[Multipleer][tac] ability intent: unknown actor netId — suppressing local activation");
                    return false;
                }

                // FIX: read the def guid off Ability.BaseDef (the real guid-bearing def), not "Def" (null).
                string abilityGuid = DefReflection.GetGuid(GetProp(ability, "BaseDef"));
                if (string.IsNullOrEmpty(abilityGuid))
                {
                    // Should never happen now BaseDef is read; kept so we never SILENTLY suppress again.
                    Debug.LogError("[Multipleer][tac] ability intent: no ability def guid (type=" +
                                   ability.GetType().Name + ") — suppressing local activation");
                    return false;
                }

                ReadTarget(parameter, out int targetNetId, out Vector3 targetPos);

                byte[] payload = TacticalLiveCodec.EncodeIntentAbility(
                    actorNetId, abilityGuid, targetNetId, targetPos.x, targetPos.y, targetPos.z, NextNonce());
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacIntentAbility, payload);
                Debug.Log("[Multipleer][tac] CLIENT sent tac.intent.ability actor=" + actorNetId +
                          " type=" + ability.GetType().Name + " ability=" + abilityGuid +
                          " targetNetId=" + targetNetId +
                          " pos=(" + targetPos.x.ToString("0.0") + "," + targetPos.y.ToString("0.0") + "," + targetPos.z.ToString("0.0") + ")");
                return false;   // suppress local activation (host is authoritative)
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] ClientInterceptAbility failed: " + ex);
                return false;   // a mirroring client must not run a local ability even on error
            }
        }

        // ─── HOST: a client shoot intent arrived → run the real shot ──────────────────────────────
        /// <summary>HOST inbound (<c>tac.intent.ability</c>): resolve shooter→ability(by guid)→target, then
        /// invoke the real shot on the host sim. The host's authoritative roll chain fires
        /// (FireWeaponAtTargetCrt → ApplyDamage), and each ApplyDamage broadcasts a tac.damage. No-op
        /// off-host / off-session.</summary>
        public static void HostOnAbilityIntent(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeIntentAbility(payload, out var intent)) { Debug.LogError("[Multipleer][tac] shoot intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(TacticalSurfaceIds.TacIntentAbility, intent.Nonce)) return;

            object shooter = TacticalDeploySync.ResolveLiveActor(intent.ShooterNetId);
            Debug.Log("[Multipleer][tac][DIAG] HOSTINTENT decoded shooter=" + intent.ShooterNetId +
                      " ability=" + intent.AbilityDefGuid + " targetNetId=" + intent.TargetNetId +
                      " shooterResolved=" + (shooter != null));
            if (shooter == null) { Debug.LogError("[Multipleer][tac] shoot intent: no shooter for netId " + intent.ShooterNetId); return; }

            try
            {
                object ability = ResolveAbilityByGuid(shooter, intent.AbilityDefGuid);
                if (ability == null) { Debug.LogError("[Multipleer][tac] shoot intent: shooter has no ability with guid " + intent.AbilityDefGuid); return; }

                object target = BuildShootTarget(intent);
                if (target == null) { Debug.LogError("[Multipleer][tac] shoot intent: could not build target"); return; }

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
                if (activate == null) { Debug.LogError("[Multipleer][tac] ability intent: Activate(object) not found"); return; }
                activate.Invoke(ability, new[] { target });
                Debug.Log("[Multipleer][tac] HOST executed ability " + ability.GetType().Name +
                          " (guid=" + intent.AbilityDefGuid + ") for actor " + intent.ShooterNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HostOnAbilityIntent exec failed: " + ex); }
        }

        // ─── HOST: ApplyDamage funneled → broadcast the FINAL applied DamageResult ─────────────────
        /// <summary>HOST postfix on <c>TacticalActorBase.ApplyDamage(DamageResult)</c>: flatten the FINAL
        /// applied result + broadcast <c>tac.damage</c> to all peers. Gated: only on the host, only in a live
        /// session, and NOT while the host is itself applying a relayed result (<see cref="_applyingRemote"/>).
        /// Postfix so it captures the result AFTER the engine finalized it.</summary>
        public static void OnHostApplyDamage(object target, object damageResultBoxed)
        {
            if (_applyingRemote) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;   // defensive: host is never mirroring
            if (target == null || damageResultBoxed == null) return;

            try
            {
                int targetNetId = TacticalDeploySync.NetIdForLiveActor(target);
                if (targetNetId < 0) return;   // an unregistered actor (e.g. transient destructible) — skip

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
                Debug.Log("[Multipleer][tac] HOST broadcast tac.damage seq=" + p.Seq + " targetNetId=" + targetNetId +
                          " sourceNetId=" + p.SourceNetId + " HealthDamage=" + p.HealthDamage.ToString("0.0") +
                          " statuses=" + p.Statuses.Count + " effects=" + p.EffectGuids.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] OnHostApplyDamage failed: " + ex); }
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
            if (!TacticalLiveCodec.TryDecodeDamage(payload, out var p)) { Debug.LogError("[Multipleer][tac] tac.damage decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacDamage, p.Seq)) return;

            object target = TacticalDeploySync.ResolveLiveActor(p.TargetNetId);
            if (target == null) { Debug.LogError("[Multipleer][tac] tac.damage: no actor for targetNetId " + p.TargetNetId); return; }

            try
            {
                object source = p.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(p.SourceNetId) : null;
                object damageResult = RebuildDamage(p, source);
                if (damageResult == null) { Debug.LogError("[Multipleer][tac] tac.damage: could not rebuild DamageResult"); return; }

                var apply = AccessTools.Method(target.GetType(), "ApplyDamage", new[] { _damageResultType });
                if (apply == null) { Debug.LogError("[Multipleer][tac] tac.damage: ApplyDamage(DamageResult) not found"); return; }

                _applyingRemote = true;
                try { apply.Invoke(target, new[] { damageResult }); }
                finally { _applyingRemote = false; }

                // Set the shooter's spent AP/WP to the host's post-shot values (the client never ran costs).
                if (p.ShooterNetId >= 0)
                {
                    object shooter = TacticalDeploySync.ResolveLiveActor(p.ShooterNetId);
                    if (shooter != null) SetApWp(shooter, p.ShooterApAfter, p.ShooterWpAfter);
                }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacDamage, p.Seq);
                Debug.Log("[Multipleer][tac] CLIENT applied tac.damage seq=" + p.Seq + " targetNetId=" + p.TargetNetId +
                          " HealthDamage=" + p.HealthDamage.ToString("0.0"));
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HandleDamage failed: " + ex); }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] FlattenDamage failed: " + ex); return null; }
        }

        /// <summary>Rebuild a boxed <c>DamageResult</c> struct from a wire payload: numeric fields + defs
        /// resolved by guid (null-skip) + source set to the resolved live actor (or null). <c>ImpactHit</c>
        /// is left default (local-FX only). Statuses/effects/statMods are rebuilt and the native ApplyDamage
        /// applies them.</summary>
        private static object RebuildDamage(TacticalLiveCodec.DamagePayload p, object sourceActor)
        {
            var t = DamageResultType;
            if (t == null) { Debug.LogError("[Multipleer][tac] RebuildDamage: DamageResult type not found"); return null; }
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

                // ApplyStatuses: rebuild List<StatusApplication> with resolved StatusDef + source actor.
                if (p.Statuses != null && p.Statuses.Count > 0)
                {
                    var statusAppType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.StatusApplication");
                    if (statusAppType != null)
                    {
                        var listType = typeof(List<>).MakeGenericType(statusAppType);
                        var list = (IList)Activator.CreateInstance(listType);
                        foreach (var s in p.Statuses)
                        {
                            object def = DefReflection.GetDefByGuid(s.DefGuid);
                            if (def == null) continue;
                            object sa = Activator.CreateInstance(statusAppType);
                            SetField(statusAppType, ref sa, "StatusDef", def);
                            SetField(statusAppType, ref sa, "Value", s.Value);
                            object src = s.SourceNetId >= 0 ? TacticalDeploySync.ResolveLiveActor(s.SourceNetId) : null;
                            SetField(statusAppType, ref sa, "StatusSource", src);
                            list.Add(sa);
                        }
                        if (list.Count > 0) SetField(t, ref dr, "ApplyStatuses", list);
                    }
                }

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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] RebuildDamage failed: " + ex); return null; }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ResolveSourceActor failed: " + ex); }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ResolveAbilityByGuid failed: " + ex); }
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
            // For an actor target, seed with the body-center aim point (non-NaN); ground fallback never used for actors.
            Vector3 aimPos = targetActor != null ? GetActorAimPosition(targetActor, groundPos) : groundPos;

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
                        return ctor.Invoke(new[] { targetActor, aimPos, DefaultAttackType(attackType) });
                }
                else
                {
                    // new TacticalAbilityTarget(Vector3 pos) — bare-ground shot (no actor to snap to).
                    var posCtor = targetType.GetConstructor(new[] { typeof(Vector3) });
                    if (posCtor != null) return posCtor.Invoke(new object[] { groundPos });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] BuildShootTarget ctor failed, falling back to field-set: " + ex); }

            // Fallback: default-ctor + field assignment (no typed ctor available / ctor threw). For an ACTOR target seed
            // Actor + GameObject + ActorGridPosition + PositionToApply(aim point) so GetWorkingPosition lands on the body
            // (never the NaN→feet fallthrough); a bare-ground target gets the explicit Vector3 ground pos.
            object target = Activator.CreateInstance(targetType);
            if (targetActor != null)
            {
                AccessTools.Field(targetType, "Actor")?.SetValue(target, targetActor);
                object go = GetProp(targetActor, "gameObject");
                if (go != null) AccessTools.Field(targetType, "GameObject")?.SetValue(target, go);
                if (GetProp(targetActor, "Pos") is Vector3 actorPos)
                    AccessTools.Field(targetType, "ActorGridPosition")?.SetValue(target, actorPos);
                AccessTools.Field(targetType, "PositionToApply")?.SetValue(target, aimPos);
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] GetActorAimPosition failed: " + ex); }
            return groundFallback;
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
                Debug.LogError("[Multipleer][tac] GetShootTarget snap failed (using aim-point target): " + ex);
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
                Debug.Log("[Multipleer][tac] HOST shoot aim item=" + itemDesc +
                          " workingPos=(" + v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] LogShootAim failed: " + ex); }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] SetApWp failed: " + ex); }
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
            else Debug.LogError("[Multipleer][tac] DamageResult field not found: " + name);
        }
    }
}
