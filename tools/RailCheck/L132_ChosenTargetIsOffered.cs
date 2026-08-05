using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L132 — AN ORDER MAY ONLY NAME A TARGET THE HOST ITSELF OFFERS.
    ///
    /// THE HOLE. The host validated every order with the game's own <c>GetDisabledState()</c>, and that gate's
    /// target arm is <c>NoValidTarget</c> — <c>HasValidTargets</c>, i.e. "does SOME legal target exist"
    /// (TacticalAbility:464). It never asked whether the point or the actor THIS client picked was one of
    /// them. The chosen target rode the codec verbatim (<c>PositionToApply</c>) and the host applied it, so
    /// the choice was client-authoritative wherever the choice is the whole decision: which tile a passenger
    /// steps out onto (<c>ExitVehicleAbility.GetTargets</c>:100-113 = <c>CanExit</c> + <c>CanStandAt</c>),
    /// which vehicle a soldier boards and whether it has room (<c>VehicleComponent.CanEnter</c>:161,
    /// <c>IsFull</c>:37 — capacity is computed locally, so a peer whose roster had drifted could put a
    /// soldier into a full one).
    ///
    /// THE FIX IS ONE SEAM AND ZERO ABILITY KNOWLEDGE: <see cref="TacticalCommandSync.TargetIsOffered"/>
    /// enumerates the ability's OWN <c>GetTargets()</c> on the host and requires the client's choice to be in
    /// it, and <see cref="TacticalCommandSync.Validate"/> refuses it if it is not. Every rule any ability has
    /// about its targets — including every modded one — is that ability's own code, run by the host.
    ///
    /// THE ARMS. (a) executes the arbiter: the same order, accepted with the target offered and refused
    /// without it, so the arm cannot be dead. (b) executes the membership decision over the two axes a target
    /// can name. (c) is structural: the arbiter can only refuse what the intent handler actually asks it.
    ///
    /// Falsify: make <c>TargetMatches</c> return true unconditionally → <c>L132 any-target-is-offered</c>;
    /// delete the <c>targetIsOffered</c> arm from <c>Validate</c> → <c>L132 host-accepts-a-target-it-does-not-
    /// offer</c>; pass <c>true</c> instead of <c>TargetIsOffered(...)</c> in <c>HandleActivate</c> →
    /// <c>L132 intent-never-checks-the-choice</c>.
    /// </summary>
    internal static class L132_ChosenTargetIsOffered
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var matches = cmd.GetMethod("TargetMatches", All);
            var offered = cmd.GetMethod("TargetIsOffered", All);
            var handle = cmd.GetMethod("HandleActivate", All);
            if (matches == null || offered == null || handle == null)
            {
                yield return "L132 premise-changed: TacticalCommandSync.{TargetMatches,TargetIsOffered," +
                             "HandleActivate} no longer resolves — re-read this law before assuming the host " +
                             "still decides WHICH target an order may name.";
                yield break;
            }

            // ── (a) the arbiter, executed ────────────────────────────────────
            if (TacticalCommandSync.Validate(true, true, true, true, true, true, false, null, true,
                                             4f, 1f, 10f, 0f) != null)
                yield return "L132 offered-target-refused: an otherwise legal order whose target the host DOES " +
                             "offer is refused. The new arm has stopped being about the choice and is now " +
                             "refusing play itself.";
            var refusal = TacticalCommandSync.Validate(true, true, true, true, true, true, false, null, false,
                                                       4f, 1f, 10f, 0f);
            if (refusal == null)
                yield return "L132 host-accepts-a-target-it-does-not-offer: the arbiter accepts an order whose " +
                             "chosen target is NOT in the ability's own target set. The client then decides " +
                             "where a passenger disembarks and which vehicle it boards — the host's only " +
                             "remaining question being whether SOME legal target existed, which is a different " +
                             "question and is always true while the ability is usable at all.";
            else if (refusal.IndexOf("target", StringComparison.OrdinalIgnoreCase) < 0)
                yield return "L132 refusal-does-not-name-the-target: the order is refused with \"" + refusal +
                             "\", which is shipped to the player's screen (L123). A refusal that does not say " +
                             "the chosen target was the problem sends the reader hunting AP and turns.";

            // ── (b) the membership decision, executed ───────────────────────
            var vehicle = new object();
            var otherVehicle = new object();
            var here = new Vector3(4f, 0f, -2f);
            var neighbour = new Vector3(5f, 0f, -2f);          // one grid cell away
            var jitter = new Vector3(4.02f, 0.05f, -1.98f);    // the same tile, re-derived by another peer

            if (!TacticalCommandSync.TargetMatches(vehicle, Vector3.zero, false, vehicle, Vector3.zero, false))
                yield return "L132 same-actor-is-not-a-match: an order naming the very actor the host offers is " +
                             "rejected. Boarding would then be refused for every peer including the one whose " +
                             "board agrees with the host's.";
            if (TacticalCommandSync.TargetMatches(otherVehicle, Vector3.zero, false, vehicle, Vector3.zero, false))
                yield return "L132 any-target-is-offered: an order naming a DIFFERENT actor than the host offers " +
                             "matches. That is the whole gap — a soldier boarding the vehicle its own peer " +
                             "picked rather than one the host says it may.";
            if (!TacticalCommandSync.TargetMatches(null, jitter, true, null, here, true))
                yield return "L132 same-tile-not-recognised: two peers' own derivations of the SAME tile " +
                             "(a floor cast differs in the last decimals) are treated as different targets. " +
                             "Every disembark would be refused, which is a peer-wide brick, not a fix.";
            if (TacticalCommandSync.TargetMatches(null, neighbour, true, null, here, true))
                yield return "L132 neighbouring-tile-accepted: a target one whole grid cell from the offered one " +
                             "matches. The tolerance has grown past the grid and the exit tile is client-chosen " +
                             "again, one square at a time.";
            if (TacticalCommandSync.TargetMatches(null, Vector3.zero, false, null, here, true))
                yield return "L132 positionless-offer-matches-a-position: an offer that names no position " +
                             "satisfies an order that chose one. Any ability with a single positionless offer " +
                             "would then accept every point on the map.";
            if (!TacticalCommandSync.TargetMatches(null, Vector3.zero, false, null, Vector3.zero, false))
                yield return "L132 choiceless-order-refused: an order that names neither an actor nor a position " +
                             "(a direction, an inventory item, the actor itself) is refused. There is no choice " +
                             "between offers to arbitrate there, and the game's own gate is its authority.";

            // ── (c) the arm must be asked ───────────────────────────────────
            if (!Program.Callees(handle, cmd.Assembly).Any(c => c.MetadataToken == offered.MetadataToken))
                yield return "L132 intent-never-checks-the-choice: HandleActivate no longer calls TargetIsOffered, " +
                             "so the arbiter's target arm is fed a constant and the whole gate is dead code. " +
                             "This is the shape this repo keeps paying for: the law stays green on the arbiter " +
                             "while the rail never asks it anything.";
        }
    }
}
