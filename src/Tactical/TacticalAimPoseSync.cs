using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.PathProcessors;
using PhoenixPoint.Tactical.View;
using PhoenixPoint.Tactical.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// A8b — THE AIM POSE ONLY. A soldier committed to manual aim holds the aim STANCE on every peer, so a
    /// shot starts from the same animator state everywhere. Nothing else about aiming crosses.
    ///
    /// ─── WHY THE POSE IS NOT COSMETIC ───
    /// <c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1645 —
    /// <c>if (shootAnimAction.UseAiming &amp;&amp; !stepOutNeeded &amp;&amp; !tacticalActor.CurrentlyAiming)</c> —
    /// skips the entire aim-start block (:1647-1678: an <c>AnimatorStateCheckpoint</c> wait with a 5 s
    /// ceiling) when the actor is ALREADY aiming. The peer that aimed manually fires instantly; every mirror
    /// first plays the entry-into-aim and only then the shot. A built-in desync on EVERY shot.
    ///
    /// <c>TacticalActor.CurrentlyAiming</c> (TacticalActor.cs:228-238) reads ANIMATOR INTEGERS and nothing
    /// else: <c>Animator.GetInteger("TravelType") == 7</c> (<c>TravelType.Aim</c>) or
    /// <c>GetInteger("ShootSegmentType") == 5</c>. So the stance IS those integers, and the game's own
    /// nav-free writer for them is <c>PathProcessorUtils.SetAimParams</c>:81-84 /
    /// <c>SetNullNavParams</c>:91-94 → <c>SetParams</c>:67-74, whose entire body is
    /// <c>for (…) animator.SetInteger(name, value)</c> — no other callee, cannot allocate a
    /// <c>PathPointInfo</c>, cannot reach <c>TacticalNav</c>. It is what <c>FireWeaponAtTargetCrt</c>:1651
    /// sets and :1712 clears on itself. <c>AimSegmentType.AimLoop</c> is the HELD value:
    /// <c>GetAimOrPeekPathPoints</c>:566-568 makes <c>AimStart</c> the transition point and <c>AimLoop</c> the
    /// final one at :643.
    ///
    /// ─── WHAT IS DELIBERATELY NOT HERE (removed 2026-08-04 by developer decision, do NOT restore) ───
    /// The third-person aim CAMERA, the aim UI, the auto-enter/auto-leave of a watcher's aim view state, and
    /// the TAB-target-cycling mirror. A watcher keeps its own free camera and its own screen; an arriving
    /// message poses an actor and NEVER moves anyone's state stack. RailCheck L97 keeps that red if it
    /// returns.
    ///
    /// ─── THE NAV BAN (the defect that got 3071859 reverted by 0252247) ───
    /// The first attempt fed a <c>CoverPose</c> to <c>IdleAbility.ForceRefresh</c>, whose consumption
    /// (<c>IdleAbility.IdleAction</c>:275-290 → <c>RefreshIdle</c>:324 → <c>DoAimOrPeek</c>:166 →
    /// <c>PathProcessorUtils.GetAimOrPeekPathPoints</c> → <c>TacticalNav.ExecutePoints</c>) is a NAV
    /// TRAVERSAL — it MOVED mirrored soldiers instead of posing them. This file never touches
    /// <c>IdleAbility</c> or <c>TacticalNavigationComponent</c>.
    ///
    /// ─── DEDUP BY SAMPLING, NOT EDGES ───
    /// <see cref="AimStatePatch"/> samples the live top view state once per frame and emits only on
    /// difference against the SHARED table. Over-emission is impossible (a transition's intermediate values
    /// never exist as a sample; <c>UIStateShoot</c> genuinely alternates target→null→target across
    /// <c>ExitState</c>:1261 and <c>SetShootTarget</c>:277, which is what broke the edge-capture attempt).
    /// Swallowing is impossible (the compare is against the shared table, not a private memo, so any
    /// disagreement is re-asserted on the very next frame, forever).
    /// </summary>
    internal static class TacticalAimPoseSync
    {
        // Wire ops on SurfaceIds.TacAimPose (host→all) and SurfaceIds.TacAimPoseIntent (client→host).
        private const byte OpAim = 1;
        internal const byte OpSetAim = 1;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>THE SHARED AIM STATE, actorKey → targetKey (0 = not aiming). Every peer holds a copy;
        /// the HOST's is the authority and the only writer that reaches the wire. Absent key == 0.</summary>
        private static readonly Dictionary<int, int> _aim = new Dictionary<int, int>();

        // This peer's live sample (what its own screen really holds) and what it last put on the wire.
        private static int _liveActor, _liveTarget;
        private static int _reportedActor, _reportedTarget;

        /// <summary>Actor keys whose stance this peer could NOT apply yet (actor not resolvable, no animator,
        /// still navigating, weapon/target not there). The EMITTER's self-heal cannot cover these: once the
        /// shared table agrees with the aiming peer's own screen it goes silent forever, so a mirror that
        /// dropped the message on arrival would never hear it again (a soldier still walking when the aim
        /// lands is the ordinary case). Retried from the frame postfix until it lands.</summary>
        private static readonly HashSet<int> _pending = new HashSet<int>();
        private static readonly List<int> _retryScratch = new List<int>();

        /// <summary>The mirror's IN-FLIGHT facing turns, actorKey → where it started, where it is going, how
        /// far along. Facing is part of the stance and must LERP, never snap — see <see cref="StartTurn"/>.
        /// </summary>
        private static readonly Dictionary<int, AimTurn> _turning = new Dictionary<int, AimTurn>();
        private static readonly List<int> _turnScratch = new List<int>();

        private struct AimTurn { public Vector3 From, To; public float T; }

        /// <summary>The game's own aim-facing rate, <c>TacticalNavigationComponent.NoAnimsFace</c>:1040
        /// (<c>FacingLerp += (float)Timing.Delta * 6f</c>) — copied as a VALUE, not called: that method is
        /// declared on <c>TacticalNavigationComponent</c>, which this arc may not reach.</summary>
        private const float FacingLerpSpeed = 6f;

        private static readonly HashSet<string> _loggedFailures = new HashSet<string>();

        /// <summary>The battle this table belongs to. Actor keys are re-derived per battle, so carrying the
        /// table across one would pose the NEXT battle's actors off a dead one's values.</summary>
        private static TacticalLevelController _battle;

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.TacAimPoseIntent, "tac-aim-pose",
                                new Dictionary<byte, IntentRail.OpHandler> { [OpSetAim] = HandleSetAim });
        }

        private static TacticalLevelController Tlc() => TacticalDamageSync.Tlc();

        private static NetworkEngine LiveEngine()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return null;
            var coord = engine.SaveTransfer;
            return coord != null && coord.SessionStarted ? engine : null;
        }

        private static int Shared(int actorKey)
        {
            int t;
            return _aim.TryGetValue(actorKey, out t) ? t : 0;
        }

        // ─── THE ARBITER (pure — RailCheck L97 executes it) ────────────────

        /// <summary>
        /// LAST-WRITER-WINS, decided from this peer's live stance, what it last announced, and the SHARED
        /// values. Pure: no game types, no statics, so the whole race is falsifiable headless.
        ///
        /// <paramref name="clearReported"/> — the stance I announced is one I no longer hold, so it must be
        /// dropped for everyone. Gated on the shared value still BEING mine: if a newer writer overwrote it,
        /// that soldier's aim is not mine to end (this is what stops a peer who merely visited the soldier
        /// from cancelling another peer's live aim on the way out).
        ///
        /// <paramref name="assertLive"/> — the shared state does not carry the stance I actually hold. It
        /// fires on a genuine change (fresh aim, or a new target) and, crucially, as a SELF-HEAL when the
        /// shared value went to 0 while I am still aiming. It deliberately does NOT fire when the shared
        /// value names a DIFFERENT non-zero target I already conceded: that is another peer's newer write,
        /// and re-asserting over it is an unbounded ping-pong between two peers aiming one soldier at two
        /// enemies — an ordinary situation in a shared battle, not an exotic one.
        /// </summary>
        internal static void Decide(int liveActor, int liveTarget, int reportedActor, int reportedTarget,
                                    int sharedForReported, int sharedForLive,
                                    out bool clearReported, out bool assertLive)
        {
            clearReported = reportedActor != 0
                            && (reportedActor != liveActor || liveTarget == 0)
                            && sharedForReported == reportedTarget;

            assertLive = liveActor != 0 && liveTarget != 0
                         && sharedForLive != liveTarget
                         && (sharedForLive == 0 || reportedActor != liveActor || reportedTarget != liveTarget);
        }

        // ─── CAPTURE: sample the live state, once per frame ────────────────

        /// <summary>
        /// The view's OWN frame slot — a postfix on <c>TacticalViewState.Update</c>, which the state stack
        /// calls every frame for whichever state is current (the same seam <c>TacticalUiRepaint</c> uses, and
        /// for the same reason: it runs on the HOST too, where a client's aim produces no local transition).
        /// A SAMPLER, not a stream: a peer sitting in aim mode for a minute sends nothing.
        /// </summary>
        [HarmonyPatch(typeof(TacticalViewState), nameof(TacticalViewState.Update))]
        internal static class AimStatePatch
        {
            private static void Postfix(TacticalViewState __instance)
            {
                var tlc = Tlc();
                if (!ReferenceEquals(_battle, tlc)) ResetForBattle(tlc);
                if (LiveEngine() == null) return;
                var view = tlc == null ? null : tlc.View;
                if (view == null) return;

                Sample(__instance, view);
                Emit();
                RetryPending();
                AdvanceTurns();
            }
        }

        private static void ResetForBattle(TacticalLevelController tlc)
        {
            _battle = tlc;
            Seq.Reset();
            _aim.Clear();
            _liveActor = _liveTarget = _reportedActor = _reportedTarget = 0;
            _pending.Clear();
            _retryScratch.Clear();
            _turning.Clear();
            _turnScratch.Clear();
            _loggedFailures.Clear();
        }

        /// <summary>What THIS peer's screen actually holds. A committed stance is the exact type
        /// <c>UIStateShoot</c> (never the <c>UIStateFreeCam</c> subclass, whose crosshair re-targets as it
        /// sweeps — hover/preview aiming stays local, law 5) WITH a target actor: aim mode with no target is
        /// not a commitment, it is a screen looking for one.</summary>
        private static void Sample(TacticalViewState state, PhoenixPoint.Tactical.View.TacticalView view)
        {
            _liveActor = _liveTarget = 0;
            var shoot = state as UIStateShoot;
            if (shoot == null || state.GetType() != typeof(UIStateShoot)) return;
            var target = shoot.AbilityTarget;
            int targetKey = target == null ? 0 : TacticalActorKey.Of(target.GetTargetActor());
            if (targetKey == 0) return;
            int actorKey = TacticalActorKey.Of(view.SelectedActor);
            if (actorKey == 0) return;
            _liveActor = actorKey;
            _liveTarget = targetKey;
        }

        private static void Emit()
        {
            bool clear, assert;
            Decide(_liveActor, _liveTarget, _reportedActor, _reportedTarget,
                   Shared(_reportedActor), Shared(_liveActor), out clear, out assert);
            if (!clear && !assert) return;
            if (clear)
            {
                Announce(_reportedActor, 0);
                _reportedActor = _reportedTarget = 0;
            }
            if (assert)
            {
                Announce(_liveActor, _liveTarget);
                _reportedActor = _liveActor;
                _reportedTarget = _liveTarget;
            }
        }

        /// <summary>One aim change onto the rail. The host writes its own table and broadcasts; a client asks
        /// through the intent surface and waits for the host's echo — single writer, one ordered stream
        /// (law 7), so "last writer" means "last to reach the host": the rail's standing arbitration, with no
        /// ownership table anywhere.</summary>
        private static void Announce(int actorKey, int targetKey)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            if (engine.IsHost) HostSet(actorKey, targetKey);
            else IntentRail.Send(SurfaceIds.TacAimPoseIntent, OpSetAim,
                                 "aim-pose " + actorKey + " -> " + targetKey,
                                 w => { w.Write(actorKey); w.Write(targetKey); });
        }

        // ─── HOST: the one writer ──────────────────────────────────────────

        private static void HandleSetAim(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
            => HostSet(r.ReadInt32(), r.ReadInt32());

        /// <summary>Write the shared table and tell everyone. An announcement that matches the table is
        /// dropped HERE — the second dedup gate, and the one that covers peers disagreeing with each other:
        /// a peer merely re-selecting a soldier that already aims at that target costs zero wire.</summary>
        private static void HostSet(int actorKey, int targetKey)
        {
            if (actorKey == 0) return;
            if (Shared(actorKey) == targetKey) return;
            _aim[actorKey] = targetKey;
            ApplyStance(actorKey, targetKey);

            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            try
            {
                uint seq = Seq.Next(SurfaceIds.TacAimPose);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    w.Write(OpAim);
                    w.Write(actorKey);
                    w.Write(targetKey);
                    inner = ms.ToArray();
                }
                // Broadcast to ALL, the originator included: every peer's table must be byte-identical or the
                // dedup compare answers differently on different screens.
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.TacAimPose, SyncKind.StateDelta, inner)));
                Debug.Log("[Multiplayer][tac] HOST aim-pose " + actorKey + " -> " +
                          (targetKey == 0 ? "<none>" : targetKey.ToString()) + " seq=" + seq);
            }
            catch (Exception ex)
            {
                // A dropped stance leaves the other peers replaying the aim-in animation before every shot
                // this soldier fires — the exact desync this surface exists to remove. Never silent.
                Debug.LogError("[Multiplayer][tac] HOST aim-pose FAILED to reach the wire: " + ex);
            }
        }

        // ─── EVERY PEER: apply ─────────────────────────────────────────────

        /// <summary>Consumes <see cref="SurfaceIds.TacAimPose"/> only; the intent surface belongs to
        /// <see cref="IntentRail"/> and falls through untouched.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacAimPose) return false;
            if (engine == null || engine.IsHost) return true;   // the host wrote the table itself
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Seq.ShouldApply(SurfaceIds.TacAimPose, seq)) return true;  // stale re-delivery (law 7)
                    if (op != OpAim)
                    {
                        Debug.LogError("[Multiplayer][tac] unknown host→all aim-pose op " + op + " (seq=" + seq + ")");
                        return true;
                    }
                    int actorKey = r.ReadInt32(), targetKey = r.ReadInt32();
                    _aim[actorKey] = targetKey;
                    ApplyStance(actorKey, targetKey);   // law 11 — the pose is on screen the instant it lands
                    Seq.Mark(SurfaceIds.TacAimPose, seq);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] aim-pose inbound FAILED: " + ex);
            }
            return true;
        }

        /// <summary>MUST THE STANCE WRITE WAIT — pure, so RailCheck L231 executes the OUTCOME rather than
        /// asserting that a guard exists. TRUE means the animator belongs to the engine right now and this
        /// mirror keeps its hands off; the write is not dropped, it goes on <see cref="_pending"/> and is
        /// retried every frame by <see cref="RetryPending"/>, so the stance still lands the frame the actor
        /// is free.
        ///
        /// Two axes and no more. NAVIGATION was always here. EXECUTION is the one this law is named for:
        /// <c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1646-1665 writes the SAME animator
        /// parameters this mirror writes and then SPINS on the "AimStart" checkpoint for up to five of that
        /// actor's seconds — so a stance write that lands inside a shot does not merely look wrong, it stalls
        /// the shot on the host, and the host is the peer that decides when the damage is dealt.</summary>
        internal static bool StanceMustWait(bool actorIsNavigating, bool engineIsExecutingAnAbility) =>
            actorIsNavigating || engineIsExecutingAnAbility;

        /// <summary>
        /// THE MIRROR HALF — the animator integers plus the facing lerp, and nothing else. It moves no
        /// camera, opens no UI and touches no view state.
        ///
        /// Skipped when this peer is the one holding the stance natively (its own <c>UIStateShoot</c> already
        /// ran the game's real aim path, and stomping the integers mid-transition would fight it). Every
        /// OTHER reason to skip is TRANSIENT and goes on <see cref="_pending"/> instead of being dropped.
        ///
        /// It is nav-free AND event-free at rest: <c>TacticalActorBase.SetTransform</c>:665-684 raises
        /// <c>TacticalLevel.ActorMoved</c> only inside <c>if (!Utl.Equals(actor.Pos, prevPos))</c>
        /// (<c>TacticalLevelController</c>:1157-1163) and <c>ActorMovedInNewTile</c> only when the VOXEL
        /// changes — a rotation-only write changes neither.
        /// </summary>
        private static void ApplyStance(int actorKey, int targetKey)
        {
            if (actorKey == _liveActor && targetKey == _liveTarget) { _pending.Remove(actorKey); return; }
            string why;
            var actor = TacticalActorKey.ResolveActor(Tlc(), actorKey, out why);
            var animator = actor == null ? null : actor.Animator;
            if (animator == null)
            {
                if (actor == null && why != null && _loggedFailures.Add(why))
                    Debug.LogWarning("[Multiplayer][tac] aim pose for actor key " + actorKey +
                                     " not applied yet: " + why);
                // A stance we cannot clear on an actor that is not there is moot; a stance we cannot SET is
                // owed to that actor the moment it exists.
                if (targetKey == 0) _pending.Remove(actorKey); else _pending.Add(actorKey);
                return;
            }
            var nav = actor.TacticalNav;
            // AN ACTOR THE ENGINE IS DRIVING OWNS ITS OWN ANIMATOR (law L231). The stance mirror writes the
            // very animator parameters the game's fire coroutine writes and then WAITS on:
            // TacticalLevelController.FireWeaponAtTargetCrt:1646-1665 arms the "AimStart" checkpoint
            // behaviour, calls SetAimParams(animator, AimSegmentType.AimStart), and spins until that
            // checkpoint clears with a 5 s timeout. A clear landing inside that window calls
            // SetNullNavParams on the same animator, the checkpoint never becomes inactive, and the shot
            // sits out the WHOLE timeout before firing.
            //
            // MEASURED, NOT ARGUED (2026-08-08, three instances). The acting peer's UI leaves UIStateShoot
            // the instant its mirrored order arrives (TacticalCommandSync.ReleaseLocalUiHolding), which is a
            // stance it no longer holds, so Emit announces the clear one frame later. On the host that
            // intent landed 17 ms AFTER FireWeaponAtTargetCrt had started waiting: "wait while
            // [AIM_START_ANIMATION_IS_ACTIVE]" at 536.391, "HOST aim-pose 3 -> <none>" at 536.408, and
            // "Actor Soldier_5 has timed out waiting for aim animation" at 540.043 — 3.65 s of nothing, on
            // the ONE peer that decides when the damage is dealt. Every other peer had already played the
            // whole shot from the same record; the enemy then died a second and a half after the shooter had
            // visibly finished, which is the owner's report verbatim.
            //
            // TRANSIENT, so it PENDS rather than drops — the same posture as the navigation guard above,
            // through the same RetryPending loop, so the aim-out still happens the frame the shot ends.
            // HasExecutingAbility ignores IdleAbility by the engine's own rule
            // (TacticalActorBase:695-704), which is exactly right here: idling IS the state a stance is
            // written on, and only a real ability (shoot, melee, throw, move) owns the animator.
            if (StanceMustWait(nav != null && nav.IsNavigating, actor.HasExecutingAbility()))
            {
                _pending.Add(actorKey);
                return;
            }

            if (targetKey == 0)
            {
                PathProcessorUtils.SetNullNavParams(animator);
                _pending.Remove(actorKey);
                _turning.Remove(actorKey);   // the stance is gone; a swing still owed to it is stale
                return;
            }

            // Bind the aim clips exactly as the game does one line before it sets these same params
            // (FireWeaponAtTargetCrt:1566 → :1651). Without the override the "Aim Loop" state has no clip.
            var target = TacticalActorKey.Resolve(Tlc(), targetKey, out why);
            var weapon = actor.Equipments == null ? null : actor.Equipments.SelectedWeapon;
            var anims = actor.ActorAnimActions;
            if (target == null || weapon == null || anims == null) { _pending.Add(actorKey); return; }
            var shootAnim = anims.ActivateShootingClips(weapon);
            // The game's own test for "this weapon can aim at all" (GetAimOrPeekPathPoints:549-552). A weapon
            // with no aim animation is a settled answer, not a transient one — nothing to retry.
            if (shootAnim == null || shootAnim.Aim == null || !shootAnim.Aim.HasAllAnimations)
            {
                _pending.Remove(actorKey);
                return;
            }
            PathProcessorUtils.SetAimParams(animator, AimSegmentType.AimLoop);

            // Flattened like the game's own aim facing (NoAnimsFace zeroes y on the same vector); the up axis
            // stays the actor's own.
            var dir = target.Pos - actor.Pos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) StartTurn(actorKey, actor, dir.normalized);
            _pending.Remove(actorKey);
        }

        /// <summary>
        /// THE SWING, not the snap. A retarget (TAB) on the ACTING peer never re-runs the aim path points:
        /// <c>PathProcessorUtils.GetAimOrPeekPathPoints</c>:620-623 returns FALSE for an actor already in
        /// "Aim Loop" with no peek (its 90°/180° turn arms are dead code — both thresholds are the constant
        /// <c>-2f</c>, :589-590, and a dot product cannot go below -1), so <c>IdleAbility.DoAimOrPeek</c>
        /// takes its else-branch and the whole visible turn is
        /// <c>TacticalNavigationComponent.FaceWithLerpOnly</c> → <c>NoAnimsFace</c>:1035-1053, a plain
        /// <c>Vector3.Slerp</c> stepped by <c>Timing.Delta * 6f</c> and written with <c>SetForward</c>.
        /// Writing that lerp's ENDPOINT in one frame — which the first version of this mirror did — is
        /// exactly the target-switch teleport reported on every non-acting instance.
        ///
        /// So the mirror runs the lerp instead: same rate, same primitive
        /// (<c>ActorComponent.SetForward</c>:280 → <c>SetRotation</c>:294 → <c>SetTransform</c>:299), driven
        /// from this class's own per-frame postfix rather than a coroutine on the nav component, which this
        /// arc may not reach. The step uses the ACTOR's timing, not <c>Time.deltaTime</c>: <c>NoAnimsFace</c>
        /// reads <c>base.Timing.Delta</c>, which carries that actor's <c>TimingScale</c> (an unrevealed actor
        /// runs at 4x), so the wall clock would make mirrored turns disagree with native ones exactly where
        /// the game speeds them up.
        ///
        /// ponytail: the facing lerps, the AIM IK does not — <c>DoAimOrPeek</c>:210-243 cross-fades
        /// <c>AimIK.solver.IKPositionWeight</c> through a private of <c>IdleAbility</c>, the one type this arc
        /// may not touch. Upgrade path if the WEAPON (as opposed to the soldier) is ever seen lagging on a
        /// mirror: drive the actor's own <c>AimIK</c> component directly, never through <c>IdleAbility</c>.
        /// </summary>
        private static void StartTurn(int actorKey, TacticalActor actor, Vector3 to)
        {
            // Only ever ARMS the turn: writing the destination here — even once, even as a "first step" —
            // is the teleport this seam replaces. A re-arm onto the same heading is a no-op swing.
            _turning[actorKey] = new AimTurn { From = actor.transform.forward, To = to, T = 0f };
        }

        /// <summary>One frame of every in-flight mirrored turn. Bounded by the number of soldiers whose aim
        /// changed in the last ~1/6 s (in practice 0 or 1), silent when empty, and self-terminating: an entry
        /// leaves on completion or the moment its actor stops resolving.</summary>
        private static void AdvanceTurns()
        {
            if (_turning.Count == 0) return;
            _turnScratch.Clear();
            _turnScratch.AddRange(_turning.Keys);
            foreach (var key in _turnScratch)
            {
                AimTurn turn;
                if (!_turning.TryGetValue(key, out turn)) continue;
                string why;
                var actor = TacticalActorKey.ResolveActor(Tlc(), key, out why);
                if (actor == null || actor.Timing == null) { _turning.Remove(key); continue; }

                turn.T += (float)actor.Timing.Delta * FacingLerpSpeed;
                actor.SetForward(Vector3.Slerp(turn.From, turn.To, Mathf.Min(turn.T, 1f)));
                if (turn.T >= 1f) _turning.Remove(key);
                else _turning[key] = turn;
            }
        }

        /// <summary>Re-applies the stances that could not land when they arrived. Bounded by the number of
        /// soldiers actually aiming, cleared per battle, and silent when empty.</summary>
        private static void RetryPending()
        {
            if (_pending.Count == 0) return;
            _retryScratch.Clear();
            _retryScratch.AddRange(_pending);
            foreach (var key in _retryScratch) ApplyStance(key, Shared(key));
        }
    }
}
