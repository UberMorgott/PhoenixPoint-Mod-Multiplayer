using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Harmony.Tactical;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
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

        // Animate the mirror only when it is more than ~one grid cell from the host's final pos; closer than
        // this and the path would be degenerate (0/1 node) → animate would OOR in ExecutePoints, so teleport.
        // PP tactical grid cell ≈ 1 world unit (no public cell-size const surfaced); 1.0f is the safe gate.
        private const float MoveAnimateMinDist = 1.0f;

        // Fail-safe budget for the deferred move reconcile (~600 frames ≈ 10 s @ 60 fps).
        private const int ReconcileMaxFrames = 600;

        // Reusable mirror navigation settings: NO authoritative side effects (no AP spend, no overwatch, no
        // perception-range update), snap to the grid on finish. Built lazily off the native NavigationSettings
        // type so the field names stay bound to the real engine type.
        private static object _mirrorNavSettings;

        // CLIENT: the requested dst the START broadcast set the mirror navigating toward, keyed by netId. The
        // END outcome (ClientOnMove) reads it to tell a NORMAL completion (finalPos ≈ startDst → wait-then-snap)
        // from an EARLY INTERRUPT (finalPos far from startDst → cancel-nav + immediate snap). Cleared on apply.
        private static readonly Dictionary<int, Vector3> _clientStartDst = new Dictionary<int, Vector3>();

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
                Debug.Log("[Multiplayer][tac][DIAG] CAPTURE role=" + (diagIsHost ? "HOST" : "CLIENT") +
                          " mirrorArmed=" + TacticalDeploySync.IsClientMirroring + " netId=<n/a>" +
                          " posToApply=(" + diagPos.x.ToString("0.0") + "," + diagPos.y.ToString("0.0") + "," + diagPos.z.ToString("0.0") + ")" +
                          " action=PASS");
                // HOST START broadcast: this prefix is the SINGLE choke point for the host beginning a move —
                // both the host's own click AND HostOnMoveIntent's programmatic Activate.Invoke run THROUGH the
                // patched MoveAbility.Activate, so they both trip this prefix exactly once. Broadcast the COMMAND
                // (dst) now, BEFORE the native move runs, so every client mirror animates CONCURRENTLY with the
                // host instead of waiting for the END outcome. Fail-open: any failure here never blocks the move.
                HostBroadcastMoveStart(moveAbility, parameter);
                return true;   // host / single-player / non-mirror
            }
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost)
            {
                Debug.Log("[Multiplayer][tac][DIAG] CAPTURE role=" + (diagIsHost ? "HOST" : "CLIENT") +
                          " mirrorArmed=" + TacticalDeploySync.IsClientMirroring + " netId=<n/a> posToApply=(n/a) action=PASS");
                return true;
            }

            try
            {
                object actor = GetProp(moveAbility, "TacticalActorBase");
                if (actor == null)
                {
                    Debug.Log("[Multiplayer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                              " netId=<no-actor> posToApply=(n/a) action=PASS");
                    return true;
                }
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0)
                {
                    Debug.Log("[Multiplayer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                              " netId=" + netId + " posToApply=(n/a) action=SUPPRESS");
                    Debug.LogError("[Multiplayer][tac] move intent: unknown netId for actor"); return false;
                }

                if (!TryGetPositionToApply(parameter, out Vector3 pos))
                {
                    Debug.Log("[Multiplayer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                              " netId=" + netId + " posToApply=(no-pos) action=SUPPRESS");
                    Debug.LogError("[Multiplayer][tac] move intent: no PositionToApply on target — suppressing local move");
                    return false;   // still suppress: a client must never run a local move
                }

                Debug.Log("[Multiplayer][tac][DIAG] CAPTURE role=CLIENT mirrorArmed=" + TacticalDeploySync.IsClientMirroring +
                          " netId=" + netId +
                          " posToApply=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") + ")" +
                          " action=SUPPRESS");
                byte[] payload = TacticalLiveCodec.EncodeMoveIntent(netId, pos.x, pos.y, pos.z, NextNonce());
                SendToHost(engine, TacticalSurfaceIds.TacIntentMove, payload);
                Debug.Log("[Multiplayer][tac] CLIENT sent tac.intent.move netId=" + netId +
                          " pos=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") + ")");
                return false;   // suppress local move
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ClientInterceptMove failed: " + ex);
                return false;   // a mirroring client must not run a local move even on error
            }
        }

        // ─── HOST: a client move intent arrived → run the real move ───────────────────────────────
        /// <summary>HOST inbound: resolve netId→actor and execute the real move toward the requested pos on
        /// the host sim. Running the native move fires <see cref="HostBroadcastMoveOutcome"/> via the
        /// OnPlayingActionEnd postfix, which streams the FINAL pose to all peers.</summary>
        public static void HostOnMoveIntent(ulong senderPeerId, byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeMoveIntent(payload, out var intent)) { Debug.LogError("[Multiplayer][tac] move intent decode failed"); return; }
            // Drop a reliable-transport double-send (a double-applied move would step the actor twice).
            if (!TacticalDeploySync.IntentDedup.IsNew(senderPeerId, TacticalSurfaceIds.TacIntentMove, intent.Nonce)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(intent.NetId);
            // [DIAG] HOST DECODE: decoded destination, whether the netId resolved, and — crucially — the resolved
            // actor's NAME + CURRENT (start) position. A "wrong start position" divergence shows here as an
            // actorStartPos far from the requested pos (the host's netId-bound actor standing across the map).
            Vector3 startPos = actor != null ? GetPos(actor) : Vector3.zero;
            Debug.Log("[Multiplayer][tac][DIAG] HOSTINTENT decoded pos=(" + F(intent.X) + "," + F(intent.Y) + "," + F(intent.Z) + ")" +
                      " netId=" + intent.NetId + " actorResolved=" + (actor != null) +
                      " actor=" + ActorName(actor) + " actorStartPos=(" + F(startPos.x) + "," + F(startPos.y) + "," + F(startPos.z) + ")");
            if (actor == null)
            {
                Debug.LogError("[Multiplayer][tac] move intent: no actor for netId " + intent.NetId + " " + TacticalDeploySync.DescribeLiveActorIds());
                return;
            }

            try
            {
                object moveAbility = ResolveMoveAbility(actor);
                if (moveAbility == null) { Debug.LogError("[Multiplayer][tac] move intent: actor has no MoveAbility"); return; }
                object target = BuildMoveTarget(actor, new Vector3(intent.X, intent.Y, intent.Z));
                if (target == null) { Debug.LogError("[Multiplayer][tac] move intent: could not build TacticalAbilityTarget"); return; }
                // [DIAG] HOST RE-EXEC: read the destination back off the freshly-built target so we can see
                // whether BuildMoveTarget preserved the requested pos (ctor vs field-set path), then invoke.
                TryGetPositionToApply(target, out Vector3 builtPos);
                string ctorPath = BuildMoveTargetUsedCtor(actor) ? "ctor" : "fieldset";
                // public override void Activate(object parameter) — runs the real move (animates + resolves
                // path). The host is NOT mirroring, so ClientInterceptMove passes through; OnPlayingActionEnd
                // postfix then broadcasts the final pose.
                var activate = AccessTools.Method(moveAbility.GetType(), "Activate", new[] { typeof(object) });
                if (activate == null) { Debug.LogError("[Multiplayer][tac] move intent: MoveAbility.Activate(object) not found"); return; }
                string activateExc = "none";
                bool activateInvoked = false;
                // BUG2: hold the host camera-follow guard across the relayed client MOVE's activate.Invoke so the
                // synchronous Activate camera hint can't fly the host camera to the client's soldier. try/finally so
                // the depth still pops if Activate throws.
                FireReplayGate.EnterHostApply();
                try { activate.Invoke(moveAbility, new[] { target }); activateInvoked = true; }
                catch (Exception aex) { activateExc = aex.Message; throw; }
                finally
                {
                    FireReplayGate.ExitHostApply();
                    Debug.Log("[Multiplayer][tac][DIAG] HOSTEXEC builtTarget.PositionToApply=(" +
                              F(builtPos.x) + "," + F(builtPos.y) + "," + F(builtPos.z) + ")" +
                              " ctorPath=" + ctorPath + " activateInvoked=" + activateInvoked + " exception=" + activateExc);
                }
                Debug.Log("[Multiplayer][tac] HOST executed move for netId " + intent.NetId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnMoveIntent exec failed: " + ex); }
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

                // [DIAG] HOST OUTCOME CAPTURE: the actor's NAME + final landed Pos + StopReason at the moment the
                // host commits to broadcast tac.move (broadcast=true since we reached this point).
                Debug.Log("[Multiplayer][tac][DIAG] HOSTOUTCOME netId=" + netId + " actor=" + ActorName(actor) +
                          " finalPos=(" + F(pos.x) + "," + F(pos.y) + "," + F(pos.z) + ")" +
                          " stop=" + stopReason + " broadcast=true");

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacMove);
                byte[] payload = TacticalLiveCodec.EncodeMove(seq, netId, pos.x, pos.y, pos.z, stopReason);
                BroadcastToAll(engine, TacticalSurfaceIds.TacMove, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.move seq=" + seq + " netId=" + netId +
                          " pos=(" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0") + "," + pos.z.ToString("0.0") +
                          ") stop=" + stopReason);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastMoveOutcome failed: " + ex); }
        }

        // ─── HOST: a move is STARTING → broadcast the COMMAND (dst) so clients animate concurrently ──
        /// <summary>HOST: at the moment the host BEGINS a move (own click OR a relayed client intent, both via
        /// the patched <c>MoveAbility.Activate</c>), read {netId, requested dst (PositionToApply)} and broadcast
        /// <c>tac.move.start</c> to all peers. Clients run the ANIMATED navigate immediately so the mirror moves
        /// CONCURRENTLY with the host. The END outcome (<see cref="HostBroadcastMoveOutcome"/>) still reconciles
        /// the EXACT final cell, so this is never worse than today. Fail-open: any failure is logged + swallowed
        /// so the native host move always proceeds.</summary>
        public static void HostBroadcastMoveStart(object moveAbility, object parameter)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                object actor = GetProp(moveAbility, "TacticalActorBase");
                if (actor == null) return;
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                if (netId < 0) return;
                if (!TryGetPositionToApply(parameter, out Vector3 dst)) return;

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacMoveStart);
                byte[] payload = TacticalLiveCodec.EncodeMoveStart(seq, netId, dst.x, dst.y, dst.z);
                BroadcastToAll(engine, TacticalSurfaceIds.TacMoveStart, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.move.start seq=" + seq + " netId=" + netId +
                          " dst=(" + dst.x.ToString("0.0") + "," + dst.y.ToString("0.0") + "," + dst.z.ToString("0.0") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastMoveStart failed: " + ex); }
        }

        // ─── CLIENT: a move is STARTING → animate the mirror CONCURRENTLY ──────────────────────────
        /// <summary>CLIENT inbound (<c>tac.move.start</c>): resolve the actor and run the ANIMATED navigate
        /// toward the requested dst so the mirror moves at the SAME time as the host (concurrency fix). NO
        /// authoritative side effects (mirror nav settings: no AP spend, no overwatch). The END outcome
        /// (<c>tac.move</c>) then reconciles to the EXACT final cell. Degenerate sub-cell move → teleport.
        /// Own monotonic seq (last-writer-wins) independent of tac.move.</summary>
        public static void ClientOnMoveStart(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeMoveStart(payload, out var s)) { Debug.LogError("[Multiplayer][tac] tac.move.start decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacMoveStart, s.Seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(s.NetId);
            if (actor == null) { Debug.LogError("[Multiplayer][tac] tac.move.start: no actor for netId " + s.NetId + " " + TacticalDeploySync.DescribeLiveActorIds()); return; }

            var dst = new Vector3(s.X, s.Y, s.Z);
            // Record the requested dst so the END outcome can distinguish a normal completion from an interrupt.
            _clientStartDst[s.NetId] = dst;
            // Enemy-turn chase cam: follow the moving actor's live transform across the navigate (follow=true).
            if (ClientEnemyTurnCameraGate.ShouldChaseEnemyAction(TacticalTurnSync.IsClientEnemyTurn, actor != null))
                TacticalEnemyTurnCamera.ChaseActor(actor, follow: true);
            string branch;
            Vector3 cur = GetPos(actor);
            object nav = GetProp(actor, "TacticalNav");
            if (nav != null && Vector3.Distance(cur, dst) > MoveAnimateMinDist)
            {
                bool started = TryAnimatedNavigate(nav, dst);
                if (started)
                {
                    branch = "animated-start";
                    TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMoveStart, s.Seq);
                }
                else
                {
                    // Navigate threw / unavailable → leave the actor for the END outcome to reconcile (still
                    // correct cell, just no concurrent animation). Do NOT teleport here: a teleport on START
                    // would make the actor pop to the REQUESTED cell, which can differ from the final cell on an
                    // early interrupt — let the authoritative END outcome place it.
                    branch = "start-navfail";
                    TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMoveStart, s.Seq);
                }
            }
            else
            {
                // Degenerate / sub-cell move → no concurrent animation needed; END outcome reconciles it.
                branch = "start-degenerate";
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMoveStart, s.Seq);
            }
            Debug.Log("[Multiplayer][tac][DIAG] CLIENTSTART dst=(" +
                      s.X.ToString("0.0") + "," + s.Y.ToString("0.0") + "," + s.Z.ToString("0.0") + ")" +
                      " branch=" + branch + " netId=" + s.NetId);
            Debug.Log("[Multiplayer][tac] CLIENT applied tac.move.start seq=" + s.Seq + " netId=" + s.NetId + " branch=" + branch);
        }

        // ─── CLIENT: apply the host move OUTCOME → RECONCILE only (concurrency redesign) ────────────
        /// <summary>CLIENT inbound (<c>tac.move</c>, the END outcome): the concurrent animation was ALREADY
        /// started by <see cref="ClientOnMoveStart"/>, so this handler MUST NOT initiate a fresh Navigate (that
        /// would double-animate). It RECONCILES the mirror to the host's authoritative FINAL cell:
        ///   • NORMAL completion (finalPos ≈ the START dst the mirror is heading toward) → wait until the nav
        ///     stops, then <c>SetPosition(finalPos)</c> (the existing deferred reconcile; exact cell).
        ///   • EARLY INTERRUPT (finalPos far from the START dst — e.g. overwatch stopped the host short) →
        ///     <c>CancelNavigation()</c> + immediate <c>SetPosition(finalPos)</c> so the mirror snaps back to
        ///     where the host actually landed instead of overshooting to the requested cell.
        ///   • No START recorded (start lost / no-op) → behaves as NORMAL (wait-then-snap if navigating, else
        ///     immediate snap), so the END outcome remains the correctness backstop.
        /// Never worse than today: the final cell is always the exact host cell. Idempotent via the per-surface
        /// seq guard (last-writer-wins).</summary>
        public static void ClientOnMove(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeMove(payload, out var m)) { Debug.LogError("[Multiplayer][tac] tac.move decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacMove, m.Seq)) return;

            object actor = TacticalDeploySync.ResolveLiveActor(m.NetId);
            if (actor == null) { Debug.LogError("[Multiplayer][tac] tac.move: no actor for netId " + m.NetId + " " + TacticalDeploySync.DescribeLiveActorIds()); return; }

            var finalPos = new Vector3(m.X, m.Y, m.Z);
            object nav = GetProp(actor, "TacticalNav");
            bool navigating = nav != null && ToBool(GetProp(nav, "IsNavigating"));

            // Was finalPos significantly different from where the START set the mirror heading? → early interrupt.
            bool hadStart = _clientStartDst.TryGetValue(m.NetId, out Vector3 startDst);
            bool interrupt = hadStart && Vector3.Distance(startDst, finalPos) > MoveAnimateMinDist;
            _clientStartDst.Remove(m.NetId);

            string branch;
            if (interrupt)
            {
                // Host stopped short of the requested cell → cancel the (now-wrong) concurrent nav, snap to the
                // authoritative final cell immediately. CancelNavigation() is the public override (no AP/overwatch
                // side effects on the mirror; it only resolves StopReason + halts the path).
                if (navigating) TryCancelNavigation(nav);
                bool placed = TrySetPosition(actor, finalPos);
                if (placed) TryRederiveCoverPose(actor, finalPos);   // bug A: re-derive cover/stance after the snap
                branch = placed ? "reconcile-interrupt" : "reconcile-interrupt-failed";
                if (placed) TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMove, m.Seq);
                else Debug.LogError("[Multiplayer][tac] tac.move: SetPosition unavailable for actor " + m.NetId + " — position may diverge");
            }
            else if (navigating)
            {
                // Normal move still animating from START → defer the snap until the nav stops, landing on the
                // EXACT host cell (the existing wait-IsNavigating-then-SetPosition reconcile coroutine).
                branch = "reconcile";
                ScheduleReconcile(actor, nav, finalPos, m.NetId);
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMove, m.Seq);
            }
            else
            {
                // Not navigating (START no-op/degenerate, or already arrived) → snap immediately (exact cell).
                bool placed = TrySetPosition(actor, finalPos);
                if (placed) TryRederiveCoverPose(actor, finalPos);   // bug A: re-derive cover/stance after the snap
                branch = placed ? "reconcile-snap" : "reconcile-snap-failed";
                if (placed) TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMove, m.Seq);
                else Debug.LogError("[Multiplayer][tac] tac.move: SetPosition unavailable for actor " + m.NetId + " — position may diverge");
            }
            // [DIAG] CLIENT APPLY: final cell, chosen reconcile branch, and the actor's pos right after dispatch.
            Vector3 posAfter = GetPos(actor);
            Debug.Log("[Multiplayer][tac][DIAG] CLIENTAPPLY finalPos=(" +
                      m.X.ToString("0.0") + "," + m.Y.ToString("0.0") + "," + m.Z.ToString("0.0") + ")" +
                      " branch=" + branch + " netId=" + m.NetId + " navigating=" + navigating + " interrupt=" + interrupt +
                      " actorPosAfter=(" + posAfter.x.ToString("0.0") + "," + posAfter.y.ToString("0.0") + "," + posAfter.z.ToString("0.0") + ")");
            Debug.Log("[Multiplayer][tac] CLIENT applied tac.move seq=" + m.Seq + " netId=" + m.NetId + " branch=" + branch);

            // NOTE: the client view-freeze is now PREVENTED up-front by SuppressedAbilityViewClearPatch (a CLIENT
            // prefix on TacticalViewState.ActivateAbility that skips the wedging ClearStackAndPush for suppressed
            // non-shoot abilities), so no after-the-fact control-view recovery is needed here.
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
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TrySetPosition failed: " + ex); return false; }
        }

        /// <summary>Cancel an in-flight mirror navigation: <c>TacticalNavigationComponent.CancelNavigation()</c>
        /// (public override, no params — TacticalNavigationComponent.cs:1357; sets StopReason then halts the
        /// path). Used to abort a concurrent START animation when the host's END outcome reveals an early
        /// interrupt (final cell ≠ requested cell). Returns false on a throw / missing method.</summary>
        private static bool TryCancelNavigation(object nav)
        {
            try
            {
                if (nav == null) return false;
                var cancel = AccessTools.Method(nav.GetType(), "CancelNavigation", Type.EmptyTypes);
                if (cancel == null) { Debug.LogError("[Multiplayer][tac] CancelNavigation() not found"); return false; }
                cancel.Invoke(nav, null);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TryCancelNavigation failed: " + ex); return false; }
        }

        // ─── Inc1 full-state: drive the mirror from an ABSOLUTE position delta (tac.actorstate 0x8F) ──

        /// <summary>CLIENT (mirror): present an actor's host-authoritative ABSOLUTE position carried by the
        /// generic per-actor delta (<c>tac.actorstate</c> 0x8F). This is the Inc1 "walk-from-state" bridge: a
        /// position change becomes a NATIVE walk animation (<see cref="TryAnimatedNavigate"/>) or an instant snap
        /// (<see cref="TrySetPosition"/>) per the PURE <see cref="TacticalActorStateDiff.DecidePositionApply"/>
        /// decision — never a custom interpolator (uses the game's own animator, avoiding the geoscape render-drift
        /// failure mode). Reuses the SAME inert mirror <c>NavigationSettings</c> as the move rail (no AP spend, no
        /// overwatch). Returns true when it actually moved/snapped the actor.
        ///
        /// ADDITIVE-SAFE: runs ALONGSIDE the proven tac.move / tac.move.start rails. If the move rail already set
        /// this actor NAVIGATING (a concurrent <c>tac.move.start</c> Navigate is in flight), this SKIPS — the move
        /// rail owns that animation and its END outcome reconciles the exact cell, so the delta must not start a
        /// second Navigate (double-animate) nor snap mid-walk. Once the move rail's nav stops, the next delta
        /// heartbeat converges any residual drift. Re-entrancy-safe: only ever called inside the client's
        /// remote-apply scope. No-op off-mirror / off-session.</summary>
        public static bool ApplyMirrorPosition(object actor, Vector3 dst)
        {
            try
            {
                if (actor == null) return false;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || engine.IsHost) return false;   // client-only
                if (!TacticalDeploySync.IsClientMirroring) return false;

                object nav = GetProp(actor, "TacticalNav");
                // Move rail owns an in-flight walk → let it finish (no double-animate / no mid-walk snap).
                if (nav != null && ToBool(GetProp(nav, "IsNavigating"))) return false;

                Vector3 cur = GetPos(actor);
                float dist = Vector3.Distance(cur, dst);
                var mode = TacticalActorStateDiff.DecidePositionApply(dist);
                switch (mode)
                {
                    case TacticalActorStateDiff.PositionApplyMode.None:
                        return false;   // already converged → no churn
                    case TacticalActorStateDiff.PositionApplyMode.Walk:
                        if (nav != null && TryAnimatedNavigate(nav, dst))
                        {
                            // bug A: DEFER the cover/stance re-derive to walk ARRIVAL — the host sets the pose only
                            // AFTER the move finishes (MoveAbility.Move → WaitUntilFinished → SetIdleParams,
                            // MoveAbility.cs:121). Setting it at kickoff would feed the animator cover params mid-walk
                            // (wrong pose blend). Reuses the same Timing.Start launcher as the move rail's reconcile.
                            if (!TryStartOnActorTiming(actor, RederiveCoverPoseAfterNavCrt(actor, nav, dst)))
                                Debug.LogError("[Multiplayer][tac] could not defer cover-pose re-derive — skipped (no mid-walk pose set)");
                            return true;
                        }
                        // Navigate unavailable / threw → snap so the position still converges (correct cell).
                        if (TrySetPosition(actor, dst)) { TryRederiveCoverPose(actor, dst); return true; }                      // bug A: re-derive after snap (instant placement = at arrival)
                        return false;
                    case TacticalActorStateDiff.PositionApplyMode.Teleport:
                    default:
                        if (TrySetPosition(actor, dst)) { TryRederiveCoverPose(actor, dst); return true; }                      // bug A: re-derive after snap
                        return false;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ApplyMirrorPosition failed: " + ex); return false; }
        }

        // ─── Inc2: drive the mirror from an ABSOLUTE facing delta (tac.actorstate 0x8F) ──────────────

        /// <summary>CLIENT (mirror): apply the host-authoritative ABSOLUTE facing carried by the tac.actorstate
        /// delta. Sets the actor's forward via <c>ActorComponent.SetForward(Vector3)</c> (ActorComponent.cs:280 →
        /// SetRotation → SetTransform :299). ABSOLUTE/idempotent. SKIPS while the mirror is navigating (a walk owns
        /// rotation — the move rail or <see cref="ApplyMirrorPosition"/> Navigate turns the actor along the path;
        /// the next heartbeat converges to the host's final facing). SKIPS a zero vector (SetForward(zero) → invalid
        /// LookRotation) and a sub-epsilon change (no churn / no ActorMovedEvent re-fire — vision recompute is
        /// already client-suppressed). No-op off-mirror. Returns true only when it actually re-faced the actor.</summary>
        public static bool ApplyMirrorFacing(object actor, Vector3 forward)
        {
            try
            {
                if (actor == null) return false;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || engine.IsHost) return false;   // client-only
                if (!TacticalDeploySync.IsClientMirroring) return false;
                if (forward == Vector3.zero) return false;                                // SetForward(zero) → invalid LookRotation
                object nav = GetProp(actor, "TacticalNav");
                if (nav != null && ToBool(GetProp(nav, "IsNavigating"))) return false;    // walk owns rotation
                Vector3 cur = GetForward(actor);
                if (!TacticalActorStateDiff.FacingChanged(cur.x, cur.y, cur.z, forward.x, forward.y, forward.z))
                    return false;   // already converged → no churn
                return TrySetForward(actor, forward);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ApplyMirrorFacing failed: " + ex); return false; }
        }

        /// <summary>Read the actor's current world forward vector (<c>ActorComponent.Rot</c> * Vector3.forward).
        /// Defaults to Vector3.forward when the rotation is unreadable. Mirrors <see cref="GetPos"/>.</summary>
        private static Vector3 GetForward(object actor)
        {
            object r = GetProp(actor, "Rot");
            return r is Quaternion q ? q * Vector3.forward : Vector3.forward;
        }

        /// <summary>Set the actor's world forward via <c>ActorComponent.SetForward(Vector3)</c> (the single-Vector3
        /// public override, ActorComponent.cs:280). Returns false on a throw / missing overload. Mirrors
        /// <see cref="TrySetPosition"/>.</summary>
        private static bool TrySetForward(object actor, Vector3 forward)
        {
            try
            {
                var m = AccessTools.Method(actor.GetType(), "SetForward", new[] { typeof(Vector3) });
                if (m == null) { Debug.LogError("[Multiplayer][tac] SetForward(Vector3) not found"); return false; }
                m.Invoke(actor, new object[] { forward });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TrySetForward failed: " + ex); return false; }
        }

        // ─── CLIENT: re-derive idle cover/stance pose (bug A) ───────────────────────────────────────

        /// <summary>CLIENT (mirror): re-derive the idle cover/stance pose at <paramref name="finalPos"/> and push it
        /// into the actor's <c>IdleAbility</c>, replacing the engine step the client's move-suppression skips. On the
        /// host this runs inside the <c>MoveAbility.Move</c> coroutine (MoveAbility.cs:121):
        /// <c>TacticalActor.IdleAbility.SetIdleParams(TacticalActor.TacticalPerception.GetBestIdleCoverPoseAt(target.PositionToApply))</c>.
        /// A mirroring client never runs that coroutine (the move is suppressed + reconciled), so the animator
        /// "CoverType" int stays 0 and the soldier never crouches into cover. This calls the SAME engine methods by
        /// reflection with <c>searchForEnemy:false</c> — the client's vision is frozen, so we must NOT touch the enemy
        /// search (the CharacterTargetDummy precedent, CharacterTargetDummy.cs:381). Pose-only + idempotent → safe to
        /// call at every move-completion site. Returns false on a throw / missing member (e.g. an actor with no
        /// IdleAbility) so callers stay fail-open. Signatures: TacticalActor.IdleAbility (TacticalActor.cs:200),
        /// TacticalActor.TacticalPerception (TacticalActor.cs:147), TacticalPerception.GetBestIdleCoverPoseAt(
        /// Vector3,bool=true)→CoverPose (TacticalPerception.cs:347), IdleAbility.SetIdleParams(CoverPose)
        /// (IdleAbility.cs:101).</summary>
        private static bool TryRederiveCoverPose(object actor, Vector3 finalPos)
        {
            try
            {
                if (actor == null) return false;
                object idle = GetProp(actor, "IdleAbility");
                object perception = GetProp(actor, "TacticalPerception");
                if (idle == null || perception == null) return false;

                var getPose = AccessTools.Method(perception.GetType(), "GetBestIdleCoverPoseAt", new[] { typeof(Vector3), typeof(bool) });
                if (getPose == null) { Debug.LogError("[Multiplayer][tac] GetBestIdleCoverPoseAt(Vector3,bool) not found"); return false; }
                // searchForEnemy:false — the client's vision path is frozen; never trigger the enemy search here.
                object pose = getPose.Invoke(perception, new object[] { finalPos, false });
                if (pose == null) return false;

                var setParams = AccessTools.Method(idle.GetType(), "SetIdleParams", new[] { pose.GetType() });
                if (setParams == null) { Debug.LogError("[Multiplayer][tac] SetIdleParams(CoverPose) not found"); return false; }
                setParams.Invoke(idle, new[] { pose });
                Debug.Log("[Multiplayer][tac] CLIENT re-derived cover pose at (" +
                          finalPos.x.ToString("0.0") + "," + finalPos.y.ToString("0.0") + "," + finalPos.z.ToString("0.0") + ")");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TryRederiveCoverPose failed: " + ex); return false; }
        }

        // ─── CLIENT animated-mirror helpers (FIX B) ────────────────────────────────────────────────

        /// <summary>Lazily build the reusable mirror <c>NavigationSettings</c>: <c>CostsAPToActor=false</c>
        /// (skips the AP spend in ExecutePoints — TacticalNavigationComponent.cs:~1087), <c>TriggerOverwatch
        /// =false</c> (skips overwatch), <c>UpdateNavigationPerceptionRange=false</c>, <c>SnapToGridOnFinish
        /// =true</c>. Field names grounded against NavigationSettings.cs:10-40.</summary>
        private static object GetMirrorNavSettings()
        {
            if (_mirrorNavSettings != null) return _mirrorNavSettings;
            try
            {
                var nsType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.NavigationSettings");
                if (nsType == null) { Debug.LogError("[Multiplayer][tac] NavigationSettings type not found"); return null; }
                object s = Activator.CreateInstance(nsType);
                SetField(nsType, s, "CostsAPToActor", false);
                SetField(nsType, s, "TriggerOverwatch", false);
                SetField(nsType, s, "UpdateNavigationPerceptionRange", false);
                SetField(nsType, s, "SnapToGridOnFinish", true);
                _mirrorNavSettings = s;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] GetMirrorNavSettings failed: " + ex); }
            return _mirrorNavSettings;
        }

        private static void SetField(Type type, object obj, string name, object value)
        {
            var f = AccessTools.Field(type, name);
            if (f != null) f.SetValue(obj, value);
            else Debug.LogError("[Multiplayer][tac] NavigationSettings field not found: " + name);
        }

        /// <summary>Start the animated mirror move: <c>TacticalNavigationComponent.Navigate(Vector3,
        /// NavigationSettings)</c> (TacticalNavigationComponent.cs:1182). Returns false on a throw / missing
        /// overload so the caller can fall back to a teleport (removes the degenerate-path OOR class).</summary>
        private static bool TryAnimatedNavigate(object nav, Vector3 dst)
        {
            try
            {
                object settings = GetMirrorNavSettings();
                var nsType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.NavigationSettings");
                if (settings == null || nsType == null) return false;
                var navigate = AccessTools.Method(nav.GetType(), "Navigate", new[] { typeof(Vector3), nsType });
                if (navigate == null) { Debug.LogError("[Multiplayer][tac] Navigate(Vector3,NavigationSettings) not found"); return false; }
                navigate.Invoke(nav, new[] { (object)dst, settings });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TryAnimatedNavigate failed: " + ex); return false; }
        }

        /// <summary>Schedule a deferred reconcile: wait until the actor's nav <c>IsNavigating==false</c>, then
        /// <c>SetPosition(dst)</c> so the mirror lands on the EXACT host cell (never worse than a teleport).
        /// Driven on the actor's Timing via the same Timing.Start pattern used elsewhere.</summary>
        private static void ScheduleReconcile(object actor, object nav, Vector3 dst, int netId)
        {
            try
            {
                if (!TryStartOnActorTiming(actor, ReconcileMoveCrt(actor, nav, dst, netId)))
                {
                    // Could not defer → reconcile immediately (still correct cell, just not animated-to-stop).
                    TrySetPosition(actor, dst);
                    Debug.LogError("[Multiplayer][tac] tac.move: reconcile could not be deferred — snapped immediately netId=" + netId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ScheduleReconcile failed: " + ex);
                TrySetPosition(actor, dst);
            }
        }

        /// <summary>Resolve the actor's (or live TLC's) <c>Timing</c> and <c>Timing.Start</c> the given coroutine —
        /// the shared launcher for the deferred mirror coroutines (<see cref="ReconcileMoveCrt"/>,
        /// <see cref="RederiveCoverPoseAfterNavCrt"/>). Returns false when no Timing is available or Start could not
        /// be invoked, so the caller can fall back.</summary>
        private static bool TryStartOnActorTiming(object actor, IEnumerator<NextUpdate> crt)
        {
            object timing = GetProp(actor, "Timing");
            if (timing == null)
            {
                object tlc = TacticalDeploySync.LiveTlc;
                timing = tlc != null ? GetProp(tlc, "Timing") : null;
            }
            return timing != null && InvokeStart(timing, timing.GetType(), crt);
        }

        private static IEnumerator<NextUpdate> ReconcileMoveCrt(object actor, object nav, Vector3 dst, int netId)
        {
            int frames = 0;
            while (frames < ReconcileMaxFrames)
            {
                bool navigating = false;
                try { navigating = ToBool(GetProp(nav, "IsNavigating")); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReconcileMoveCrt: IsNavigating read failed: " + ex); }
                if (!navigating) break;
                frames++;
                yield return NextUpdate.NextFrame;
            }
            try
            {
                TrySetPosition(actor, dst);
                TryRederiveCoverPose(actor, dst);   // bug A: re-derive cover/stance the client move-suppression skipped
                Vector3 after = GetPos(actor);
                Debug.Log("[Multiplayer][tac] tac.move RECONCILE netId=" + netId + " frames=" + frames +
                          " finalPos=(" + after.x.ToString("0.0") + "," + after.y.ToString("0.0") + "," + after.z.ToString("0.0") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReconcileMoveCrt snap failed: " + ex); }
        }

        /// <summary>CLIENT (mirror): wait until the animated mirror-walk finishes (<c>nav.IsNavigating==false</c>),
        /// THEN re-derive the idle cover/stance pose at <paramref name="dst"/> (bug A). Pose-only — the in-flight
        /// Navigate owns the position. Mirrors the host's ordering: the host sets the pose at walk ARRIVAL inside the
        /// <c>MoveAbility.Move</c> coroutine (after WaitUntilFinished, MoveAbility.cs:121). Setting it at walk KICKOFF
        /// would feed the animator cover params while the walk anim is still playing (wrong pose blend / mid-walk
        /// crouch). Same wait-loop + frame cap as <see cref="ReconcileMoveCrt"/>.</summary>
        private static IEnumerator<NextUpdate> RederiveCoverPoseAfterNavCrt(object actor, object nav, Vector3 dst)
        {
            int frames = 0;
            while (frames < ReconcileMaxFrames)
            {
                bool navigating = false;
                try { navigating = ToBool(GetProp(nav, "IsNavigating")); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] RederiveCoverPoseAfterNavCrt: IsNavigating read failed: " + ex); }
                if (!navigating) break;
                frames++;
                yield return NextUpdate.NextFrame;
            }
            TryRederiveCoverPose(actor, dst);   // bug A: re-derive at walk ARRIVAL, matching the host (MoveAbility.cs:121)
        }

        /// <summary>Find and invoke <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> (first param the
        /// coroutine, remaining params optional → filled with Type.Missing). Mirrors TacticalTurnSync's helper.</summary>
        private static bool InvokeStart(object timingInstance, Type timingType, object crt)
        {
            try
            {
                MethodInfo best = null;
                foreach (var mth in timingType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (mth.Name != "Start") continue;
                    var pars = mth.GetParameters();
                    if (pars.Length < 1) continue;
                    if (!typeof(IEnumerator).IsAssignableFrom(pars[0].ParameterType)) continue;
                    bool restOptional = true;
                    for (int i = 1; i < pars.Length; i++) if (!pars[i].IsOptional) { restOptional = false; break; }
                    if (!restOptional) continue;
                    if (best == null || pars.Length < best.GetParameters().Length) best = mth;
                }
                if (best == null) return false;
                var bp = best.GetParameters();
                var args = new object[bp.Length];
                args[0] = crt;
                for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;
                best.Invoke(timingInstance, args);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] InvokeStart failed: " + ex); return false; }
        }

        private static bool ToBool(object o) => o is bool b && b;

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

        // [DIAG] InvariantCulture float format so move logs are locale-unambiguous ("-6.5", never "-6,5").
        private static string F(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        // [DIAG] best-effort human name for an actor (TacticalActorBase.DisplayName), falling back to the runtime
        // type name; never throws.
        private static string ActorName(object actor)
        {
            if (actor == null) return "<null>";
            try { if (GetProp(actor, "DisplayName") is string s && !string.IsNullOrEmpty(s)) return s; }
            catch { }
            return actor.GetType().Name;
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
