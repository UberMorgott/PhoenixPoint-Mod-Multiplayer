using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.View;
using PhoenixPoint.Tactical.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L179 — A LOOT CONTAINER'S INVENTORY OPENS ON THE PEER WHOSE VIEW IS HOLDING THAT SOLDIER, AND ON NO OTHER.
    ///
    /// THE REPORT (2026-08-06, ambush mission). A CLIENT's soldier walked up to a crate and the container window
    /// opened ON THE HOST — empty, showing what looked like the host's own soldier's kit. In reverse too. The
    /// owner's rule is one sentence: nothing opens on the other peers; they see the lid animation at most.
    ///
    /// NOTHING WE RELAY DOES IT, WHICH IS WHY NO EXISTING LAW COULD SEE IT. <c>InventoryAbility</c> has been in
    /// <c>TacticalCommandSync.LocalAbilities</c> since A6 precisely so it never crosses, and the log proves the
    /// drop fired ("'Inventory_AbilityDef' is DECLARED LOCAL … It ran on this peer only, by design"). The window
    /// is opened by the GAME, on each peer, off state that is replicated CORRECTLY:
    /// <c>OpenCrateAbility.AbilityAdded</c>:38-42 subscribes <c>TacticalActor.AbilityExecutedEvent</c>, so
    /// <c>OnActorAbilityExecuted</c>:70-97 fires on every peer whose copy of that soldier finished an
    /// <c>IMoveAbility</c> — a mirror does — and <c>OpenCrate</c>:63 ends at
    /// <c>GetAbility&lt;InventoryAbility&gt;().Activate()</c>. <c>InventoryAbility.Activate</c>:11-15 calls
    /// <c>View.ToInventoryViewState()</c> with no test of whose soldier this is. A rail law asks "did this
    /// cross"; the answer here was "no", and the screen opened anyway.
    ///
    /// WHY IT WAS EMPTY IS ALSO WHY <c>SelectedActor</c> IS THE HONEST DISCRIMINATOR RATHER THAN AN OWNERSHIP
    /// CLAIM (P5 says there is none). <c>UIStateInventory</c> never opens against the ability's actor:
    /// <c>PrimaryActor</c>:273 IS <c>Context.View.SelectedActor</c>, and <c>GetInventoryComponents</c>:663-677
    /// enumerates that actor's <c>InventoryAbility.GetTargets()</c> — so on a watcher the ground list is the
    /// containers in range of HIS soldier, which is none. Vanilla therefore already RELIES on the two being the
    /// same actor; solo, you clicked the soldier to move him, so he is selected when he reaches the crate. The
    /// gate asserts the game's own precondition instead of assuming it.
    ///
    /// THE ARMS. (a) runs <see cref="TacticalContainerOpen.InventoryWindowMayOpen(bool,bool,bool)"/> over its
    /// whole cube — the watcher case must be the ONLY refusal, so a solo game and a peer holding that very
    /// soldier are positive controls inside the same arm. (b) is the seam: a bool PREFIX on
    /// <c>InventoryAbility.Activate</c> that actually consults the decision — a postfix would already have
    /// entered the state, and a gate nothing calls is the shape this repo keeps paying for (L132, L137).
    /// (c) and (d) are the two GAME premises the whole reasoning stands on, and either one moving silently
    /// turns the gate into either a no-op or a new bug: <c>InventoryAbility.Activate</c> must still be the path
    /// into <c>ToInventoryViewState</c>, and <c>UIStateInventory.PrimaryActor</c> must still read
    /// <c>TacticalView.SelectedActor</c>. (e) pins the raiser: an ENGINE-raised crate open must still reach
    /// <c>InventoryAbility.Activate</c>, or this law is guarding a door nobody walks through.
    ///
    /// Falsify: make <c>InventoryWindowMayOpen</c> return true unconditionally → <c>L179
    /// window-opens-on-a-watcher</c>; return false unconditionally → <c>L179 own-peer-loses-the-container</c>
    /// and <c>L179 solo-game-changed</c>; make the patch a postfix or drop its bool return → <c>L179
    /// prefix-cannot-withhold</c>; stop calling the decision from it → <c>L179 gate-is-decorative</c>.
    /// </summary>
    internal static class L179_ContainerWindowOpensOnlyForItsOwnPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var open = typeof(TacticalContainerOpen);
            var decision = open.GetMethod("InventoryWindowMayOpen",
                               All, null, new[] { typeof(bool), typeof(bool), typeof(bool) }, null);
            var live = open.GetMethod("InventoryWindowMayOpen", All, null, new[] { typeof(TacticalAbility) }, null);
            var patch = open.Assembly.GetType(
                            "Multiplayer.Tactical.ContainerWindowOpensOnlyForTheActorThisPeerHolds");
            var prefix = patch == null ? null : patch.GetMethod("Prefix", All);
            if (decision == null || live == null || patch == null || prefix == null)
            {
                yield return "L179 premise-changed: TacticalContainerOpen.InventoryWindowMayOpen or " +
                             "ContainerWindowOpensOnlyForTheActorThisPeerHolds.Prefix no longer resolves. " +
                             "Nothing else stops the game opening a container's inventory on a peer that is " +
                             "merely watching — re-read this law before assuming the drop list covers it, " +
                             "because InventoryAbility was ALREADY declared local while this happened.";
                yield break;
            }

            // ── (a) THE OUTCOME, over the whole cube ─────────────────────────
            if (TacticalContainerOpen.InventoryWindowMayOpen(true, true, false))
                yield return "L179 window-opens-on-a-watcher: a shared battle, a live tactical view, and the " +
                             "actor the game is about to open a window for is NOT the one this peer's view is " +
                             "holding — and the window opens. That is the report verbatim: the client's soldier " +
                             "reached the crate and the HOST got an empty inventory panel showing his own " +
                             "soldier, because UIStateInventory.PrimaryActor:273 is TacticalView.SelectedActor " +
                             "and its ground list is the containers in range of THAT actor.";
            if (!TacticalContainerOpen.InventoryWindowMayOpen(true, true, true))
                yield return "L179 own-peer-loses-the-container: the peer whose view IS holding that soldier is " +
                             "refused the window too. The loot is then unreachable for everybody, which is a " +
                             "worse bug than the one this law exists for and is what the cheap version of this " +
                             "gate (refuse every engine-raised open) does.";
            if (!TacticalContainerOpen.InventoryWindowMayOpen(false, true, false))
                yield return "L179 solo-game-changed: the window is withheld outside a shared session. A mod " +
                             "that is not in a co-op battle must leave the engine's own behaviour byte for byte.";
            if (!TacticalContainerOpen.InventoryWindowMayOpen(true, false, false))
                yield return "L179 no-view-still-refused: the gate refuses when there is no tactical view at " +
                             "all. There is then no screen to open and no peer to be wrong — refusing there is " +
                             "this mod inventing a behaviour instead of narrowing one.";

            // ── (b) the seam: a PREFIX that can withhold, routed through the decision ──
            if (prefix.ReturnType != typeof(bool))
                yield return "L179 prefix-cannot-withhold: the patch method no longer returns bool, so it " +
                             "cannot skip the original. Harmony runs InventoryAbility.Activate regardless — the " +
                             "state is entered, the use is counted and CameraDirector.Hint(AbilityActivated) " +
                             "has already taken the watcher's camera (L97, L162) before anything is decided.";
            if (!Program.Callees(prefix, open.Assembly).Any(c => c.MetadataToken == live.MetadataToken))
                yield return "L179 gate-is-decorative: the prefix no longer consults InventoryWindowMayOpen, so " +
                             "every arm above is proved about a decision the live seam does not make.";

            // ── (c)(d) the two GAME premises the reasoning stands on ─────────
            var activate = typeof(InventoryAbility).GetMethod("Activate", All, null, new[] { typeof(object) }, null);
            var toInv = typeof(TacticalView).GetMethod("ToInventoryViewState", All);
            if (activate == null || toInv == null ||
                !Program.Callees(activate, typeof(TacticalView).Assembly)
                        .Any(c => c.MetadataToken == toInv.MetadataToken))
                yield return "L179 premise-changed: InventoryAbility.Activate no longer reaches " +
                             "TacticalView.ToInventoryViewState. That call IS what this gate stands in front " +
                             "of; if the game now opens the inventory from somewhere else, the prefix is dead " +
                             "code and the window is back on every peer with nothing said about it.";
            var primary = typeof(UIStateInventory).GetProperty("PrimaryActor", All);
            var getter = primary == null ? null : primary.GetGetMethod(true);
            var selected = typeof(TacticalView).GetProperty("SelectedActor", All);
            var selGetter = selected == null ? null : selected.GetGetMethod(true);
            if (getter == null || selGetter == null ||
                !Program.Callees(getter, typeof(TacticalView).Assembly)
                        .Any(c => c.MetadataToken == selGetter.MetadataToken))
                yield return "L179 premise-changed: UIStateInventory.PrimaryActor no longer reads " +
                             "TacticalView.SelectedActor. The whole discriminator is that the game opens this " +
                             "screen against THIS peer's selection and not against the ability's actor — that " +
                             "is why a watcher's window was empty, and why matching the two is the game's own " +
                             "precondition rather than an ownership rule of ours. If PrimaryActor now follows " +
                             "the ability, this gate refuses windows that would have been correct.";

            // ── (e) the raiser: an engine-raised crate open still reaches the seam ──
            if (!Bodies(typeof(OpenCrateAbility)).Any(m => Program.Callees(m, typeof(TacticalView).Assembly)
                                                                  .Any(ReachesTheInventoryAbility)))
                yield return "L179 premise-changed: OpenCrateAbility no longer activates InventoryAbility. The " +
                             "defect this law is about is an ability NO PEER CLICKED opening a screen — if the " +
                             "crate path now opens the inventory some other way, that way is unguarded.";
        }

        /// <summary>The crate path names <c>InventoryAbility</c> either as the receiver of the call
        /// (<c>OpenCrate</c>:63) or as the type argument of the lookup that produced it
        /// (<c>GetAbility&lt;InventoryAbility&gt;()</c>). Which of the two the compiler emitted is not the
        /// premise — that the path reaches that ability at all is.</summary>
        private static bool ReachesTheInventoryAbility(MethodBase callee)
        {
            if (callee.DeclaringType == typeof(InventoryAbility)) return true;
            if (!callee.IsGenericMethod) return false;
            return callee.GetGenericArguments().Any(a => a == typeof(InventoryAbility));
        }

        private static IEnumerable<MethodBase> Bodies(System.Type t)
        {
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(Declared)) yield return m;
            foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in n.GetMethods(Declared)) yield return m;
        }
    }
}
