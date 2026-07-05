using System;
using System.Collections;
using HarmonyLib;
using Multiplayer.Harmony.Tactical;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// LIVE host-authoritative OVERWATCH-ARM replication (Inc Overwatch). In co-op a client putting a soldier
    /// on overwatch ran ONLY locally — the HOST (the authority that runs enemy turns) never armed that soldier,
    /// so the host never triggered the reaction fire, and the watch cone never showed on peers. Mirrors
    /// <see cref="TacticalEquipSync"/> / <see cref="TacticalCombatSync"/>'s intent→host→state→all shape:
    ///   • CLIENT arm intent: a mirroring client's <c>OverwatchAbility.Activate(object)</c> is suppressed; it
    ///     instead sends <c>tac.intent.overwatch</c> {actorNetId, nonce, flattened cone} to the host. The watch
    ///     cone is built entirely CLIENT-side (UIStateOverwatchAbilitySelected → GetAbilityTargetCone), so the
    ///     host can't re-derive the player's chosen direction/spread — the cone rides the wire.
    ///   • HOST intent handler: resolve actor → its <c>OverwatchAbility</c>, rebuild the <c>Cone</c>, wrap it in
    ///     a <c>TacticalAbilityTarget{Cone=…}</c>, and re-invoke the REAL <c>Activate</c> on the host actor. The
    ///     host is now AUTHORITATIVELY armed → its <c>TacticalLevelController.TriggerOverwatch</c> fires the
    ///     reaction shot on enemy moves (the reaction DAMAGE already replicates via <c>tac.damage</c> 0x88).
    ///   • HOST state broadcast: a postfix on the single cone funnel <c>OverwatchStatus.SetCone(Cone?)</c> —
    ///     the sink BOTH arm (SetCone(realCone), from Activate→StartOverwatch) AND clear (SetCone(null), from
    ///     OnUnapply, fired by EVERY status removal: consume-after-reaction, next-turn expiry, manual cancel)
    ///     funnel through. The host reads the actor + the cone and broadcasts <c>tac.overwatch.state</c>.
    ///   • CLIENT apply (COSMETIC only): armed → (re)create the actor's <c>OverwatchStatus</c> + <c>SetCone</c>
    ///     so the cone shows; cleared → remove that status so the cone disappears — all under a re-entrancy flag.
    ///     The client's mirrored status is INERT: client enemy-moves mirror with <c>TriggerOverwatch=false</c>
    ///     (TacticalMoveSync.GetMirrorNavSettings), so a client-side <c>OverwatchStatus</c> is NEVER consulted by
    ///     a reaction path → the client NEVER double reaction-fires; it only shows the cone.
    ///
    /// All game types are reached by name via <see cref="AccessTools"/> (this is the reflection boundary). The
    /// PURE wire codec is <see cref="TacticalLiveCodec"/> (unit-tested).
    /// </summary>
    public static class TacticalOverwatchSync
    {
        private static uint _nonceCounter;
        private static uint NextNonce() => unchecked(++_nonceCounter);

        // Re-entrancy guard: true only while THIS instance is applying a host-relayed overwatch state (client
        // cosmetic apply). The SetCone postfix (OnHostSetCone) checks it (defense-in-depth on top of IsHost) so
        // the client's own cosmetic SetCone never re-broadcasts; the Activate prefix checks it so a re-entrant
        // host/relayed Activate passes through without re-sending an intent.
        [ThreadStatic] private static bool _applyingRemote;

        // ─── CLIENT: intercept the local arm, send intent, suppress ────────────────────────────────
        /// <summary>
        /// CLIENT (mirroring) entry from <c>OverwatchAbilityActivatePatch</c>: capture {actorNetId, cone} from
        /// the ability + its <c>TacticalAbilityTarget</c> parameter and send <c>tac.intent.overwatch</c> to the
        /// host. Returns false to SUPPRESS the local arm (the host runs the authoritative arm + broadcasts the
        /// state). Returns true (let the arm run) when re-applying a host outcome, or this is NOT a mirroring
        /// client (host / single-player run the real arm and the SetCone postfix broadcasts). A mirroring client
        /// SUPPRESSES even on a read miss — it must never arm a local-only overwatch that the host won't fire.
        /// </summary>
        public static bool ClientInterceptArm(object overwatchAbility, object parameter)
        {
            if (_applyingRemote) return true;                         // applying a host outcome → run locally, no re-send
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / single-player / non-mirror
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return true;

            try
            {
                object actor = GetProp(overwatchAbility, "TacticalActorBase") ?? GetProp(overwatchAbility, "TacticalActor");
                if (actor == null)
                {
                    Debug.LogError("[Multiplayer][tac] overwatch intent: no actor on ability — suppressing local arm");
                    return false;
                }
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0)
                {
                    Debug.LogError("[Multiplayer][tac] overwatch intent: unknown actor netId — suppressing local arm");
                    return false;
                }

                if (!ReadConeFromTarget(parameter, out var c))
                {
                    Debug.LogError("[Multiplayer][tac] overwatch intent: could not read cone from target — suppressing local arm");
                    return false;
                }

                byte[] payload = TacticalLiveCodec.EncodeOverwatchIntent(
                    actorNetId, NextNonce(), c.TipX, c.TipY, c.TipZ, c.Height, c.Radius, c.FwdX, c.FwdY, c.FwdZ);
                TacticalMoveSync.SendToHost(engine, TacticalSurfaceIds.TacIntentOverwatch, payload);
                Debug.Log("[Multiplayer][tac] CLIENT sent tac.intent.overwatch actorNetId=" + actorNetId +
                          " coneH=" + c.Height.ToString("0.0") + " coneR=" + c.Radius.ToString("0.0"));

                // The suppressed-arm view freeze (the native ActivateAbility ClearStackAndPush wedge) is now PREVENTED
                // up-front by SuppressedAbilityViewClearPatch (CLIENT prefix on TacticalViewState.ActivateAbility) —
                // no per-arm runtime diagnostics needed here.
                return false;   // suppress local arm
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ClientInterceptArm failed: " + ex);
                return false;   // a mirroring client must not arm a local-only overwatch even on error
            }
        }

        // ─── HOST: a client overwatch intent arrived → run the real arm ────────────────────────────
        /// <summary>HOST inbound (<c>tac.intent.overwatch</c>): resolve actor → its <c>OverwatchAbility</c>,
        /// rebuild the cone + a <c>TacticalAbilityTarget{Cone}</c>, then invoke the real <c>Activate</c> on the
        /// host actor. The host is NOT mirroring, so the prefix passes through and the host SetCone postfix
        /// (<see cref="OnHostSetCone"/>) broadcasts <c>tac.overwatch.state</c>. No-op off-host / off-session.</summary>
        public static void HostOnArmIntent(ulong senderPeerId, byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeOverwatchIntent(payload, out var intent)) { Debug.LogError("[Multiplayer][tac] overwatch intent decode failed"); return; }
            if (!TacticalDeploySync.IntentDedup.IsNew(senderPeerId, TacticalSurfaceIds.TacIntentOverwatch, intent.Nonce)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(intent.ActorNetId);
            Debug.Log("[Multiplayer][tac][DIAG] HOSTINTENT decoded overwatch actorNetId=" + intent.ActorNetId +
                      " actorResolved=" + (actor != null));
            if (actor == null) { Debug.LogError("[Multiplayer][tac] overwatch intent: no actor for netId " + intent.ActorNetId); return; }

            try
            {
                object overwatchAbility = ResolveOverwatchAbility(actor);
                if (overwatchAbility == null) { Debug.LogError("[Multiplayer][tac] overwatch intent: actor " + intent.ActorNetId + " has no OverwatchAbility"); return; }

                object cone = BuildCone(intent.TipX, intent.TipY, intent.TipZ, intent.Height, intent.Radius, intent.FwdX, intent.FwdY, intent.FwdZ);
                if (cone == null) { Debug.LogError("[Multiplayer][tac] overwatch intent: could not build cone"); return; }

                // RE-ANCHOR the cone Tip to the HOST actor's own authoritative eye point. The client built the
                // cone in CLIENT space (Cone.Tip = the CLIENT actor's TacticalActor.VisionPoint — same as the
                // native OverwatchAbility.cs:67 / GetAbilityTargetCone), so applying it verbatim on the host can
                // leave Tip translated relative to where the host's authoritative enemies actually move →
                // TacticalLevelController.ExecuteOverwatch (TLC.cs:1388) Contains(target.Pos||VisionPoint) is
                // false for every mover → the watcher never reaction-fires and the cone just expires. Overwrite
                // Tip with the HOST actor's VisionPoint (TacticalActorBase.cs:172, the host's own eye point),
                // KEEPING the client's Forward/Height/Radius (aim direction + shape). Re-anchoring the host's own
                // soldier's cone to its own eye point is correct by definition — harmless if positions are
                // already synced (delta ≈ 0). The DIAG below reports the gap so the in-game test can CONFIRM the
                // cause (large delta → this fix repairs it; ~0 → the real gate is LOS/weapon at TLC.cs:1394).
                Vector3 intentTip = new Vector3(intent.TipX, intent.TipY, intent.TipZ);
                object hostVisionBoxed = GetProp(actor, "VisionPoint");
                if (hostVisionBoxed is Vector3 hostVision)
                {
                    SetField(ConeType, ref cone, "Tip", hostVision);
                    float delta = (hostVision - intentTip).magnitude;
                    Debug.Log("[Multiplayer][tac] overwatch arm: intent.Tip=(" +
                              intentTip.x.ToString("0.00") + "," + intentTip.y.ToString("0.00") + "," + intentTip.z.ToString("0.00") +
                              ") hostVisionPoint=(" +
                              hostVision.x.ToString("0.00") + "," + hostVision.y.ToString("0.00") + "," + hostVision.z.ToString("0.00") +
                              ") delta=" + delta.ToString("0.000"));
                }
                else Debug.LogError("[Multiplayer][tac] overwatch arm: could not read host actor VisionPoint — arming with client-space Tip (cone may miss)");

                object target = BuildOverwatchTarget(cone);
                if (target == null) { Debug.LogError("[Multiplayer][tac] overwatch intent: could not build TacticalAbilityTarget"); return; }

                // public override void Activate(object parameter) — runs the real arm (StartOverwatch applies
                // OverwatchStatus + SetCone → the SetCone postfix broadcasts the state). The host is now
                // authoritatively armed → TriggerOverwatch fires the reaction shot on enemy moves.
                var activate = AccessTools.Method(overwatchAbility.GetType(), "Activate", new[] { typeof(object) });
                if (activate == null) { Debug.LogError("[Multiplayer][tac] overwatch intent: Activate(object) not found"); return; }
                // BUG2: hold the host camera-follow guard across the relayed client OVERWATCH arm so the synchronous
                // Activate camera hint can't fly the host camera to the client's soldier. try/finally pops on throw.
                FireReplayGate.EnterHostApply();
                try { activate.Invoke(overwatchAbility, new[] { target }); }
                finally { FireReplayGate.ExitHostApply(); }
                Debug.Log("[Multiplayer][tac] HOST armed overwatch actorNetId=" + intent.ActorNetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnArmIntent exec failed: " + ex); }
        }

        // ─── HOST: SetCone ran → broadcast the now-armed/cleared overwatch state ────────────────────
        /// <summary>HOST postfix on <c>OverwatchStatus.SetCone(Cone?)</c> — the single funnel for BOTH arm
        /// (real cone) and clear (null/default cone, via OnUnapply). Read the actor + the cone and broadcast
        /// <c>tac.overwatch.state</c>. Gated: only on the host, only in a live session, and NOT while applying a
        /// relayed state (<see cref="_applyingRemote"/>). The boxed argument is a <c>Nullable&lt;Cone&gt;</c>:
        /// null OR a default cone → ARMED=false (clear); a real cone → ARMED=true + flattened fields.</summary>
        public static void OnHostSetCone(object overwatchStatus, object coneNullableBoxed)
        {
            if (_applyingRemote) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;   // defensive: host is never mirroring
            if (overwatchStatus == null) return;

            try
            {
                object actor = GetProp(overwatchStatus, "TacticalActor") ?? GetProp(overwatchStatus, "TacticalActorBase");
                if (actor == null) return;
                int actorNetId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (actorNetId < 0) return;   // an unregistered actor — skip

                bool armed = TryFlattenCone(coneNullableBoxed, out var c);
                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacOverwatchState);
                byte[] payload = armed
                    ? TacticalLiveCodec.EncodeOverwatchState(seq, actorNetId, true, c.TipX, c.TipY, c.TipZ, c.Height, c.Radius, c.FwdX, c.FwdY, c.FwdZ)
                    : TacticalLiveCodec.EncodeOverwatchClear(seq, actorNetId);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacOverwatchState, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.overwatch.state seq=" + seq + " actorNetId=" + actorNetId +
                          " armed=" + armed);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] OnHostSetCone failed: " + ex); }
        }

        // ─── CLIENT: apply the host overwatch state (COSMETIC cone only) ────────────────────────────
        /// <summary>CLIENT inbound (<c>tac.overwatch.state</c>): armed → (re)create the actor's
        /// <c>OverwatchStatus</c> + <c>SetCone</c> so the cone shows; cleared → remove that status so the cone
        /// disappears. All under <see cref="_applyingRemote"/> so the client's own SetCone postfix never
        /// re-broadcasts. Guarded by the per-surface seq (last-writer-wins). The applied status is COSMETIC: a
        /// client enemy-move mirror carries TriggerOverwatch=false, so this status is never consulted by a
        /// reaction path → no double reaction-fire. No-op off-client / off-session.</summary>
        public static void HandleOverwatchState(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeOverwatchState(payload, out var s)) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacOverwatchState, s.Seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(s.ActorNetId);
            if (actor == null) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state: no actor for netId " + s.ActorNetId); return; }

            try
            {
                _applyingRemote = true;
                try
                {
                    if (s.Armed) ApplyCosmeticArm(actor, s);
                    else RemoveCosmeticArm(actor);
                }
                finally { _applyingRemote = false; }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacOverwatchState, s.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.overwatch.state seq=" + s.Seq + " actorNetId=" + s.ActorNetId +
                          " armed=" + s.Armed);

                // NOTE: the client view-freeze is now PREVENTED up-front by SuppressedAbilityViewClearPatch (a CLIENT
                // prefix on TacticalViewState.ActivateAbility that skips the wedging ClearStackAndPush for suppressed
                // non-shoot abilities incl. OverwatchAbility), so no after-the-fact control-view recovery is needed here.
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleOverwatchState failed: " + ex); }
        }

        // ─── CLIENT cosmetic apply helpers ──────────────────────────────────────────────────────────

        /// <summary>Cosmetically show the watch cone on a mirrored actor: resolve the actor's OverwatchAbility →
        /// its OverwatchStatusDef + source weapon ItemDef (the SAME refs the native arm uses — OverwatchAbility.cs
        /// :101), apply the status, then SetCone with the host's cone. If the actor already has an OverwatchStatus
        /// we just SetCone on it (re-aim) instead of stacking a duplicate.</summary>
        private static void ApplyCosmeticArm(object actor, TacticalLiveCodec.OverwatchState s)
        {
            object cone = BuildCone(s.TipX, s.TipY, s.TipZ, s.Height, s.Radius, s.FwdX, s.FwdY, s.FwdZ);
            if (cone == null) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state: could not build client cone"); return; }

            object statusComponent = GetProp(actor, "Status");
            if (statusComponent == null) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state: actor has no Status component"); return; }

            // Re-aim an existing cosmetic overwatch in place (avoid stacking duplicates on re-broadcast).
            object existing = FindOverwatchStatus(statusComponent);
            if (existing != null) { InvokeSetCone(existing, cone); return; }

            object overwatchAbility = ResolveOverwatchAbility(actor);
            if (overwatchAbility == null) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state: actor has no OverwatchAbility to derive defs"); return; }
            object abilityDef = GetProp(overwatchAbility, "OverwatchAbilityDef") ?? GetProp(overwatchAbility, "Def");
            object statusDef = GetProp(abilityDef, "OverwatchStatus");
            if (statusDef == null) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state: OverwatchAbilityDef.OverwatchStatus null"); return; }
            object weapon = GetProp(overwatchAbility, "OverwatchWeapon");
            object weaponItemDef = weapon != null ? GetProp(weapon, "ItemDef") : null;

            // public Status ApplyStatus(StatusDef def, object source = null, object target = null)
            object applied = InvokeApplyStatus(statusComponent, statusDef, weaponItemDef);
            if (applied == null) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state: ApplyStatus returned null"); return; }
            InvokeSetCone(applied, cone);
        }

        /// <summary>Cosmetically clear the watch cone: find the mirrored actor's OverwatchStatus and unapply it
        /// (OnUnapply → SetCone(null) destroys the cone visuals). No-op if none present.</summary>
        private static void RemoveCosmeticArm(object actor)
        {
            object statusComponent = GetProp(actor, "Status");
            if (statusComponent == null) return;
            object existing = FindOverwatchStatus(statusComponent);
            if (existing == null) return;
            InvokeUnapplyStatus(statusComponent, existing);
        }

        // ─── Engine reflection helpers ──────────────────────────────────────────────────────────────

        private static Type _coneType;
        private static Type ConeType => _coneType ?? (_coneType = AccessTools.TypeByName("Base.Utils.Maths.Cone"));
        private static Type _overwatchAbilityType;
        private static Type OverwatchAbilityType => _overwatchAbilityType ??
            (_overwatchAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.OverwatchAbility"));
        private static Type _overwatchStatusType;
        private static Type OverwatchStatusType => _overwatchStatusType ??
            (_overwatchStatusType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Statuses.OverwatchStatus"));

        /// <summary>Flattened cone fields the codec carries.</summary>
        private struct ConeFields
        {
            public float TipX, TipY, TipZ, Height, Radius, FwdX, FwdY, FwdZ;
        }

        /// <summary>Read the cone from a <c>TacticalAbilityTarget</c> (its public <c>Cone Cone</c> field) and
        /// flatten it. Returns false if the parameter / cone can't be read.</summary>
        private static bool ReadConeFromTarget(object parameter, out ConeFields c)
        {
            c = default(ConeFields);
            if (parameter == null) return false;
            object coneBoxed = GetField(parameter, "Cone");
            if (coneBoxed == null) return false;
            return FlattenConeStruct(coneBoxed, out c);
        }

        /// <summary>Flatten a boxed <c>Nullable&lt;Cone&gt;</c> (the SetCone arg). Returns true (ARMED) with the
        /// fields filled when it is a present, non-default cone; false (CLEAR) when null or a default cone.</summary>
        private static bool TryFlattenCone(object coneNullableBoxed, out ConeFields c)
        {
            c = default(ConeFields);
            if (coneNullableBoxed == null) return false;   // SetCone(null) → clear
            // A boxed Nullable<Cone> with a value boxes as the underlying Cone struct (HasValue==true). So a
            // non-null box IS the Cone; flatten it, then treat an all-zero (default) cone as a clear.
            if (!FlattenConeStruct(coneNullableBoxed, out c)) return false;
            // A default cone (Height==0 && Radius==0 && forward zero) is the engine's "no cone" sentinel.
            bool isDefault = c.Height == 0f && c.Radius == 0f && c.TipX == 0f && c.TipY == 0f && c.TipZ == 0f
                             && c.FwdX == 0f && c.FwdY == 0f && c.FwdZ == 0f;
            return !isDefault;
        }

        /// <summary>Flatten a boxed <c>Cone</c> struct: Tip(Vector3), Height(float), Radius(float),
        /// Forward(Vector3, via the normalizing Forward property). Fields grounded against Cone.cs.</summary>
        private static bool FlattenConeStruct(object coneBoxed, out ConeFields c)
        {
            c = default(ConeFields);
            if (coneBoxed == null) return false;
            try
            {
                Vector3 tip = ToVec3(GetField(coneBoxed, "Tip"));
                Vector3 fwd = ToVec3(GetProp(coneBoxed, "Forward"));   // Forward is a property (getter → _forward)
                float height = Convert.ToSingle(GetField(coneBoxed, "Height") ?? 0f);
                float radius = Convert.ToSingle(GetField(coneBoxed, "Radius") ?? 0f);
                c = new ConeFields
                {
                    TipX = tip.x, TipY = tip.y, TipZ = tip.z, Height = height, Radius = radius,
                    FwdX = fwd.x, FwdY = fwd.y, FwdZ = fwd.z,
                };
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] FlattenConeStruct failed: " + ex); return false; }
        }

        /// <summary>Rebuild a boxed <c>Cone</c> struct from flattened fields: set Tip/Height/Radius fields and
        /// the Forward PROPERTY (its setter normalizes the direction, matching how the engine builds it).</summary>
        private static object BuildCone(float tipX, float tipY, float tipZ, float height, float radius, float fwdX, float fwdY, float fwdZ)
        {
            var t = ConeType;
            if (t == null) { Debug.LogError("[Multiplayer][tac] Cone type not found"); return null; }
            try
            {
                object cone = Activator.CreateInstance(t);   // struct default
                SetField(t, ref cone, "Tip", new Vector3(tipX, tipY, tipZ));
                SetField(t, ref cone, "Height", height);
                SetField(t, ref cone, "Radius", radius);
                // Forward is a property whose setter normalizes; use it so _forward matches the engine's value.
                var fwdProp = AccessTools.Property(t, "Forward");
                if (fwdProp != null && fwdProp.CanWrite) fwdProp.SetValue(cone, new Vector3(fwdX, fwdY, fwdZ), null);
                else SetField(t, ref cone, "_forward", new Vector3(fwdX, fwdY, fwdZ));
                return cone;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildCone failed: " + ex); return null; }
        }

        /// <summary>Wrap a cone in a fresh <c>TacticalAbilityTarget</c> (its public <c>Cone Cone</c> field).</summary>
        private static object BuildOverwatchTarget(object cone)
        {
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) { Debug.LogError("[Multiplayer][tac] TacticalAbilityTarget type not found"); return null; }
            try
            {
                object target = Activator.CreateInstance(targetType);
                var coneField = AccessTools.Field(targetType, "Cone");
                if (coneField == null) { Debug.LogError("[Multiplayer][tac] TacticalAbilityTarget.Cone field not found"); return null; }
                coneField.SetValue(target, cone);
                return target;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] BuildOverwatchTarget failed: " + ex); return null; }
        }

        /// <summary>Resolve a <c>TacticalActor</c>'s <c>OverwatchAbility</c> by enumerating
        /// <c>GetAbilities&lt;TacticalAbility&gt;()</c> and matching the OverwatchAbility type (mirrors
        /// <c>TacticalCombatSync.ResolveAbilityByGuid</c>'s enumeration shape).</summary>
        private static object ResolveOverwatchAbility(object actor)
        {
            try
            {
                var owType = OverwatchAbilityType;
                if (owType == null) return null;
                var tacAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
                if (tacAbilityType == null) return null;
                var getAbilities = AccessTools.Method(actor.GetType(), "GetAbilities");
                if (getAbilities == null || !getAbilities.IsGenericMethodDefinition) return null;
                var gen = getAbilities.MakeGenericMethod(tacAbilityType);
                if (gen.GetParameters().Length != 0) return null;   // GetAbilities<T>() — the no-arg overload
                if (!(gen.Invoke(actor, null) is IEnumerable result)) return null;
                foreach (var a in result)
                {
                    if (a != null && owType.IsInstanceOfType(a)) return a;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveOverwatchAbility failed: " + ex); }
            return null;
        }

        /// <summary>The actor's existing <c>OverwatchStatus</c> (or null): enumerate the StatusComponent's
        /// <c>Statuses</c> and match the OverwatchStatus type (avoids the reflective generic GetStatus&lt;T&gt;).</summary>
        private static object FindOverwatchStatus(object statusComponent)
        {
            try
            {
                var owStatusType = OverwatchStatusType;
                if (owStatusType == null) return null;
                if (!(GetProp(statusComponent, "Statuses") is IEnumerable statuses)) return null;
                foreach (var s in statuses)
                    if (s != null && owStatusType.IsInstanceOfType(s)) return s;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] FindOverwatchStatus failed: " + ex); }
            return null;
        }

        private static object InvokeApplyStatus(object statusComponent, object statusDef, object source)
        {
            try
            {
                var statusDefType = AccessTools.TypeByName("Base.Entities.Statuses.StatusDef");
                if (statusDefType == null) return null;
                // public Status ApplyStatus(StatusDef def, object source = null, object target = null)
                var m = AccessTools.Method(statusComponent.GetType(), "ApplyStatus", new[] { statusDefType, typeof(object), typeof(object) });
                if (m == null) { Debug.LogError("[Multiplayer][tac] ApplyStatus(StatusDef,object,object) not found"); return null; }
                return m.Invoke(statusComponent, new[] { statusDef, source, null });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] InvokeApplyStatus failed: " + ex); return null; }
        }

        private static void InvokeUnapplyStatus(object statusComponent, object status)
        {
            try
            {
                var statusType = AccessTools.TypeByName("Base.Entities.Statuses.Status");
                if (statusType == null) return;
                var m = AccessTools.Method(statusComponent.GetType(), "UnapplyStatus", new[] { statusType });
                if (m == null) { Debug.LogError("[Multiplayer][tac] UnapplyStatus(Status) not found"); return; }
                m.Invoke(statusComponent, new[] { status });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] InvokeUnapplyStatus failed: " + ex); }
        }

        private static void InvokeSetCone(object overwatchStatus, object cone)
        {
            try
            {
                var coneType = ConeType;
                if (coneType == null) return;
                var nullableConeType = typeof(Nullable<>).MakeGenericType(coneType);
                var m = AccessTools.Method(overwatchStatus.GetType(), "SetCone", new[] { nullableConeType });
                if (m == null) { Debug.LogError("[Multiplayer][tac] SetCone(Cone?) not found"); return; }
                m.Invoke(overwatchStatus, new[] { cone });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] InvokeSetCone failed: " + ex); }
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

        // Struct field-set: a boxed struct mutates in place (the box is a reference), so the single boxed
        // instance accumulates all sets. The `ref` keeps the API explicit that `boxed` is the live box.
        private static void SetField(Type type, ref object boxed, string name, object value)
        {
            var f = AccessTools.Field(type, name);
            if (f != null) f.SetValue(boxed, value);
            else Debug.LogError("[Multiplayer][tac] Cone field not found: " + name);
        }
    }
}
