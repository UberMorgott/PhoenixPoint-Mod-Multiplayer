using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Core;
using Base.Entities;
using Base.Utils.Maths;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Entities.Statuses;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.UI;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE capture seam (law 4a), on the ONE generic funnel: <c>TacticalAbility.Activate(object)</c>
    /// (<c>TacticalAbility</c>:1078). On the BASE method, so the single patch covers every derived ability
    /// that calls <c>base.Activate</c> — which every rider does, and RailCheck L65-rider keeps proving.
    ///
    /// A PREFIX, and the ordering is the point, not a detail. A3a deliberately lets the acting peer's own
    /// click PLAY locally (law 5's speculative presentation: the closer is the authority, so there is no
    /// rewind engine to build) — but the ORDER still leaves before the local mutation, exactly where a
    /// block-first family would put its block. A postfix would emit AFTER the native write and be a
    /// result-ship (RailCheck L19), which is a real distinction and not a naming one: from here the wire sees
    /// the command at the same instant the local actor does, so the other peers start the same move on the
    /// same frame instead of one round trip behind the animation. The prefix returns void, which Harmony
    /// treats as never-skipping — the native body always runs.
    ///
    /// Parameter types are named EXACTLY: <c>AccessTools</c>/<c>HarmonyPatch</c> do no widening and skip no
    /// optional parameter, and a mistyped guess resolves to null, which <c>PatchAll</c> turns into one warning
    /// <c>MultiplayerMain</c> swallows — killing every later patch in the same pass (RailCheck L23).
    /// </summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.Activate), new[] { typeof(object) })]
    internal static class AbilityActivateCapture
    {
        /// <summary>SECOND, behind <c>AbilityDriveOrigin</c> (<c>Priority.First</c>) — see the reason there.
        /// The order is not load-bearing today; it is declared so that it stops being a coin flip.</summary>
        [HarmonyPriority(Priority.Normal)]
        private static void Prefix(TacticalAbility __instance, object parameter)
            => TacticalCommandSync.OnAbilityActivated(__instance, parameter);
    }

    /// <summary>
    /// A9's ONE SEAM (law L230): <c>TacticalViewState.ActivateAbility</c>:259 — the single method every
    /// PLAYER CLICK passes through, and the only one that can tell a click apart from the engine's own
    /// activations without enumerating abilities. Blocking here and not at <c>TacticalAbility.Activate</c> is
    /// forced, not preferred: <c>Activate</c> is VIRTUAL, so a prefix that skips the base body still lets
    /// <c>ShootAbility.Activate</c>:165-174 run its own <c>PlayAction(Shoot)</c> — the same reason
    /// <see cref="AutonomousReactionExecuteGate"/> sits on the non-virtual <c>Execute</c> wrappers. This is the
    /// caller, so returning false suppresses the whole activation.
    ///
    /// It covers every clicked action at once because the game funnels them all here: <c>UIStateShoot</c>
    /// (the one override in the game, and it calls base), <c>UIStateFreeCam</c>:464 free-aim,
    /// <c>UIStateFirstPersonMultiTargetSelection</c>, <c>UIStateOverwatchAbilitySelected</c>,
    /// <c>UIStateAbilitySelected</c> (every def-driven ability: grenades, cones, throws, alien specials) and
    /// <c>UIStateCharacterSelected</c> (melee, reload, move).
    ///
    /// SUPPRESSING THE STATE SWITCH TOO IS DELIBERATE. The native body also leaves the targeting state for
    /// <c>UIStateWaiting</c>; letting the view park for an ability that has not started would show a wait for
    /// nothing. The release happens on the mirror instead, where it already lives —
    /// <see cref="TacticalCommandSync.ReleaseLocalUiHolding"/> runs from <c>ApplyActivate</c> BEFORE the
    /// engine takes the actor, and a second click in the meantime is dropped by the echo gate itself.
    ///
    /// <c>Prepare</c> rather than a null <c>TargetMethod</c>: <c>AccessTools.Method</c> does EXACT parameter
    /// matching, a returned null aborts <c>PatchAll</c> and kills every later patch in the pass (RailCheck
    /// L23), and a silent skip is this repo's dominant bug class. It says so and stands down.
    /// </summary>
    [HarmonyPatch]
    internal static class ClickedOrderWaitsForTheEcho
    {
        internal static readonly MethodBase Seam = AccessTools.Method(
            typeof(PhoenixPoint.Tactical.View.TacticalViewState), "ActivateAbility",
            new[] { typeof(TacticalAbility), typeof(TacticalAbilityTarget), typeof(Base.UI.StateStackAction),
                    typeof(Func<TacticalAbility, bool>) });

        private static bool Prepare()
        {
            if (Seam != null) return true;
            Debug.LogError("[Multiplayer][tac] ECHO SEAM NOT BOUND — TacticalViewState.ActivateAbility" +
                           "(TacticalAbility, TacticalAbilityTarget, StateStackAction, Func<TacticalAbility,bool>) " +
                           "did not resolve, so every clicked order will play LOCALLY at the click again and " +
                           "attack animations will start at a different moment on every peer (law L230).");
            return false;
        }

        private static MethodBase TargetMethod() => Seam;

        private static bool Prefix(TacticalAbility ability, TacticalAbilityTarget target)
            => !TacticalCommandSync.PublishClickedOrder(ability, target);
    }

    /// <summary>
    /// L83 — THE REACTION GATE. A non-host peer does not raise its own overwatch / return fire /
    /// zone-of-control / synced shot: the host raises all four and mirrors them on 0x82 like any other action,
    /// so a locally-raised one would be a second shot from the same actor. See
    /// <see cref="TacticalCommandSync.IsAutonomous"/> for the measurement that made the host the authority
    /// here, and for why the block sits on these two NON-VIRTUAL wrappers rather than on the virtual
    /// <c>Activate</c> the capture uses (skipping a virtual's base body leaves the override's own
    /// <c>PlayAction</c> running).
    ///
    /// Two patch classes and not one <c>TargetMethods</c>: the wrappers return different types, so each skip
    /// has to hand Harmony a different <c>__result</c> — an empty enumerator for the coroutine form (every
    /// caller wraps it in <c>Timing.Call</c>, which completes immediately) and <c>NextUpdate.ThisFrame</c> for
    /// the immediate form, which is exactly what the native body returns when nothing began (:1170).
    /// </summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.Execute), new[] { typeof(object) })]
    internal static class AutonomousReactionExecuteGate
    {
        private static IEnumerator<NextUpdate> Nothing() { yield break; }

        private static bool Prefix(object parameter, ref IEnumerator<NextUpdate> __result)
        {
            if (!TacticalCommandSync.BlockAutonomousReaction(parameter)) return true;
            __result = Nothing();
            return false;
        }
    }

    /// <summary>The immediate half of <see cref="AutonomousReactionExecuteGate"/> —
    /// <c>MassShootTargetActorEffect.FaceAndShootAtTarget</c>:77 is the one raiser that uses it.</summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.ExecuteAndWait), new[] { typeof(object) })]
    internal static class AutonomousReactionExecuteAndWaitGate
    {
        private static bool Prefix(object parameter, ref NextUpdate __result)
        {
            if (!TacticalCommandSync.BlockAutonomousReaction(parameter)) return true;
            __result = NextUpdate.ThisFrame;
            return false;
        }
    }

    /// <summary>
    /// A7 — THE SECOND TACTICAL FUNNEL. Switching a soldier's weapon is NOT an ability, so nothing about it
    /// reaches <see cref="AbilityActivateCapture"/>: the model write is
    /// <c>EquipmentComponent.SetSelectedEquipment</c>:242-268, clicked straight out of three view states
    /// (<c>UIStateCharacterSelected</c>:748/751, <c>UIStateShoot</c>:854/862,
    /// <c>UIStateAbilitySelected</c>:725/736). Until this seam existed, a weapon switch stayed on the peer
    /// that clicked it, and — far worse than a cosmetic gap — the HOST then refused that peer's next order
    /// with the game's own <c>EquipmentNotSelected</c> gate
    /// (<c>TacticalAbility.GetDisabledStateInternal</c>:435 tests <c>isEquipmentOfSelectedGroup</c>:481-499
    /// against the HOST's selection), which is the 2026-07-31 "threw a grenade, nothing happened, everything
    /// went dead" report.
    ///
    /// A PREFIX so the capture reads the OLD selection and can tell a real change from the native no-op
    /// (:244 returns immediately when the value is unchanged). It returns void — never blocks — for the
    /// reasons argued at <see cref="TacticalCommandSync.OnEquipmentSelected"/>.
    /// </summary>
    [HarmonyPatch(typeof(EquipmentComponent), nameof(EquipmentComponent.SetSelectedEquipment))]
    internal static class EquipmentSelectCapture
    {
        private static void Prefix(EquipmentComponent __instance, Equipment equipment)
            => TacticalCommandSync.OnEquipmentSelected(__instance, equipment);
    }

    /// <summary>
    /// THE closer seam, on the ONE generic action-END funnel: <c>TacticalAbility.ClearPlayingAction</c>
    /// (:1039). Chosen over <c>OnPlayingActionEnd</c> because that one is VIRTUAL — a derived override that
    /// forgot to call base would silently remove the closer — while this is the non-virtual method that calls
    /// it, reached by every ability, for a completed action AND for a cancelled one.
    ///
    /// Host-only inside: the client's own action ends produce nothing authoritative. By this point the move's
    /// navigation has finished and its AP has been charged (<c>TacticalNavigationComponent</c>:800 runs inside
    /// <c>Navigate</c>, which <c>MoveAbility.Move</c>:119 awaits before the action can end), so the values read
    /// here are final.
    /// </summary>
    [HarmonyPatch]
    internal static class AbilityActionEndCapture
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TacticalAbility), "ClearPlayingAction",
                               new[] { typeof(Base.Entities.PlayingAction) });

        private static void Postfix(TacticalAbility __instance)
            => TacticalCommandSync.OnAbilityActionEnded(__instance);
    }

    /// <summary>
    /// A3b — THE FUMBLE IS THE HOST'S, AND IT RIDES WITH THE ORDER (law L66d).
    ///
    /// WHY IT CANNOT BE SHIPPED AFTERWARDS. <c>TacticalAbility.Activate</c>:1109 rolls
    /// <c>FumbledAction = FumbleActionCheck()</c> off the GLOBAL <c>UnityEngine.Random</c>
    /// (<c>FumbleActionCheck</c>:1124-1131, <c>Random.Range(0,100) &lt; EquipmentDef.FumblePerc</c>), and the
    /// value is CONSUMED inside the same synchronous call — <c>PlayAction</c>:988-993 diverts to
    /// <c>PlayFumbleAction</c>, and with TFTV installed <c>EnqueueAction</c> does too
    /// (<c>TFTVVanillaFixes</c>:4003-4033, the fix that makes shoot fumbles actually fire; vanilla no-ops
    /// them). By the time any later message could arrive, the shot has already been queued. So the bit has to
    /// be IN the order, which means the host has to know it BEFORE the native body runs.
    ///
    /// THE MECHANISM, and it is deliberately RNG-neutral: the host's capture prefix consumes the ONE native
    /// roll early (<see cref="FumbleGate.RollForHost"/> calls the real method through this very patch, which
    /// finds nothing pending and lets the original run) and MEMOIZES it; the native call at :1109 then finds
    /// the memo and returns it without a second draw. Exactly one roll per activation, same as vanilla.
    /// Every non-host peer NEVER rolls: it returns the host's declared bit if the order carried one, and
    /// false otherwise — a client that rolled its own would fumble on a shot the host landed.
    /// <c>JetJumpAbility</c>:136-146 is the only override of the check; it is not a rider, so it is left to
    /// roll natively on the host and to return false on a client like everything else.
    /// </summary>
    internal static class FumbleGate
    {
        private static readonly Dictionary<TacticalAbility, bool> _pending = new Dictionary<TacticalAbility, bool>();
        private static MethodInfo _check;

        internal static void Reset() => _pending.Clear();

        /// <summary>HOST: take the native roll NOW and memoize it for <c>Activate</c>:1109.</summary>
        internal static bool RollForHost(TacticalAbility ability)
        {
            if (ability == null) return false;
            if (_check == null)
            {
                _check = AccessTools.Method(typeof(TacticalAbility), "FumbleActionCheck", new Type[0]);
                if (_check == null)
                {
                    Debug.LogError("[Multiplayer][tac] TacticalAbility.FumbleActionCheck did not resolve — the " +
                                   "fumble cannot be pre-rolled, so it will not ride with the order and every " +
                                   "peer will roll its own. Shots will differ between screens.");
                    return false;
                }
            }
            bool rolled;
            try { rolled = (bool)_check.Invoke(ability, null); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] fumble pre-roll THREW — the order ships 'not fumbled' and the " +
                               "host may still fumble: " + ex);
                return false;
            }
            _pending[ability] = rolled;
            return rolled;
        }

        /// <summary>MIRROR: the host's answer for the activation that is about to run.</summary>
        internal static void Declare(TacticalAbility ability, bool fumbled)
        {
            if (ability != null) _pending[ability] = fumbled;
        }

        internal static bool TryConsume(TacticalAbility ability, out bool value)
        {
            value = false;
            if (ability == null || !_pending.TryGetValue(ability, out value)) return false;
            _pending.Remove(ability);
            return true;
        }
    }

    [HarmonyPatch]
    internal static class FumbleCheckGate
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TacticalAbility), "FumbleActionCheck", new Type[0]);

        private static bool Prefix(TacticalAbility __instance, ref bool __result)
        {
            if (FumbleGate.TryConsume(__instance, out __result)) return false;   // memoized host roll / declared mirror bit
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;   // solo or host: roll natively
            __result = false;   // a client never draws from the global RNG; the host's bit is authoritative
            return false;
        }
    }

    /// <summary>
    /// THE MIRRORED SHOT THAT WAITED ITS TURN (law L78). <c>ShootAbility.Activate</c>:167 chooses between
    /// <c>PlayAction</c> (now) and <c>EnqueueAction(soloAfterCurrent: true)</c> (after whatever is already
    /// playing, and alone), and one arm of that condition is
    /// <c>TacticalLevelController.AnyAIEvaluationAbilityExecuting</c>:259 —
    /// <c>_aiEvaluationUpdateable != null</c>. On the HOST a mirrored order runs inside its own AI turn, so
    /// the flag is TRUE and the shot plays immediately. On a CLIENT the AI turn is held (A5's
    /// <c>ClientAiGate</c>), that field is null, and the SAME order takes the queue instead — which is
    /// exactly the reported "the other screens only start playing it once my animation has finished, and
    /// then everything at once". The peers never disagreed about the order; they disagreed about this getter.
    ///
    /// So answer it the way the host would, and ONLY while a mirrored activation is genuinely on the stack:
    /// <c>SyncApplyScope</c> is entered at the single mirror call site (<c>PlayMirroredCommand</c>, the
    /// <c>using</c> around <c>ability.Activate</c>) and closes with that synchronous call. Scoped that
    /// tightly, the flag's two other readers cannot observe the lie — <c>ExecuteAIEvaluationAbilities</c>:1236
    /// and <c>AnyGlobalEffectExecuting</c>:267 are reached from coroutine drivers, never from inside one
    /// synchronous <c>Activate</c>. A postfix, not a prefix: when the client legitimately has an evaluation
    /// running the native answer is already TRUE and must survive.
    ///
    /// NARROWED TO THE AI TURN (law L104, 2026-08-05), because "match the host" is only PlayAction while the
    /// host is actually running its AI. During a PLAYER turn the host's own answer here is FALSE — its
    /// <c>_aiEvaluationUpdateable</c> is null — so a blanket lie made a WATCHER the only peer taking
    /// <c>PlayAction(cancelCurrent: true)</c> while the acting peer and the host both took
    /// <c>EnqueueAction(soloAfterCurrent: true)</c>. That is the second half of "I move behind a wall and
    /// immediately shoot, and the other windows do it noticeably faster than mine": a watcher CANCELLED the
    /// move mid-walk and fired at once, while the peer who clicked correctly finished the walk first. It was
    /// not merely faster, it was wrong — cancelling a move leaves that peer at a position the order never
    /// reached until the settle drags it back (the same hazard law 5 spells out for held melee orders).
    /// The narrowing is derived from REPLICATED state (whose turn it is), so every peer computes the same
    /// answer for the same order without a byte on the wire.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController),
                  nameof(TacticalLevelController.AnyAIEvaluationAbilityExecuting), MethodType.Getter)]
    internal static class MirroredPlayMatchesHostPacing
    {
        private static void Postfix(TacticalLevelController __instance, ref bool __result)
        {
            if (__result || !SyncApplyScope.Active) return;
            var faction = __instance == null ? null : __instance.CurrentFaction;
            if (faction != null && faction.IsControlledByAI) __result = true;
        }
    }

    /// <summary>
    /// THE AIM BRANCH, ONE LAYER BELOW THE CAMERA WAIT (law L104(j), 2026-08-05).
    ///
    /// MEASURED, not argued: order skew between peers is negligible (actor→host +18 ms, →peer2 +28 ms), and
    /// there is no confirmation round trip to blame — <c>TacticalViewState.ActivateAbility</c>:270 calls
    /// <c>ability.Activate</c> synchronously, and <c>HOST mission END outcome=Won</c> reached the KILLER
    /// FIRST (+15 ms) against the non-killer's +37 ms. What differs is a 495-502 ms block (778-781 ms on a
    /// heavy weapon) that mirror peers play and the acting peer skips, or the reverse: "in some windows
    /// someone fires half a second earlier", and run-then-shoot makes observers fire INSTANTLY while the
    /// acting peer plays the full aim.
    ///
    /// <c>TacticalLevelController</c>:1645 gates that entire block (:1647-1678) on
    /// <c>TacticalActor.CurrentlyAiming</c> — which is
    /// <c>Animator.GetInteger("TravelType") == 7 || Animator.GetInteger("ShootSegmentType") == 5</c>
    /// (TacticalActor.cs:228). A LOCAL ANIMATOR INTEGER. It is not in the order, it cannot be, and two peers
    /// answering it differently take opposite sides of a half-second branch for the same shot.
    /// <c>TacticalAimPoseSync</c>:361 makes that certain rather than likely — it defers ANY stance message,
    /// including the CLEAR, while <c>nav.IsNavigating</c>, so a mirror still walking keeps
    /// <c>SetAimParams(AimLoop)</c> (:385) and fires with no wind-up at all, while :346 does not exempt the
    /// emitter and the acting peer's own clear writes <c>SetNullNavParams</c> (:365) onto its own soldier,
    /// which then plays the FULL wind-up.
    ///
    /// SO THE FIX IS NOT IN THE STANCE TABLE. A table that defers while walking can never be authoritative at
    /// fire time; papering over it there would move the race, not end it. Instead the branch is forced to ONE
    /// answer under a relayed activation — exactly the shape <see cref="MirroredPlayMatchesHostPacing"/>
    /// already uses one layer up, and armed at the same two points as the L104 camera token. Universal, zero
    /// wire bytes, and it demotes the aim table back to what it is: cosmetic.
    ///
    /// THE ACCEPTED COST, stated rather than hidden: forcing FALSE means every peer PLAYS the wind-up, so a
    /// player who was already holding aim on a target loses vanilla's instant follow-up shot. That is the
    /// price of one answer; forcing TRUE would be the opposite bug (nobody ever aims) and is worse.
    ///
    /// MISSION STATISTICS ARE THE SAME RULE, NOT A SECOND PATCH. The 2-3 s late summary on the peer that made
    /// the killing blow is its own presentation queue being longer — its shot plus the kill cinematic that
    /// <see cref="TacticalCameraPolicy"/> suppresses on watchers — and the native summary waits on
    /// <c>TacticalView.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate</c>, already named by L104(f).
    /// Shortening the shot to one shared length shortens that queue on every peer alike.
    /// </summary>
    [HarmonyPatch(typeof(TacticalActor), nameof(TacticalActor.CurrentlyAiming), MethodType.Getter)]
    internal static class RelayedAimBranchIsTheSameOnEveryPeer
    {
        private static void Postfix(TacticalActor __instance, ref bool __result)
        {
            if (!__result) return;                                    // already the forced answer
            if (TacticalCommandSync.UnderRelayedAim(__instance)) __result = false;
        }
    }

    /// <summary>
    /// THE MIRRORED ORDER THAT WAITED FOR THE WRONG PEER'S CAMERA. <c>MirroredPlayMatchesHostPacing</c> above
    /// only decides PlayAction vs EnqueueAction — but BOTH paths wrap the action in
    /// <c>CreateWaitingForCameraBlendingAction</c>, and <c>WaitingForCameraBlendingAction</c>:969-974 then
    /// spins in <c>WaitForCameraChase</c>:952-966 while <c>CameraDirector.Chasing</c>, for every ability with
    /// <c>TrackWithCamera</c>. So no choice between those two branches could ever have fixed it.
    ///
    /// That wait is the wrong gate for a mirror, for a reason that does not depend on any bug report:
    /// <c>Chasing</c> is <c>PlanarScrollCamera.IsDoingChase</c>:256 — ONE GLOBAL camera state per peer, not
    /// per actor — so a replicated action's start time was being decided by where THIS peer's camera happens
    /// to be pointing. Law 5 names camera local-only and never relayed; a local-only thing must not decide
    /// WHEN a shared action begins. It is also the only mechanism in this arc that can make two receivers of
    /// the SAME order start at different times, which a per-actor action queue cannot.
    ///
    /// Skipping it costs nothing but the camera blend: the coroutine is a pure wait, so the mirrored action
    /// simply starts now.
    ///
    /// AND THE ACTING PEER TAKES THE SAME EXEMPTION (law L104, 2026-08-05). Leaving its own click on the
    /// native wait did not make it "single player" — it made it the SLOW one, because the acting peer is the
    /// peer holding that soldier selected and therefore the ONLY peer whose camera hint survives
    /// <c>TacticalCameraPolicy.AllowAbilityHint</c>. Every watcher started the shot immediately and the peer
    /// who clicked watched its own camera fly in first: "on the other windows this happens noticeably faster
    /// than on mine". The token is now armed in <see cref="TacticalCommandSync.OnAbilityActivated"/> for every
    /// RELAYED activation as well, so a shared action begins at the moment the order exists on all peers
    /// alike, and this class's name is now half the story — it is the ANCHOR, not a mirror concession.
    /// </summary>
    [HarmonyPatch(typeof(TacticalAbility), "WaitForCameraChase")]
    internal static class MirroredPlayDoesNotWaitForThisPeersCamera
    {
        private static bool Prefix(TacticalAbility __instance, ref IEnumerator<NextUpdate> __result)
        {
            if (!TacticalCommandSync.ConsumeCameraWaitSkip(__instance)) return true;
            __result = NoWait();
            return false;
        }

        private static IEnumerator<NextUpdate> NoWait() { yield break; }
    }

    /// <summary>THE STANDING HALF of the local-UI release (<see cref="TacticalCommandSync.MovePollMustBeWithheld"/>
    /// carries the reasoning). ONE seam for every caller, sited on the engine's own error line rather than on
    /// the <c>UIStateCharacterSelected.ValidMoves</c>:153-160 that happened to be the reported one — the game
    /// already answers <c>null</c> there whenever the move ability is not enabled, so an empty sweep is a value
    /// its callers were always written to receive.</summary>
    [HarmonyPatch(typeof(MoveAbility), nameof(MoveAbility.GetTargetsData))]
    internal static class MoveRangeIsNotSweptWhileAnotherPeerDrivesTheActor
    {
        private static readonly MoveAbilityTargetData[] Nothing = new MoveAbilityTargetData[0];

        // ONE LINE PER EPISODE, not per frame: the withholding lasts as long as the other peer's order and the
        // poll is once a frame, so an undeduplicated record would bury the log it is meant to explain. Removed
        // on the first sweep that runs again, which is the order ending — so a second order logs a second line.
        private static readonly HashSet<TacticalActorBase> _withheld = new HashSet<TacticalActorBase>();

        private static bool Prefix(MoveAbility __instance, ref IEnumerable<MoveAbilityTargetData> __result)
        {
            TacticalActor actor;
            try { actor = __instance == null ? null : __instance.TacticalActor; }
            catch { return true; }   // a presentation gate alters NOTHING when it cannot answer (P4c)
            if (actor == null) return true;
            if (!TacticalCommandSync.SweepIsWithheldFor(__instance))
            {
                _withheld.Remove(actor);
                return true;
            }
            if (_withheld.Add(actor))
                Debug.Log("[Multiplayer][tac] move-range sweep WITHHELD for " + SafeName(actor) +
                          " while another peer's order drives him — the game's own GetTargetsData says it " +
                          "must not run now (it invalidates the situation cache and turns the static " +
                          "NavigationSettings.PathRequestPostProcess off mid-navigation), and it says so " +
                          "without stopping. His move overlay is blank until that order ends; every other " +
                          "soldier is unaffected and nothing else about this peer's screen changes.");
            __result = Nothing;
            return false;
        }

        private static string SafeName(TacticalActorBase actor)
        {
            try { return actor.name; } catch { return "<an actor>"; }
        }
    }

    /// <summary>THE OTHER HALF OF THE WITHHOLD, and the half that keeps Unity's coroutine chain alive
    /// (<see cref="TacticalCommandSync.MoveOverlayMustNotSeeNull"/> carries the full reasoning).
    ///
    /// The sibling above answers an EMPTY sweep, which the engine immediately reads as
    /// <c>AbilityDisabledState.NoValidTarget</c> (<c>MoveAbility</c>:26 → <c>TacticalAbility</c>:465-468) — so
    /// <c>ValidMoves</c>:69-79 starts answering <c>null</c> to a coroutine that already passed its only guard
    /// (<c>UpdateMoveAreas</c>:223) and re-reads the property after every yield (:237, :243, :253, :259). One
    /// null there is an <c>ArgumentNullException</c> out of <c>Enumerable.Where</c> and Unity's
    /// <c>Broken coroutine call chain</c>, which aborts the chain for the rest of the battle. This postfix hands
    /// that read an EMPTY list instead, so the coroutine finishes normally having drawn nothing.
    ///
    /// A POSTFIX ON THE GETTER, not a rewrite of it: the engine's own null is left alone everywhere else, and
    /// the getter is the one place ALL of those reads route through — so this covers every path that takes this
    /// peer's UI off an actor mid-sweep (any mirrored ability's release, the forced settle's release, and the
    /// standing L168 case where the player merely re-selected), not the mirrored Move that was reported.</summary>
    [HarmonyPatch(typeof(MoveAbilitySceneViewElement), "ValidMoves", MethodType.Getter)]
    internal static class TheMoveOverlayIsNeverHandedANullSweep
    {
        // NEVER a fabricated target: an empty list draws nothing, a populated one would paint move tiles the
        // player cannot use and TacticalActorDrive.RefuseLocalCommand would refuse (L146). L310 arm (d).
        private static readonly List<MoveAbilityTargetData> Empty = new List<MoveAbilityTargetData>();

        // ONE LINE PER EPISODE, like the withhold's own: the getter is read several times a frame and the
        // withholding lasts as long as the other peer's order. Cleared on the first non-null answer, which is
        // that order ending — so a second order logs a second line.
        private static readonly HashSet<TacticalActor> _fed = new HashSet<TacticalActor>();

        private static void Postfix(MoveAbilitySceneViewElement __instance,
                                    ref List<MoveAbilityTargetData> __result)
        {
            MoveAbility ability;
            TacticalActor actor;
            try
            {
                ability = __instance == null ? null : __instance.GetActorMoveAbility;
                actor = ability == null ? null : ability.TacticalActor;
            }
            catch { return; }   // a presentation gate alters NOTHING when it cannot answer (P4c)
            if (actor == null) return;
            if (!TacticalCommandSync.MoveOverlayMustNotSeeNull(TacticalCommandSync.SweepIsWithheldFor(ability),
                                                               __result == null))
            {
                if (__result != null) _fed.Remove(actor);
                return;
            }
            __result = Empty;
            if (_fed.Add(actor))
                Debug.Log("[Multiplayer][tac] move overlay fed an EMPTY sweep for " + SafeName(actor) +
                          " instead of the null the game answers while his move ability is disabled — the " +
                          "withheld sweep is what disables it (MoveAbility:26 -> TacticalAbility:465-468), and " +
                          "MoveAbilitySceneViewElement.UpdateMoveAreas re-reads ValidMoves after its yields " +
                          "(:237, :243, :253, :259) with only one guard at :223, so the null landed in " +
                          "Enumerable.Where and Unity aborted the whole coroutine chain. His overlay draws " +
                          "nothing until that order ends; nothing else on this screen changes.");
        }

        private static string SafeName(TacticalActorBase actor)
        {
            try { return actor.name; } catch { return "<an actor>"; }
        }
    }
}
