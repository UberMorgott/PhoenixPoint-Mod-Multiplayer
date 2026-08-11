using System.Collections.Generic;
using Base.Core;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Statuses;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// WHO IS DRIVING THIS SOLDIER RIGHT NOW — one per-ACTOR fact, and the two postulate-2 defects it closes.
    ///
    /// THE LEDGER IS ONE HASHSET AND TWO ARM POINTS, because the mod already has exactly two ways an order can
    /// arrive from somebody else:
    ///  • a MIRROR being applied — <c>TacticalCommandSync.ApplyActivate</c> wraps <c>ability.Activate</c> in
    ///    <c>SyncApplyScope</c>, and a mirror is BY CONSTRUCTION never this peer's own click
    ///    (<c>Send(..., _replayOriginPeer)</c> excludes the peer that authored it);
    ///  • the HOST replaying a client's intent — <c>HandleActivate</c> sets
    ///    <c>TacticalCommandSync.ReplayOriginPeer</c> around its own native <c>Activate</c>.
    /// Everything else — the host's own click, a client's speculative play, an engine-raised ambient ability —
    /// is this peer's. No new wire byte, no ownership table to reload, no per-ability code (postulate 3).
    ///
    /// ── DEFECT 1: ONE PEER'S ACTION FROZE EVERY OTHER PEER, AND THE FREEZE IS VANILLA'S ───────────────────
    ///
    /// <c>TacticalActor.ShouldViewWaitForMe</c>:1342-1360 is true for as long as the actor has an executing
    /// ability (<c>TacticalAbility.StartPlayingAction</c>:1015-1018 sets
    /// <c>ShouldForceViewInWaitingState</c> on EVERY ability), and
    /// <c>TacticalView.IsWaitingForActiveAbilitiesAndMapUpdate</c>:864 folds that over
    /// <c>Map.GetActors&lt;TacticalActor&gt;().Any(...)</c> — the WHOLE MAP. <c>UIStateInitial</c>:48 then
    /// switches this peer's view to <c>UIStateWaiting</c> (<c>SetInputState("Waiting")</c>, :20) and
    /// <c>UIStateWaiting.UpdateState</c>:25 refuses to come back while that fold is true.
    ///
    /// In single player that is correct: the only actor executing anything is the one YOU just ordered. In a
    /// shared battle it is a GLOBAL LOCK on a per-actor fact: another human's jetpack flight is an executing
    /// ability on this peer's map, so this peer's whole view parked for the length of somebody else's action.
    /// Measured, from the client's own log of the 22:22:30 build — <c>released this peer's UI from Soldier_3
    /// (a mirrored Move_AbilityDef) — it was held in UIStateWaiting</c>: the release seam of `bb1ca52` found
    /// this peer ALREADY parked, which is the proof the park is upstream of it and not caused by it.
    /// The vanished movement boundaries are the same fact and not a second bug: the ranges are drawn by
    /// <c>UIStateCharacterSelected</c> from <c>ValidMoves</c>:154-160 (:1594-1597), and the park pushes that
    /// state off the stack with <c>ClearStackAndPush</c>.
    ///
    /// So the fold is narrowed to a per-actor question, at the one per-actor function it already calls:
    /// an actor being driven by ANOTHER peer's order is not one THIS peer's view has any business waiting
    /// for. Vanilla answer kept everywhere else — the actor this peer ordered itself still parks it (that
    /// soldier is the one player who legitimately cannot be interrupted), and an AI faction's turn is left
    /// exactly as the game wrote it, because there the wait IS the presentation.
    ///
    /// REACTIVITY (postulate 1): no repaint call is needed and none is added — the release is the game's own
    /// and it is polled. A peer already parked leaves on the next frame through
    /// <c>UIStateWaiting.UpdateState</c>:25, and <c>UIStateInitial</c>:48 then falls through to its
    /// <c>UIStateCharacterSelected</c> branch (:70-79), which re-reads <c>ValidMoves</c> and redraws the
    /// movement boundaries. Anything else would be a hand-rolled state switch — the thing L139 forbids.
    ///
    /// ── DEFECT 2: TWO PEERS ON ONE SOLDIER, AND ONLY ONE DIRECTION WAS ARBITRATED ─────────────────────────
    ///
    /// A CLIENT's second order was already refused: <c>Validate</c>'s <c>actorBusy</c> arm answers "another
    /// peer commanded it first" and <c>IntentRail.Reject</c> nudges it back. The HOST's own click never goes
    /// through that path at all — <c>TacticalViewState.ActivateAbility</c>:270 calls <c>Activate</c> natively —
    /// so host-clicks-second was unarbitrated. That is the report verbatim: client-first + host-second gave
    /// "the character freezes in place, nothing happens, then it teleports", which is a second activation
    /// landing on a busy actor (<c>ShootAbility.Activate</c>:167 /<c>PlayAction(cancelCurrent:true)</c>) and
    /// the host's settle then dragging every peer to where the SECOND order pointed.
    ///
    /// The gate is therefore at the local INPUT seam, not on <c>Activate</c>: <c>TacticalViewState</c>
    /// :259-277 is the single entry every UI state uses (<c>UIStateCharacterSelected</c>:455/:624/:940/:958,
    /// <c>UIStateAbilitySelected</c>:617/:621/:629, and it has no overrides) so ambient engine-raised
    /// abilities — hurt reactions, EndTurn, idle — are untouched. Same predicate as defect 1, same ledger,
    /// both peers: a client clicking a soldier the HOST is driving is refused here too, before its
    /// speculative play can start the wind-up the host will only reject a round trip later.
    ///
    /// THIS IS NOT A BLOCKER IN THE POSTULATE-2 SENSE, and the distinction is the whole reason it is allowed:
    /// it locks ONE ACTOR for the duration of ITS OWN ACTION, and that action ends BY ITSELF — the release is
    /// <c>TacticalActorBase.HasExecutingAbility()</c> going false, i.e. the engine finishing the animation it
    /// started. It never waits on a human decision, there is no acknowledgement to collect, no vote, and an
    /// AFK peer cannot extend it by one frame: an AFK peer issues no orders, so it drives no actor. Every
    /// other soldier stays commandable throughout (that is defect 1's half), and the peer holding the lock
    /// cannot renew it either. Compare the thing the mandate forbids — a gate that stays shut until some
    /// person presses something — and this has none of that shape.
    ///
    /// AND THE REFUSAL IS SAID OUT LOUD (this repo's dominant bug class is the silent swallow): the local
    /// gate toasts through <c>SessionNotifier.ShowToast(modalFallback: true)</c>, which is the same widget the
    /// remote half reaches through <c>IntentRail</c>'s reject nudge — and the remote half now passes
    /// <c>notify: true</c> for exactly this refusal, so BOTH peers of the race see the same sentence instead
    /// of one of them watching a dead click.
    /// </summary>
    internal static class TacticalActorDrive
    {
        /// <summary>Abilities on THIS peer whose current execution was started by another peer's order.
        /// Per-BATTLE (cleared by <see cref="TacticalCommandSync.Reset"/>). A stale entry cannot wedge
        /// anything: every reader first asks the ENGINE whether the ability is still executing, so an entry
        /// for an ability that never started or already ended is invisible.</summary>
        private static readonly HashSet<TacticalAbility> _foreign = new HashSet<TacticalAbility>();

        internal static void Reset() => _foreign.Clear();

        /// <summary>The classification, pure so RailCheck L145 can execute it rather than read its IL. Two
        /// inputs and no more, and they are the mod's only two doors for somebody else's order.</summary>
        internal static bool ActivationIsForeign(bool underMirrorApply, bool hostIsReplayingAPeer) =>
            underMirrorApply || hostIsReplayingAPeer;

        /// <summary>THE OUTCOME L145 asserts: must this peer's view park itself for this actor? Pure.
        /// <paramref name="nativeSaysWait"/> is the game's own answer, and every arm that is not the shared
        /// battle keeps it verbatim — an AI turn, a status that forces the wait, and this peer's own soldier.
        /// TRUE for an actor another peer is driving is the defect: that is the global lock.</summary>
        internal static bool ViewMustWaitFor(bool nativeSaysWait, bool playerFactionIsPlayingTurn,
                                             bool drivenByAnotherPeer, bool aStatusForcesTheWait) =>
            nativeSaysWait && (!playerFactionIsPlayingTurn || !drivenByAnotherPeer || aStatusForcesTheWait);

        /// <summary>THE OUTCOME L146 asserts: may THIS peer's click start an order on this actor? Pure.
        /// First-come-first-served in one line — and the release is <paramref name="actorIsExecuting"/>
        /// going false, which is the engine's own state and not a timer of ours.</summary>
        internal static bool PeerMayCommand(bool actorIsExecuting, bool executionBelongsToAnotherPeer) =>
            !(actorIsExecuting && executionBelongsToAnotherPeer);

        internal static void MarkForeign(TacticalAbility ability, bool foreign)
        {
            if (ability == null) return;
            if (foreign) _foreign.Add(ability);
            else _foreign.Remove(ability);   // ability instances are reused: a local re-activation must clear
        }

        internal static void ReleaseDrive(TacticalAbility ability)
        {
            if (ability != null) _foreign.Remove(ability);
        }

        /// <summary>Is the order this actor is executing RIGHT NOW another peer's? Grounded in the engine:
        /// <c>HasExecutingAbility</c> (<c>TacticalActorBase</c>:695-704, which already ignores
        /// <c>IdleAbility</c>) is the "is executing" test, and the ledger only says whose. An order that is
        /// merely QUEUED is not executing and therefore owns nothing — deliberately, because a mirror can sit
        /// in the queue for its 10 s ceiling and a lock that outlives its action is a blocker.</summary>
        internal static bool DrivenByAnotherPeer(TacticalActorBase actor)
        {
            if (actor == null || _foreign.Count == 0) return false;
            if (!actor.HasExecutingAbility()) return false;
            var executing = actor.ExecutingAbilities;
            if (executing == null) return false;
            for (int i = 0; i < executing.Count; i++)
                if (_foreign.Contains(executing[i])) return true;
            return false;
        }

        /// <summary>The OTHER half of <c>ShouldViewWaitForMe</c>:1351-1358 — a status may force the wait on its
        /// own, with no ability executing at all. Read here so the narrowing can leave that arm alone instead
        /// of guessing that abilities are the only reason the game said yes.</summary>
        private static bool AStatusForcesTheWait(TacticalActorBase actor)
        {
            var comp = actor == null ? null : actor.Status;
            if (comp == null) return false;
            foreach (var s in comp.Statuses)
            {
                var tac = s as TacStatus;
                if (tac != null && tac.ShouldForceViewInWaitingState) return true;
            }
            return false;
        }

        private static bool InSharedBattle()
        {
            var engine = NetworkEngine.Instance;
            return engine != null && engine.IsActiveSession;
        }

        private static TacticalLevelController Tlc() => TacticalDamageSync.Tlc();

        /// <summary>Is a PLAYER-controlled faction mid-turn on this peer? The narrowing is confined to the
        /// shared player turn: during an AI faction's turn the wait is the presentation of a side nobody
        /// commands, and unparking it would let a player click through the enemy's move.</summary>
        private static bool PlayerFactionIsPlayingTurn()
        {
            var tlc = Tlc();
            var faction = ReferenceEquals(tlc, null) ? null : tlc.CurrentFaction;   // L113: never Unity ==
            return faction != null && faction.IsControlledByPlayer && faction.IsPlayingTurn;
        }

        // ─── the live seam behind the pure decisions ───────────────────────

        internal static bool ViewMustWaitFor(TacticalActor actor, bool nativeSaysWait)
        {
            if (!nativeSaysWait || !InSharedBattle()) return nativeSaysWait;
            return ViewMustWaitFor(true, PlayerFactionIsPlayingTurn(),
                                   DrivenByAnotherPeer(actor), AStatusForcesTheWait(actor));
        }

        /// <summary>The local-input verdict, plus the sentence the refused player is shown. Null = go ahead.
        /// </summary>
        internal static string RefuseLocalCommand(TacticalAbility ability)
        {
            if (ability == null || !InSharedBattle()) return null;
            var actor = ability.TacticalActorBase;
            if (actor == null) return null;
            if (PeerMayCommand(actor.HasExecutingAbility(), DrivenByAnotherPeer(actor))) return null;
            return (actor.name ?? "that soldier") + ": " + TacticalCommandSync.BusyRefusal;
        }
    }

    /// <summary>WHOSE ORDER THIS ACTIVATION IS, decided at the one instruction every order passes. A PREFIX,
    /// so the mark is in place before <c>StartPlayingAction</c> can put the ability into
    /// <c>ExecutingAbilities</c> — <c>PlayingAction.SetState(Playing)</c> runs SYNCHRONOUSLY inside
    /// <c>Activate</c> (<c>PlayingAction</c>:47-53 → <c>TacticalActorBase.AddExecutingAbility</c>:709), so a
    /// postfix would leave a whole activation's worth of frames answering "mine".</summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.Activate), new[] { typeof(object) })]
    internal static class AbilityDriveOrigin
    {
        private static void Prefix(TacticalAbility __instance)
            => TacticalActorDrive.MarkForeign(
                   __instance,
                   TacticalActorDrive.ActivationIsForeign(SyncApplyScope.Active,
                                                          TacticalCommandSync.ReplayOriginPeer != 0));
    }

    /// <summary>The release, on the game's own end-of-action edge — the SAME method
    /// <see cref="AbilityActionEndCapture"/> uses, because that is where the engine itself considers the
    /// order over.</summary>
    [HarmonyPatch]
    internal static class AbilityDriveEnd
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TacticalAbility), "ClearPlayingAction",
                               new[] { typeof(Base.Entities.PlayingAction) });

        private static void Postfix(TacticalAbility __instance) => TacticalActorDrive.ReleaseDrive(__instance);
    }

    /// <summary>DEFECT 1's ONE SEAM: the global "is anybody busy" fold, narrowed to the per-actor question it
    /// was always asking. Postfix and never a prefix — the game's own answer is the input, and every arm this
    /// mod has no opinion about keeps it verbatim.</summary>
    [HarmonyPatch(typeof(TacticalActor), nameof(TacticalActor.ShouldViewWaitForMe))]
    internal static class OnePeersActionDoesNotParkAnother
    {
        private static void Postfix(TacticalActor __instance, ref bool __result)
            => __result = TacticalActorDrive.ViewMustWaitFor(__instance, __result);
    }

    // DEFECT 2's SEAM IS STILL TacticalViewState.ActivateAbility — but it is ONE prefix now, not two.
    // BusyActorBelongsToThePeerThatStartedIt used to patch that method a SECOND time, unprioritised, alongside
    // ClickedOrderWaitsForTheEcho: Harmony stops at the first prefix that returns false and the order between
    // two unprioritised patch classes is undeclared, so on any given load exactly one of the local-drive gate
    // and the echo publish silently did not run. RefuseLocalCommand above is now consulted at the top of
    // TacticalCommandSync.PublishClickedOrder, which is that one prefix — same seam, same verdict, same toast,
    // and the refusal now shares the single ReleaseLocalUiHolding seam so the refused player's screen comes
    // back (L231) instead of staying in the targeting state his suppressed click never left.
}
