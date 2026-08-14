using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// A LOOT CONTAINER'S INVENTORY OPENS ON THE PEER WHOSE SOLDIER WALKED UP TO IT, AND ON NO OTHER.
    ///
    /// THE REPORT (2026-08-06, ambush mission). A CLIENT's soldier walked up to a crate and the container
    /// window opened ON THE HOST — EMPTY, showing what looked like the host's own soldier's kit. It happened in
    /// reverse too. Then heavy lag, an error on the client, and the mission froze on the enemy turn.
    ///
    /// NOTHING WE RELAY DOES THIS. The window is opened by the GAME, on every peer, off state that is already
    /// replicated correctly:
    ///   <c>OpenCrateAbility.AbilityAdded</c>:38-42 subscribes <c>TacticalActor.AbilityExecutedEvent</c>, and
    ///   <c>OnActorAbilityExecuted</c>:70-97 fires on ANY peer whose copy of that soldier just finished an
    ///   <c>IMoveAbility</c> — which a MIRROR does, because the move is mirrored. It opens the crate
    ///   (<c>CrateComponent.Open</c>, the lid animation, correct everywhere) and ends at
    ///   <c>OpenCrate</c>:63 <c>base.Actor.GetAbility&lt;InventoryAbility&gt;().Activate()</c>.
    ///   <c>InventoryAbility.Activate</c>:11-15 then calls <c>View.ToInventoryViewState()</c> with NO test of
    ///   whose soldier this is.
    ///
    /// WHY IT WAS EMPTY, AND WHY <c>SelectedActor</c> IS THE RIGHT QUESTION RATHER THAN A GUESS.
    /// <c>UIStateInventory</c> does not open against the ability's actor at all — <c>PrimaryActor</c>:273 IS
    /// <c>Context.View.SelectedActor</c>, this peer's OWN selection. Every panel follows from it:
    /// <c>GetInventoryComponents</c>:663-677 enumerates <c>PrimaryActor.GetAbility&lt;InventoryAbility&gt;()
    /// .GetTargets()</c>, so the "ground" list is the containers in range of THIS peer's selected soldier —
    /// none, on a watcher standing elsewhere. That is the empty window with somebody else's kit in it,
    /// exactly.
    ///
    /// It also means vanilla RELIES on the two being the same actor: in a solo game you clicked the soldier to
    /// move him, so he IS <c>SelectedActor</c> when he reaches the crate, and the window is right. So the gate
    /// is not an ownership rule invented here (there is none — P5, any peer commands any soldier); it is the
    /// game's own precondition, asserted instead of assumed. On the acting peer it always holds — the walking
    /// soldier survives <c>UIStateWaiting</c>, and <c>TacticalView</c> clears <c>SelectedActor</c> only on a new
    /// turn (:1150) or when that actor leaves play (:1171-1175).
    ///
    /// SKIPPED WHOLE, not just the view entry. Everything else <c>TacticalAbility.Activate</c>:1078-1110 does on
    /// a watcher is a write that peer has no business making — <c>RegisterAbilityUsage</c>,
    /// <c>IncrementUsesThisTurn</c>, and the <c>CameraDirector.Hint(AbilityActivated)</c> that takes his camera
    /// (P4c, L97, L162). Nothing is stranded: <c>ApplyCosts</c> is a no-op here anyway
    /// (<c>InventoryAbility.ShouldApplyCosts</c>:26-33 needs a boxed <c>true</c>, and the crate path passes
    /// none), and the crate's lid has already opened by the time this line is reached.
    ///
    /// REACTIVITY (postulate 1): this changes no replicated state, so nothing needs a repaint that did not
    /// already have one. What the acting peer COMMITS in that window still rides 0x84 op 5 and still lands on
    /// every open panel through <c>TacticalUiRepaint.RepaintContainerView</c> (L157's "rebuilt the open
    /// inventory panels for …"). What is removed is a screen, on a peer that never asked for one.
    ///
    /// The three other callers of <c>ToInventoryViewState</c> are local clicks in a view state
    /// (<c>UIStateAbilitySelected</c>:805, <c>UIStateOverwatchAbilitySelected</c>:208) and are untouched — they
    /// do not pass through an ability at all, and the actor they open for is the selected one by construction.
    /// </summary>
    internal static class TacticalContainerOpen
    {
        /// <summary>MAY THIS PEER OPEN THE INVENTORY SCREEN FOR THIS ACTIVATION — pure, so RailCheck L179 can
        /// run it to exhaustion rather than read its IL. Two facts and no more, and neither is an ownership
        /// claim: is this a shared battle at all, and is the actor the game is about to open a window for the
        /// one this peer's own view is holding. Outside a session, and with no view to ask, the engine's answer
        /// is kept verbatim.</summary>
        internal static bool InventoryWindowMayOpen(bool inSharedBattle, bool viewExists, bool viewHoldsThisActor)
            => !inSharedBattle || !viewExists || viewHoldsThisActor;

        internal static bool InventoryWindowMayOpen(TacticalAbility ability)
        {
            var engine = NetworkEngine.Instance;
            bool shared = engine != null && engine.IsActiveSession;
            if (!shared) return true;

            var actor = ability == null ? null : ability.TacticalActor;
            var level = actor == null ? null : actor.TacticalLevel;
            TacticalView view = level == null ? null : level.View;
            // L113: Unity's == against a literal null is the right "is the native half alive" test; identity
            // between two references is ReferenceEquals and never ==.
            bool exists = !(view == null);
            if (!InventoryWindowMayOpen(true, exists, exists && ReferenceEquals(view.SelectedActor, actor)))
            {
                var holding = !exists || view.SelectedActor == null ? "nobody" : view.SelectedActor.name;
                MpLog.Log("[Multiplayer][tac] the container window for " +
                          (actor == null ? "<no actor>" : actor.name) + " was NOT opened on this peer — this " +
                          "peer's view is holding " + holding + ". The crate's lid still opens here; the " +
                          "inventory belongs to the peer whose soldier walked up to it. UIStateInventory." +
                          "PrimaryActor:273 IS TacticalView.SelectedActor, so opening it here would have shown " +
                          "THIS peer's soldier and a ground list of the containers in range of HIM — which is " +
                          "the empty window reported on 2026-08-06.");
                return false;
            }
            return true;
        }
    }

    /// <summary>THE ONE SEAM. <c>InventoryAbility.Activate</c> is the only path into
    /// <c>ToInventoryViewState</c> that an ENGINE-raised ability can take, and it is the path
    /// <c>OpenCrateAbility.OpenCrate</c>:63 takes on every peer. A bool prefix, because the whole point is to
    /// not run the body: a postfix would already have entered the state, moved the camera and counted the
    /// use.</summary>
    [HarmonyPatch(typeof(InventoryAbility), nameof(InventoryAbility.Activate), new[] { typeof(object) })]
    internal static class ContainerWindowOpensOnlyForTheActorThisPeerHolds
    {
        private static bool Prefix(InventoryAbility __instance)
            => TacticalContainerOpen.InventoryWindowMayOpen(__instance);
    }
}
