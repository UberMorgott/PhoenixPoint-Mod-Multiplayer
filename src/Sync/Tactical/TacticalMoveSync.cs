using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// LIVE soldier-MOVE replication (spec §3.4, §5; Inc 2). Host-authoritative: a client sends a move
    /// INTENT, the host runs the real move (path resolves, may early-stop on overwatch), then the host
    /// streams the FINAL landed position to every peer, which mirror it.
    ///
    /// Intercept point (both player AND AI): <c>MoveAbility.Activate(object parameter)</c> carries the
    /// destination as <c>((TacticalAbilityTarget)parameter).PositionToApply</c> (MoveAbility.cs:41 →
    /// Move :136 navigates there). The host capture of the FINAL pose is the non-coroutine
    /// <c>MoveAbility.OnPlayingActionEnd(PlayingAction)</c> postfix (MoveAbility.cs:101), where
    /// <c>TacticalActor.Pos</c> is the landed cell and <c>TacticalNav.StopReason</c> is known.
    ///
    /// NetId scheme (spec §4): a client only sends intents for ITS OWN soldiers, which always have
    /// <c>GeoUnitId != 0</c> ⇒ NetId == GeoUnitId (shared save → identical both sides), so no reverse map
    /// is needed on send. The host resolves netId→actor via the deploy registry.
    ///
    /// Early-stop / overwatch divergence (a host move that stops short of the requested cell because an
    /// enemy was seen) is streamed as the FINAL landed pos here; the resulting reaction-fire / overwatch
    /// DAMAGE is deferred to combat-sync (Inc 3). NOTE the residual: a client mirror that animates toward
    /// the requested cell could briefly overshoot before the host's tac.move lands the final pos — we drive
    /// the mirror to the host's FINAL pos, so it converges.
    /// </summary>
    public static class TacticalMoveSync
    {
        private static uint _nonceCounter;
        private static uint NextNonce() => unchecked(++_nonceCounter);

        // ─── CLIENT: intercept the local move, send intent, suppress ──────────────────────────────
        /// <summary>
        /// CLIENT (mirroring) prefix on <c>MoveAbility.Activate</c>: capture {netId, PositionToApply}, send
        /// <c>tac.intent.move</c> to the host, and SUPPRESS the local move (return false). The host will run
        /// the authoritative move and broadcast the outcome. Returns true (let the move run) when this is
        /// NOT a mirroring client, when the destination can't be read, or on any failure (fail-open).
        /// </summary>
        public static bool ClientInterceptMove(object moveAbility, object parameter)
        {
            bool diagIsHost = NetworkEngine.Instance != null && NetworkEngine.Instance.IsActive && NetworkEngine.Instance.IsHost;
            if (!TacticalDeploySync.IsClientMirroring)
            {
                // [DIAG] host / single-player / non-mirror path: the native move runs unchanged (PASS).
                TryGetPositionToApply(parameter, out Vector3 diagPos);
                Debug.Log("[Multipleer][tac][DIAG] CAPTURE role=" + (diagIsHost ? "HOST" : "CLIENT") +
                          " mirrorArmed=" + TacticalDeploySync.IsClientMirroring + " netId=<n/a>" +
                          " posToApply=(" + diagPos.x.ToString("0.0") + "," + diagPos.y.ToString("0.0") + "," + diagPos.z.ToString("0.0") + ")" +
                          " action=PASS");
                return true;   // host / single-player / non-mirror
            }
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost)
            {
                Debug.Log("[Multipleer][tac][DIAG] CAPTURE role=" + (diagIsHost ? "HOST" : "CLIENT") +
                          " mirrorArmed=" + TacticalDeploySync.IsClientMirroring + " netId=<n/a> posToApply=(n/a) action=PASS");
                return true;
            }

            try
            {
                object actor = GetProp(moveAbility, "TacticalActorBase");
                if (actor == null)
                {
                    Debug.Log("[Multipleer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                              " netId=<no-actor> posToApply=(n/a) action=PASS");
                    return true;
                }
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0)
                {
                    Debug.Log("[Multipleer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                              " netId=" + netId + " posToApply=(n/a) action=SUPPRESS");
                    Debug.LogError("[Multipleer][tac] move intent: unknown netId for actor"); return false;
                }

                if (!TryGetPositionToApply(parameter, out Vector3 pos))
                {
                    Debug.Log("[Multipleer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                              " netId=" + netId + " posToApply=(no-pos) action=SUPPRESS");
                    Debug.LogError("[Multipleer][tac] move intent: no PositionToApply on target — suppressing local move");
                    return false;   // still suppress: a client must never run a local move
                }

                Debug.Log("[Multipleer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                          " netId=" + netId +
                          " posToApply=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") + ")" +
                          " action=SUPPRESS");
                byte[] payload = TacticalLiveCodec.EncodeMoveIntent(netId, pos.x, pos.y, pos.z, NextNonce());
                SendToHost(engine, TacticalSurfaceIds.TacIntentMove, payload);
                Debug.Log("[Multipleer][tac] CLIENT sent tac.intent.move netId=" + netId +
                          " pos=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") + ")");
                return false;   // suppress local move
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] ClientInterceptMove failed: " + ex);
                return false;   // a mirroring client must not run a local move even on error
            }
        }

        // ─── HOST: a client move intent arrived → run the real move ───────────────────────────────
        /// <summary>HOST inbound: resolve netId→actor and execute the real move toward the requested pos on
        /// the host sim. Running the native move fires <see cref="HostBroadcastMoveOutcome"/> via the
        /// OnPlayingActionEnd postfix, which streams the FINAL pose to all peers.</summary>
        public static void HostOnMoveIntent(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeMoveIntent(payload, out var intent)) { Debug.LogError("[Multipleer][tac] move intent decode failed"); return; }
            // Drop a reliable-transport double-send (a double-applied move would step the actor twice).
            if (!TacticalDeploySync.IntentDedup.IsNew(TacticalSurfaceIds.TacIntentMove, intent.Nonce)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(intent.NetId);
            // [DIAG] HOST DECODE: decoded destination + whether the netId resolved to a live actor.
            Debug.Log("[Multipleer][tac][DIAG] HOSTINTENT decoded pos=(" +
                      intent.X.ToString("0.0") + "," + intent.Y.ToString("0.0") + "," + intent.Z.ToString("0.0") + ")" +
                      " netId=" + intent.NetId + " actorResolved=" + (actor != null));
            if (actor == null) { Debug.LogError("[Multipleer][tac] move intent: no actor for netId " + intent.NetId); return; }

            try
            {
                object moveAbility = ResolveMoveAbility(actor);
                if (moveAbility == null) { Debug.LogError("[Multipleer][tac] move intent: actor has no MoveAbility"); return; }
                object target = BuildMoveTarget(actor, new Vector3(intent.X, intent.Y, intent.Z));
                if (target == null) { Debug.LogError("[Multipleer][tac] move intent: could not build TacticalAbilityTarget"); return; }
                // [DIAG] HOST RE-EXEC: read the destination back off the freshly-built target so we can see
                // whether BuildMoveTarget preserved the requested pos (ctor vs field-set path), then invoke.
                TryGetPositionToApply(target, out Vector3 builtPos);
                string ctorPath = BuildMoveTargetUsedCtor(actor) ? "ctor" : "fieldset";
                // public override void Activate(object parameter) — runs the real move (animates + resolves
                // path). The host is NOT mirroring, so ClientInterceptMove passes through; OnPlayingActionEnd
                // postfix then broadcasts the final pose.
                var activate = AccessTools.Method(moveAbility.GetType(), "Activate", new[] { typeof(object) });
                if (activate == null) { Debug.LogError("[Multipleer][tac] move intent: MoveAbility.Activate(object) not found"); return; }
                string activateExc = "none";
                bool activateInvoked = false;
                try { activate.Invoke(moveAbility, new[] { target }); activateInvoked = true; }
                catch (Exception aex) { activateExc = aex.Message; throw; }
                finally
                {
                    Debug.Log("[Multipleer][tac][DIAG] HOSTEXEC builtTarget.PositionToApply=(" +
                              builtPos.x.ToString("0.0") + "," + builtPos.y.ToString("0.0") + "," + builtPos.z.ToString("0.0") + ")" +
                              " ctorPath=" + ctorPath + " activateInvoked=" + activateInvoked + " exception=" + activateExc);
                }
                Debug.Log("[Multipleer][tac] HOST executed move for netId " + intent.NetId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HostOnMoveIntent exec failed: " + ex); }
        }

        // ─── HOST: the move finished → broadcast the FINAL landed pose ────────────────────────────
        /// <summary>HOST postfix on <c>MoveAbility.OnPlayingActionEnd</c>: read the actor's final
        /// <c>Pos</c> + <c>StopReason</c> and broadcast <c>tac.move</c> to all peers. No-op off-host /
        /// off-session / when mirroring.</summary>
        public static void HostBroadcastMoveOutcome(object moveAbility)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;   // defensive: host is never mirroring

            try
            {
                object actor = GetProp(moveAbility, "TacticalActorBase");
                if (actor == null) return;
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0) return;

                Vector3 pos = GetPos(actor);
                int stopReason = ReadStopReason(actor);

                // [DIAG] HOST OUTCOME CAPTURE: the actor's final landed Pos + StopReason at the moment the
                // host commits to broadcast tac.move (broadcast=true since we reached this point).
                Debug.Log("[Multipleer][tac][DIAG] HOSTOUTCOME finalPos=(" +
                          pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") + ")" +
                          " stop=" + stopReason + " broadcast=true");

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacMove);
                byte[] payload = TacticalLiveCodec.EncodeMove(seq, netId, pos.x, pos.y, pos.z, stopReason);
                BroadcastToAll(engine, TacticalSurfaceIds.TacMove, payload);
                Debug.Log("[Multipleer][tac] HOST broadcast tac.move seq=" + seq + " netId=" + netId +
                          " pos=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") +
                          ") stop=" + stopReason);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] HostBroadcastMoveOutcome failed: " + ex); }
        }

        // ─── CLIENT: apply the host move outcome (mirror) ─────────────────────────────────────────
        /// <summary>CLIENT inbound: drive the actor to the host's FINAL pos. Animated via
        /// <c>TacticalNav.Navigate(pos)</c>; falls back to the teleport <c>SetPosition(pos)</c>. Idempotent
        /// via the per-surface seq guard (last-writer-wins).</summary>
        public static void ClientOnMove(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeMove(payload, out var m)) { Debug.LogError("[Multipleer][tac] tac.move decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacMove, m.Seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(m.NetId);
            if (actor == null) { Debug.LogError("[Multipleer][tac] tac.move: no actor for netId " + m.NetId); return; }

            var dst = new Vector3(m.X, m.Y, m.Z);
            // Navigate(pos) is best-effort ANIMATION only — it can silently no-op on an invalid/blocked
            // client path. The host's streamed landed pos is AUTHORITATIVE, so we ALWAYS finish with
            // SetPosition(dst) to guarantee the client actor ends at exactly the host's pos (no divergence).
            bool navOk = TryAnimatedNavigate(actor, dst);     // optional animation; result captured for DIAG only
            bool placed = TrySetPosition(actor, dst);         // authoritative final placement
            // [DIAG] CLIENT APPLY: requested dst, nav + setpos outcomes, and the actor's pos after placement.
            Vector3 posAfter = GetPos(actor);
            Debug.Log("[Multipleer][tac][DIAG] CLIENTAPPLY dst=(" +
                      m.X.ToString("0.0") + "," + m.Y.ToString("0.0") + "," + m.Z.ToString("0.0") + ")" +
                      " netId=" + m.NetId + " navOk=" + navOk + " setPosOk=" + placed +
                      " actorPosAfter=(" + posAfter.x.ToString("0.0") + "," + posAfter.y.ToString("0.0") + "," + posAfter.z.ToString("0.0") + ")");
            if (placed)
            {
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMove, m.Seq);
                Debug.Log("[Multipleer][tac] CLIENT applied tac.move seq=" + m.Seq + " netId=" + m.NetId);
            }
            else Debug.LogError("[Multipleer][tac] tac.move: SetPosition unavailable for actor " + m.NetId + " — position may diverge");
        }

        // ─── Engine reflection helpers ────────────────────────────────────────────────────────────

        private static object ResolveMoveAbility(object actor)
        {
            // TacticalActorBase.GetAbilities<MoveAbility>().FirstOrDefault(). GetAbilities<T> is generic.
            var moveAbilityType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.MoveAbility");
            if (moveAbilityType == null) return null;
            var getAbilities = AccessTools.Method(actor.GetType(), "GetAbilities");
            if (getAbilities == null || !getAbilities.IsGenericMethodDefinition) return null;
            var gen = getAbilities.MakeGenericMethod(moveAbilityType);
            var result = gen.Invoke(actor, null) as IEnumerable;
            if (result == null) return null;
            foreach (var a in result) if (a != null) return a;   // first MoveAbility
            return null;
        }

        private static object BuildMoveTarget(object actor, Vector3 pos)
        {
            // new TacticalAbilityTarget(TacticalActorBase actor, Vector3 positionToApply, AttackType=Regular)
            var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            if (targetType == null) return null;
            // Prefer the (actor, pos) ctor; fall back to setting PositionToApply on the default ctor.
            var actorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
            var ctor = actorBaseType != null
                ? targetType.GetConstructor(new[] { actorBaseType, typeof(Vector3), AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType") })
                : null;
            if (ctor != null)
            {
                object attackRegular = DefaultAttackType();
                return ctor.Invoke(new[] { actor, (object)pos, attackRegular });
            }
            // Fallback: default ctor + field assignment.
            object t = Activator.CreateInstance(targetType);
            var posField = AccessTools.Field(targetType, "PositionToApply");
            var actorField = AccessTools.Field(targetType, "Actor");
            posField?.SetValue(t, pos);
            actorField?.SetValue(t, actor);
            return t;
        }

        // [DIAG] read-only mirror of BuildMoveTarget's ctor-availability check (constructs nothing) so the
        // HOSTEXEC log can report which path BuildMoveTarget took. No behavior impact.
        private static bool BuildMoveTargetUsedCtor(object actor)
        {
            try
            {
                var targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
                if (targetType == null) return false;
                var actorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                var ctor = actorBaseType != null
                    ? targetType.GetConstructor(new[] { actorBaseType, typeof(Vector3), AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType") })
                    : null;
                return ctor != null;
            }
            catch { return false; }
        }

        private static object DefaultAttackType()
        {
            var at = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.AttackType");
            if (at == null) return null;
            try { return Enum.Parse(at, "Regular"); } catch { return Activator.CreateInstance(at); }
        }

        private static bool TryGetPositionToApply(object parameter, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (parameter == null) return false;
            var f = AccessTools.Field(parameter.GetType(), "PositionToApply");
            object raw = f != null ? f.GetValue(parameter) : GetProp(parameter, "PositionToApply");
            if (raw is Vector3 v)
            {
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;  // InvalidPosition
                pos = v;
                return true;
            }
            return false;
        }

        private static bool TryAnimatedNavigate(object actor, Vector3 dst)
        {
            try
            {
                object nav = GetProp(actor, "TacticalNav");
                if (nav == null) return false;
                // public override void Navigate(Vector3 dst) — 1-arg animated overload.
                var navigate = AccessTools.Method(nav.GetType(), "Navigate", new[] { typeof(Vector3) });
                if (navigate == null) return false;
                navigate.Invoke(nav, new object[] { dst });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TryAnimatedNavigate failed: " + ex); return false; }
        }

        private static bool TrySetPosition(object actor, Vector3 dst)
        {
            try
            {
                // ActorComponent.SetPosition(Vector3) — public teleport (no coroutine).
                var setPos = AccessTools.Method(actor.GetType(), "SetPosition", new[] { typeof(Vector3) });
                if (setPos == null) return false;
                setPos.Invoke(actor, new object[] { dst });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TrySetPosition failed: " + ex); return false; }
        }

        private static int ReadStopReason(object actor)
        {
            try
            {
                object nav = GetProp(actor, "TacticalNav");
                object sr = nav != null ? GetProp(nav, "StopReason") : null;
                return sr != null ? Convert.ToInt32(sr) : 0;
            }
            catch { return 0; }
        }

        private static Vector3 GetPos(object actor)
        {
            object p = GetProp(actor, "Pos");
            return p is Vector3 v ? v : Vector3.zero;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        // ─── Wire send helpers (ride the 0x67 envelope rail + tactical fast-path) ──────────────────
        internal static void SendToHost(NetworkEngine engine, ushort surfaceId, byte[] payload)
        {
            byte[] envelope = Network.Sync.SyncProtocol.EncodeEnvelope(
                (byte)surfaceId, Network.Sync.SyncKind.StateSnapshot, payload);
            engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, envelope));
        }

        internal static void BroadcastToAll(NetworkEngine engine, ushort surfaceId, byte[] payload)
        {
            byte[] envelope = Network.Sync.SyncProtocol.EncodeEnvelope(
                (byte)surfaceId, Network.Sync.SyncKind.StateSnapshot, payload);
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, envelope));
        }
    }
}
