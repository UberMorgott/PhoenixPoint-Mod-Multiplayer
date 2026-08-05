using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L133 — A MIRRORED ABILITY ALWAYS REACHES A TERMINAL STATE, AND THE STATE IT REACHES IS THE HOST'S.
    ///
    /// THE CLASS. A mirrored ability's completion can depend on something the mirror never gets. Both vehicle
    /// coroutines park on a local presentation event with no ceiling of their own
    /// (<c>EnterVehicleAbility</c>:104 and <c>ExitVehicleAbility</c>:75 await the <c>OpenedDoor</c> anim
    /// event), and a coroutine chain broken by a throw never resumes at all (the 2026-08-01 bash NRE). Then
    /// <c>PlayingAction.CompleteAction</c> never runs, <c>ClearPlayingAction</c> never runs, the actor never
    /// leaves <c>ExecutingAbilities</c>, and <c>Validate</c>'s actorBusy arm refuses EVERY later order for
    /// that soldier — a soldier bricked for the rest of the battle. It is a CLASS, not two abilities: any
    /// mirrored ability whose end depends on local presentation belongs to it.
    ///
    /// IT IS ANSWERED AT THE SEAM, ONCE. The host's settle is the closer for every rider and the turn-edge
    /// sweep re-issues one for every keyed live actor, so the peer's held settle is the one place that sees a
    /// busy actor for all of them: <see cref="TacticalCommandSync.SettleMustBeForced"/> bounds the hold,
    /// <c>ApplySettle</c> ends the ability through the game's OWN teardown
    /// (<c>ActionComponent.CancelActions</c> → <c>ClearPlayingAction</c>, the same exit a completed action
    /// takes) and then reconciles the actor's STATUS SET to the host's — which is what makes the end state
    /// DEFINED rather than partial, because the cancel tears the coroutine wherever it stands (an
    /// <c>EnterVehicleCrt</c> killed between its nav-obstacle disable and its <c>ApplyMountedStatus</c> leaves
    /// an actor that is neither on foot nor aboard). The frame ceiling is a fallback and it CONVERGES: what
    /// it applies is the host's position, AP, WP and status set.
    ///
    /// THE ARMS. (a) is the outcome, EXECUTED as a simulation: an actor that never stops executing must still
    /// have its settle forced within a bounded number of frames — the law runs the hold to exhaustion rather
    /// than reading a constant. (b) pins the two immediate answers. (c) is structural: forcing the settle is
    /// only terminal because the applier cancels the action channel, and only defined because it reconciles
    /// the status set.
    ///
    /// Falsify: make <c>SettleMustBeForced</c> return false while the actor is busy → <c>L133
    /// hold-has-no-ceiling</c>; drop the <c>CancelActions</c> call from <c>ApplySettle</c> → <c>L133
    /// forced-settle-does-not-end-the-ability</c>.
    /// </summary>
    internal static class L133_MirroredAbilityTerminates
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>~60 s at 60 fps. Not the rail's ceiling — the rail's is its own business — but the point
        /// past which "the soldier will come back" has stopped being a description of a playable battle.</summary>
        private const int Unplayable = 3600;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var forced = cmd.GetMethod("SettleMustBeForced", All);
            var apply = cmd.GetMethod("ApplySettle", All);
            var tick = cmd.GetMethod("ClientTick", All);
            if (forced == null || apply == null || tick == null)
            {
                yield return "L133 premise-changed: TacticalCommandSync.{SettleMustBeForced,ApplySettle," +
                             "ClientTick} no longer resolves. Nothing else in the rail bounds how long a " +
                             "mirrored ability may hold a soldier — re-read this law before assuming one ends.";
                yield break;
            }

            // ── (a) THE OUTCOME: run the hold to exhaustion ──────────────────
            int at = -1;
            for (int frame = 1; frame <= 1000000; frame++)
                if (TacticalCommandSync.SettleMustBeForced(true, false, frame)) { at = frame; break; }
            if (at < 0)
                yield return "L133 hold-has-no-ceiling: an actor that NEVER stops executing an ability holds its " +
                             "settle for a million frames and counting. That is the brick this law exists for: " +
                             "the mirrored ability never ends, the correction is never applied, and every later " +
                             "order for that soldier is refused by the actorBusy arm for the rest of the battle.";
            else if (at > Unplayable)
                yield return "L133 ceiling-past-playable: a stuck mirrored ability holds its soldier for " +
                             (at / 60) + "s before the settle is forced. It terminates, but a player has spent a " +
                             "minute clicking a soldier that answers nothing, which is indistinguishable from " +
                             "the bug.";

            // ── (b) the two immediate answers ───────────────────────────────
            if (!TacticalCommandSync.SettleMustBeForced(false, false, 0))
                yield return "L133 idle-actor-still-held: the settle for an actor that is executing NOTHING is " +
                             "held anyway. There is nothing left to wait for, and the host's position and AP sit " +
                             "unapplied while the actor stands in the wrong place.";
            if (!TacticalCommandSync.SettleMustBeForced(true, true, 0))
                yield return "L133 forced-settle-waits: a settle the HOST marked forced — it sent that settle " +
                             "BECAUSE it refused the order — is held behind the very speculative ability the " +
                             "refusal was about. That ability is the one least likely to ever end.";

            // ── (c) forcing it must actually END the ability, in a known state ──
            var asm = cmd.Assembly;
            // The teardown lives in the GAME assembly — the whole point is that it is the engine's own exit
            // and not a poke of ours, so this arm is resolved against Assembly-CSharp.
            if (!Program.Callees(apply, typeof(PhoenixPoint.Tactical.Entities.TacticalActorBase).Assembly)
                        .Any(c => c.Name == "CancelActions"))
                yield return "L133 forced-settle-does-not-end-the-ability: ApplySettle no longer cancels the " +
                             "actor's action channel. Applying a position on top of a stuck ability is not a " +
                             "terminal state — the actor stays in ExecutingAbilities, so it stays dead to input " +
                             "and the settle silently loses to that ability's own navigation.";
            if (!Program.Callees(apply, asm).Any(c => c.Name == "Reconcile" &&
                                                      c.DeclaringType == typeof(TacticalStatusSet)))
                yield return "L133 terminal-state-is-partial: ApplySettle ends the mirrored ability without " +
                             "reconciling the actor's status set. The cancel kills the coroutine wherever it " +
                             "stands, so what is left is HALF of whatever it was doing — half a boarding, with " +
                             "no MountedStatus and no nav obstacle — and nothing later disagrees with it.";
            if (!Program.Callees(tick, asm).Any(c => c.MetadataToken == forced.MetadataToken))
                yield return "L133 ceiling-never-consulted: ClientTick no longer asks SettleMustBeForced. The " +
                             "bound is then whatever is inlined at the call site and this law is asserting a " +
                             "decision nothing makes.";
        }
    }
}
