using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// A8 REBUILT (law L81's carve-out, second attempt). The COMMITTED manual-aim stance is SHARED state
    /// for a soldier — not a per-window local mode — with two obligations:
    ///   (1) a soldier committed to aiming holds that stance on EVERY peer, so the shot animation starts
    ///       from the same state everywhere. <c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1645 skips
    ///       the whole aim-start block (:1649-1683, a checkpoint wait with a 5 s ceiling) when
    ///       <c>TacticalActor.CurrentlyAiming</c> is true, so the peer that already aimed fires instantly
    ///       while every mirror first plays the entry-into-aim. A built-in desync on EVERY shot.
    ///   (2) LAST WRITER WINS: whoever entered aim last owns that soldier's aim state, and a peer that
    ///       switches to the soldier lands DIRECTLY in aim mode with the matching UI and camera instead of
    ///       opening a fresh local one.
    ///
    /// ─── WHY THIS CANNOT NAV-MOVE A MIRROR (the defect that got 3071859 reverted by 0252247) ───
    /// The first attempt fed a <c>CoverPose</c> to <c>IdleAbility.ForceRefresh</c>. Its parking is harmless
    /// but its CONSUMPTION is not a pose set: <c>IdleAbility.IdleAction</c>:275-290 → <c>RefreshIdle</c>:324
    /// → <c>DoAimOrPeek</c>:166 → <c>PathProcessorUtils.GetAimOrPeekPathPoints</c> →
    /// <c>TacticalNav.ExecutePoints</c>:185, a NAV TRAVERSAL. On a mirror whose position had not settled yet
    /// that traversal is a real journey — for a jetpacked soldier, a flight back to its old tile and out again.
    ///
    /// This file never touches <c>IdleAbility</c>. <c>CurrentlyAiming</c> (TacticalActor.cs:227-237) reads
    /// ANIMATOR PARAMETERS and nothing else — <c>Animator.GetInteger("TravelType") == 7</c>
    /// (<c>TravelType.Aim</c>) or <c>GetInteger("ShootSegmentType") == 5</c> — so the stance is set by writing
    /// exactly those integers: <c>PathProcessorUtils.SetAimParams</c>:81-84 /
    /// <c>SetNullNavParams</c>:91-94, both of which forward to <c>SetParams</c>:67-74, whose ENTIRE body is
    /// <c>for (…) animator.SetInteger(name, value)</c>. That method has no other callee: it cannot allocate a
    /// <c>PathPointInfo</c>, cannot start a coroutine, cannot reach <c>TacticalNav</c> at all. It is also the
    /// GAME'S OWN nav-free aim entry — <c>FireWeaponAtTargetCrt</c>:1651 sets exactly these params (after
    /// :1566 binds the clips) and :1712 clears them with <c>SetNullNavParams</c>, which is also what
    /// <c>RefreshIdle</c>:329 uses on its non-aim arm. Note that the forbidden path's own path points carry
    /// <c>IntParams = GetAimParams(...)</c> and <c>Position = actor.Pos</c>: the animator integers ARE the
    /// whole model effect of native aiming, and the traversal is only the delivery vehicle.
    ///
    /// The POSE stays locally computed (law 5): nothing about cover geometry crosses the wire. What crosses
    /// is the DISCRETE pair (actorKey, targetKey).
    ///
    /// ─── WHY DEDUPLICATION CANNOT FAIL IN EITHER DIRECTION ───
    /// 3071859 captured EDGES (a prefix on <c>ForceRefresh</c>) and deduped them against a per-actor cache.
    /// That over-emitted (2-3 messages per repaint: <c>UIStateShoot.ExitState</c>:1261 calls
    /// <c>FaceEnemy(null)</c> and <c>EnterState</c>/<c>SetShootTarget</c>:277 calls <c>FaceEnemy(target)</c>,
    /// so the edge stream genuinely alternates target→null→target and no value compare can collapse it) AND
    /// it swallowed (an edge suppressed by the cache was never re-derivable, so a third peer never learned).
    ///
    /// This captures STATE, not edges: <see cref="AimStatePatch"/> SAMPLES the live top view state once per
    /// frame and emits only on difference. Over-emission is impossible because a transition's intermediate
    /// values never exist as a sample — N identical frames produce zero messages. Swallowing is impossible
    /// because the compare is against the SHARED table, not a private memo: any disagreement between what
    /// this peer actually holds and what every peer believes is re-asserted on the very next frame, forever.
    /// The arbitration is <see cref="Decide"/>, a PURE function (RailCheck L82 falsifies all five cases).
    ///
    /// ─── WHAT IS DELIBERATELY NOT DONE ───
    /// • Nothing PUSHES a peer's screen into aim mode. A message only updates the table and poses the actor;
    ///   the UI half fires on that peer's OWN selection change (the A6 lesson — <c>InventoryAbility</c>
    ///   yanking every peer into an inventory nobody opened). Entering is the game's own
    ///   <c>UIStateCharacterSelected.ActivateAttackAbilityState</c>:1466-1502, the identical call its hotkey
    ///   makes, so the state stack shape is native and this file never Exits anything — the 7933bbe
    ///   double-<c>ExitState</c> hazard is structurally out of reach.
    /// • <c>UIStateFreeCam : UIStateShoot</c> is excluded by an EXACT type test: its crosshair re-targets as
    ///   it sweeps (UIStateFreeCam:708/714), which is the hover/preview aiming law 5 keeps local.
    /// </summary>
    internal static class TacticalAimSync
    {
        // Wire ops on SurfaceIds.TacAim (host→all) and SurfaceIds.TacAimIntent (client→host).
        private const byte OpAim = 1;
        internal const byte OpSetAim = 1;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>THE SHARED AIM STATE, actorKey → targetKey (0 = not aiming). Every peer holds a copy;
        /// the HOST's is the authority and the only writer that reaches the wire. Absent key == 0.</summary>
        private static readonly Dictionary<int, int> _aim = new Dictionary<int, int>();

        // This peer's live sample (what its own screen really holds) and what it last put on the wire.
        private static int _liveActor, _liveTarget;
        private static int _reportedActor, _reportedTarget;

        // R2: the selection edge that still owes an auto-enter into aim mode. 0 = none.
        private static int _enterAimFor;
        private static TacticalActor _lastSelected;
        private static int _verifyEnterFor;

        /// <summary>Actor keys whose stance this peer could NOT apply yet (actor not resolvable, no animator,
        /// still navigating, weapon/target not there). The EMITTER's self-heal cannot cover these: once the
        /// shared table agrees with the aiming peer's own screen it goes silent forever, so a mirror that
        /// dropped the message on arrival never hears it again. Retried from the frame postfix until it lands.
        /// </summary>
        private static readonly HashSet<int> _pending = new HashSet<int>();
        private static readonly List<int> _retryScratch = new List<int>();

        private static readonly HashSet<string> _loggedFailures = new HashSet<string>();

        /// <summary>Per-BATTLE state (driven from <c>TacticalTurnSync.Reset</c>). Carrying the table into the
        /// next battle would pose actors from a dead one, since keys are re-derived per battle.</summary>
        internal static void Reset()
        {
            Seq.Reset();
            _aim.Clear();
            _liveActor = _liveTarget = _reportedActor = _reportedTarget = 0;
            _enterAimFor = 0;
            _verifyEnterFor = 0;
            _lastSelected = null;
            _pending.Clear();
            _retryScratch.Clear();
            _loggedFailures.Clear();
        }

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.TacAimIntent, "tac-aim",
                                new Dictionary<byte, IntentRail.OpHandler> { [OpSetAim] = HandleSetAim });
        }

        private static TacticalLevelController Tlc()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<TacticalLevelController>();
        }

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

        // ─── THE ARBITER (pure — RailCheck L82) ────────────────────────────

        /// <summary>
        /// LAST-WRITER-WINS, decided from this peer's live stance, what it last announced, and the SHARED
        /// values. Pure: no game types, no statics, so the whole race is falsifiable headless.
        ///
        /// <paramref name="clearReported"/> — the stance I announced is one I no longer hold, so it must be
        /// dropped for everyone. Gated on the shared value still BEING mine: if a newer writer overwrote it,
        /// that soldier's aim is not mine to end (this is what stops peer B, who merely visited the soldier,
        /// from cancelling peer A's live aim on the way out).
        ///
        /// <paramref name="assertLive"/> — the shared state does not carry the stance I actually hold. It
        /// fires on a genuine change (fresh aim, or a new target) and, crucially, as a SELF-HEAL when the
        /// shared value went to 0 while I am still aiming. It deliberately does NOT fire when the shared
        /// value names a DIFFERENT non-zero target that I have already conceded: that is another peer's newer
        /// write, and re-asserting over it is an unbounded ping-pong between two peers aiming one soldier at
        /// two enemies — which the shared-battle model (any peer commands any soldier) makes an ordinary
        /// situation, not an exotic one.
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
        ///
        /// This is a SAMPLER, not a stream: it reads a discrete pair and puts something on the wire only when
        /// that pair disagrees with the shared state. A peer sitting in aim mode for a minute sends nothing.
        /// </summary>
        [HarmonyPatch(typeof(TacticalViewState), nameof(TacticalViewState.Update))]
        internal static class AimStatePatch
        {
            private static void Postfix(TacticalViewState __instance)
            {
                if (LiveEngine() == null) { _lastSelected = null; return; }
                var tlc = Tlc();
                var view = tlc == null ? null : tlc.View;
                if (view == null) { _lastSelected = null; return; }

                Sample(__instance, view);
                Emit();
                RetryPending();
                DriveAutoEnter(__instance, view);
            }
        }

        /// <summary>What THIS peer's screen actually holds. A committed stance is the exact type
        /// <c>UIStateShoot</c> (never the <c>UIStateFreeCam</c> subclass) WITH a target actor: aim mode with
        /// no target is not a commitment, it is a screen looking for one.</summary>
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
        /// (law 7), so "last writer" means "last to reach the host", the rail's standing arbitration with no
        /// ownership table anywhere.</summary>
        private static void Announce(int actorKey, int targetKey)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            if (engine.IsHost) HostSet(actorKey, targetKey);
            else IntentRail.Send(SurfaceIds.TacAimIntent, OpSetAim,
                                 "aim " + actorKey + " -> " + targetKey,
                                 w => { w.Write(actorKey); w.Write(targetKey); });
        }

        // ─── HOST: the one writer ──────────────────────────────────────────

        private static void HandleSetAim(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
            => HostSet(r.ReadInt32(), r.ReadInt32());

        /// <summary>Write the shared table and tell everyone. An announcement that matches the table is
        /// dropped HERE, which is the second dedup gate and the one that covers peers disagreeing with each
        /// other: a peer merely re-selecting a soldier that already aims at that target costs zero wire.</summary>
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
                uint seq = Seq.Next(SurfaceIds.TacAim);
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
                // selection lookup below answers differently on different screens.
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.TacAim, SyncKind.StateDelta, inner)));
                Debug.Log("[Multiplayer][tac] HOST aim " + actorKey + " -> " +
                          (targetKey == 0 ? "<none>" : targetKey.ToString()) + " seq=" + seq);
            }
            catch (Exception ex)
            {
                // A dropped stance leaves the other peers replaying the aim-in animation before every shot
                // this soldier fires — the exact desync this surface exists to remove. Never silent.
                Debug.LogError("[Multiplayer][tac] HOST aim FAILED to reach the wire: " + ex);
            }
        }

        // ─── CLIENT: apply ─────────────────────────────────────────────────

        /// <summary>Consumes <see cref="SurfaceIds.TacAim"/> only; the 0x86 intent belongs to
        /// <see cref="IntentRail"/> and falls through untouched.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacAim) return false;
            if (engine == null || engine.IsHost) return true;   // the host wrote the table itself
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Seq.ShouldApply(SurfaceIds.TacAim, seq)) return true;  // stale re-delivery (law 7)
                    if (op != OpAim)
                    {
                        Debug.LogError("[Multiplayer][tac] unknown host→all aim op " + op + " (seq=" + seq + ")");
                        return true;
                    }
                    int actorKey = r.ReadInt32(), targetKey = r.ReadInt32();
                    _aim[actorKey] = targetKey;
                    ApplyStance(actorKey, targetKey);
                    Seq.Mark(SurfaceIds.TacAim, seq);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] aim inbound FAILED: " + ex);
            }
            return true;
        }

        /// <summary>
        /// THE MIRROR HALF — the animator integers, plus a FACING SNAP, and nothing else. See the class
        /// comment for the proof that this cannot reach a nav traversal.
        ///
        /// FACING IS PART OF THE STANCE, not a cosmetic extra (measured in-game 2026-08-01): the animator
        /// integers carry NO direction, so a second aim message for a soldier already in the pose changed
        /// nothing visible. The host emitted seq=5..10 alternating targets -39/-95 for actor 1 while the user
        /// cycled with TAB, every mirror applied all six, and not one of them turned — so only the FIRST aim
        /// of a soldier ever looked like it crossed, and every peer still had to re-aim before its shot.
        /// The turn is <c>ActorComponent.SetForward</c>:280 → <c>SetRotation</c>:294 →
        /// <c>SetTransform</c>:299, the exact primitive the game's own facing loop drives per frame
        /// (<c>TacticalNavigationComponent.NoAnimsFace</c>:1046/:1050 — we take the endpoint instead of the
        /// lerp, because the lerp is a coroutine on the nav component and this arc may not touch it). It is
        /// nav-free AND event-free at rest: <c>TacticalActorBase.SetTransform</c>:665-684 raises
        /// <c>TacticalLevel.ActorMoved</c> only inside <c>if (!Utl.Equals(actor.Pos, prevPos))</c>
        /// (<c>TacticalLevelController</c>:1157-1163) and <c>ActorMovedInNewTile</c> only when the VOXEL
        /// changes — a rotation-only write changes neither.
        ///
        /// Skipped when this peer is the one holding the stance natively (its own <c>UIStateShoot</c> already
        /// ran the game's real aim path, and stomping the integers mid-transition would fight it). Every
        /// OTHER reason to skip is TRANSIENT and goes on <see cref="_pending"/> instead of being dropped:
        /// the emitter's self-heal compares against the SHARED table, which already carries the value, so it
        /// falls silent the moment the host echoes and a mirror that dropped the message on arrival would
        /// never hear it again (a soldier still walking when the aim lands is the ordinary case).
        /// </summary>
        private static void ApplyStance(int actorKey, int targetKey)
        {
            if (actorKey == _liveActor && targetKey == _liveTarget) { _pending.Remove(actorKey); return; }
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), actorKey, out why) as TacticalActor;
            var animator = actor == null ? null : actor.Animator;
            if (animator == null)
            {
                if (actor == null && why != null && _loggedFailures.Add(why))
                    Debug.LogWarning("[Multiplayer][tac] aim stance for actor key " + actorKey +
                                     " not applied yet: " + why);
                // A stance we cannot clear on an actor that is not there is moot; a stance we cannot SET is
                // owed to that actor the moment it exists.
                if (targetKey == 0) _pending.Remove(actorKey); else _pending.Add(actorKey);
                return;
            }
            var nav = actor.TacticalNav;
            if (nav != null && nav.IsNavigating) { _pending.Add(actorKey); return; }

            if (targetKey == 0)
            {
                PathProcessorUtils.SetNullNavParams(animator);
                _pending.Remove(actorKey);
                return;
            }

            // Bind the aim clips exactly as the game does one line before it sets these same params
            // (FireWeaponAtTargetCrt:1566 → :1651). Without the override the aim state has no clip.
            var target = TacticalActorKey.Resolve(Tlc(), targetKey, out why);
            var weapon = actor.Equipments == null ? null : actor.Equipments.SelectedWeapon;
            var anims = actor.ActorAnimActions;
            if (target == null || weapon == null || anims == null) { _pending.Add(actorKey); return; }
            var shootAnim = anims.ActivateShootingClips(weapon);
            // The game's own test for "this weapon can aim at all" (GetAimOrPeekPathPoints:549). A weapon
            // with no aim animation is a settled answer, not a transient one — nothing to retry.
            if (shootAnim == null || shootAnim.Aim == null || !shootAnim.Aim.HasAllAnimations)
            {
                _pending.Remove(actorKey);
                return;
            }
            PathProcessorUtils.SetAimParams(animator, AimSegmentType.AimLoop);

            // Flattened like the game's own aim facing (IdleAbility:208 passes ignoreY:true, and
            // NoAnimsFace zeroes y on the same vector); the up axis stays the actor's own.
            var dir = target.Pos - actor.Pos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) actor.SetForward(dir.normalized);
            _pending.Remove(actorKey);
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

        // ─── R2: a peer switching to the soldier lands IN aim mode ─────────

        private static MethodInfo _activateAttack;
        private static bool _activateAttackLooked;

        /// <summary>
        /// Arms on this peer's OWN selection change and fires one frame later, once the top state has
        /// actually become the character screen. Edge-triggered on SELECTION and never on state, deliberately:
        /// re-arming on the state would make Esc unusable — leaving aim pops back to
        /// <c>UIStateCharacterSelected</c>, which would immediately push aim again.
        /// </summary>
        private static void DriveAutoEnter(TacticalViewState state, PhoenixPoint.Tactical.View.TacticalView view)
        {
            // ActivateAttackAbilityState:1466-1502 has THREE silent early-returns (no default shoot ability,
            // an equipment-group weapon, a disabled ability, no valid target) and swallows all of them. One
            // frame later the top state tells us whether it actually pushed aim — this repo's dominant bug
            // shape is a seam that "fired" and changed nothing.
            if (_verifyEnterFor != 0)
            {
                if (!(state is UIStateShoot))
                    Debug.LogWarning("[Multiplayer][tac] shared aim mode did NOT open for actor " + _verifyEnterFor +
                                     ": the game's ActivateAttackAbilityState refused it (no default shoot " +
                                     "ability for the selected weapon, the ability is disabled, or the shared " +
                                     "target is not a valid target on this peer). Top state is " +
                                     (state == null ? "<none>" : state.GetType().Name));
                _verifyEnterFor = 0;
            }

            var sel = view.SelectedActor;
            if (!ReferenceEquals(sel, _lastSelected))
            {
                _lastSelected = sel;
                int key = TacticalActorKey.Of(sel);
                _enterAimFor = (key != 0 && key != _liveActor && Shared(key) != 0) ? key : 0;
                return;
            }
            if (_enterAimFor == 0) return;
            if (state == null || state.GetType().Name != "UIStateCharacterSelected") return;

            int actorKey = _enterAimFor;
            _enterAimFor = 0;
            int targetKey = Shared(actorKey);
            if (targetKey == 0) return;

            string why;
            var target = TacticalActorKey.Resolve(Tlc(), targetKey, out why);
            if (target == null)
            {
                if (why != null && _loggedFailures.Add(why))
                    Debug.LogWarning("[Multiplayer][tac] shared aim target " + targetKey + " unresolvable: " + why);
                return;
            }

            if (!_activateAttackLooked)
            {
                _activateAttackLooked = true;
                // Exact param match, or AccessTools answers null and this silently never binds.
                _activateAttack = AccessTools.Method(state.GetType(), "ActivateAttackAbilityState",
                                                     new[] { typeof(bool), typeof(TacticalActorBase) });
                if (_activateAttack == null)
                    Debug.LogError("[Multiplayer][tac] UIStateCharacterSelected.ActivateAttackAbilityState(bool," +
                                   "TacticalActorBase) not found — a peer switching to an aiming soldier will " +
                                   "open the ordinary character screen instead of landing in aim mode.");
            }
            if (_activateAttack == null) return;
            try
            {
                _activateAttack.Invoke(state, new object[] { false, target });
                _verifyEnterFor = actorKey;
                Debug.Log("[Multiplayer][tac] entering SHARED aim mode for actor " + actorKey +
                          " -> " + targetKey + " on selection");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] entering shared aim mode for actor " + actorKey + " failed: " + ex);
            }
        }
    }
}
