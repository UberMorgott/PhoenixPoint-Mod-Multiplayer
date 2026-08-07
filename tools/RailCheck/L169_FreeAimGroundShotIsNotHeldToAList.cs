using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities;

namespace RailCheck
{
    /// <summary>
    /// L169 — AN ORDER AIMED AT A FREE POINT IS NOT HELD TO A LIST THE GAME ITSELF DISCARDS.
    ///
    /// THE BUG, MEASURED. On a client the grenade arc drew GREEN, the player threw, the soldier twitched, and
    /// nothing happened — five times on one soldier in 25 seconds, and again on a second soldier. The host's
    /// own words are in the client's log: <c>the chosen target is not one this ability offers on the host</c>
    /// (<see cref="TacticalCommandSync.Validate"/>:1737-1739), followed by <c>IntentRail.Reject</c> and a
    /// <c>HostSettle(forced: true)</c> whose <c>CancelActions</c> IS the twitch. The refused points were
    /// <c>(1.9, 0.1, 10.4)</c>, <c>(1.9, 0.1, 10.3)</c>, <c>(2, 0, 10.6)</c> — a human nudging a free cursor by
    /// tenths of a unit, which is the proof it was free aim and not tile picking.
    ///
    /// THE ROOT WAS OURS, AND IT CONTRADICTED THE ENGINE. <see cref="TacticalCommandSync.TargetIsOffered"/>
    /// held EVERY <c>ShootAbility</c> to <c>ability.GetTargets()</c>, because <c>ShootAbility</c> overrides
    /// that method and so <c>DeclaresOwnTargets</c> is true. But for a ground-targeted shoot the game THROWS
    /// THAT ENUMERATION AWAY: <c>UIStateAbilitySelected.EnterState</c>:198-202 replaces it with
    /// <c>Enumerable.Empty&lt;TacticalAbilityTarget&gt;()</c> and :468-470 enters <c>UIStateShoot</c> with a
    /// NULL valid-shoot list; the arc's whole verdict is then <c>GetShootTarget(...) != null</c>
    /// (<c>UIStateShoot</c>:1182) over the raw cursor point (:1180). <c>GetTargets()</c> meanwhile is a grid
    /// FLOOR-CAST SWEEP (<c>TacticalAbility.GetTargetPositions</c>:605-660) — a free cursor point is not drawn
    /// from that grid and need not land within <c>TargetMatches</c>'s half-unit tolerance of any member of it.
    /// So the host was enforcing a rule the game does not have, and only against CLIENTS: the host's own throw
    /// takes the <c>RelayMirror</c> branch and never passes through <c>Validate</c> at all.
    ///
    /// WHY THIS LAW EXISTS SEPARATELY FROM L132. L132 executes <c>ChoiceIsOffered</c>, which is unchanged by
    /// this fix and was green throughout the entire outage — its three axes (names a target / published a list
    /// / matched) are all correct, and a free-aim throw is simply the case where "published a list" is TRUE and
    /// "matched" is FALSE for a legal order. No arm of L132 could have caught it and none can catch its
    /// regression. This law asserts the missing OUTCOME (<c>multiplayer2-laws-assert-outcome</c>): a free-aim
    /// ground shot ENDS WITH THE ABILITY EXECUTING.
    ///
    /// THE POSITIVE CONTROL IS THE POINT OF ARM (c). The exemption's cheapest wrong form is to drop the
    /// <c>ShootAbility</c> narrowing and exempt everything whose <c>TargetResult</c> is
    /// <c>Position</c>/<c>ActorAndPosition</c>. That is true of <c>ExitVehicleAbility</c> — the ability this
    /// whole gate was written for, where the exit tile was effectively client-authoritative
    /// (<c>Validate</c>:1731-1736, <c>ExitVehicleAbility.GetTargets</c>:100-113 = <c>CanExit</c> +
    /// <c>CanStandAt</c>). The engine narrows it too, at <c>UIStateAbilitySelected</c>:199. Arm (c) fails the
    /// moment the narrowing is dropped.
    ///
    /// NOT ASSERTED, HONESTLY: that a rifle's def actually carries a non-ground <c>TargetResult</c>. That is a
    /// value in game DATA, not in any assembly, so no console host can read it. The anchor is the engine's own
    /// behaviour instead: if <c>IsTargetingGround</c> were true for rifles, :199 would discard the target list
    /// for rifles too and the shipped game would have no tab-targeting. Arm (d) tests the predicate's own
    /// truth table for the delivery types, which is the half that lives in code.
    ///
    /// Falsified 2026-08-07, all five both ways. Delete the exemption call from <c>TargetIsOffered</c> →
    /// <c>L169 exemption-is-decorative</c> ALONE, because arm (a) is pure and does not reach the live seam —
    /// arm (e) is the only thing standing between this law and a green proof about dead code, which is why it
    /// is not optional. Make <c>GroundTargetingExemptsTheOrder</c> return false
    /// unconditionally → <c>L169 grenade-is-held-to-a-list</c>, <c>L169 cone-is-held-to-a-list</c>,
    /// <c>L169 free-aim-throw-never-executes</c>; return true unconditionally → <c>L169 exemption-is-not-
    /// narrowed-to-a-shoot</c>, <c>L169 exit-tile-is-client-authoritative-again</c> and
    /// <c>L169 aimed-shot-escapes-the-gate</c>; drop the <c>isShootAbility</c> guard only →
    /// <c>L169 exemption-is-not-narrowed-to-a-shoot</c> + <c>L169 exit-tile-is-client-authoritative-again</c>;
    /// change <c>Sphere</c> to fall through → <c>L169 copied-predicate-was-edited</c>.
    /// </summary>
    internal static class L169_FreeAimGroundShotIsNotHeldToAList
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var exempts = cmd.GetMethod("GroundTargetingExemptsTheOrder", All);
            var offered = cmd.GetMethod("TargetIsOffered", All);
            var validate = cmd.GetMethod("Validate", All);
            if (exempts == null || offered == null || validate == null)
            {
                yield return "L169 premise-changed: TacticalCommandSync.{GroundTargetingExemptsTheOrder," +
                             "TargetIsOffered,Validate} no longer resolves. Nothing else stops the host holding " +
                             "a free-aim throw to a grid sweep the engine itself discards — re-read this law " +
                             "before assuming something does.";
                yield break;
            }

            // THE SOURCE WE COPIED. TacticalViewState.IsTargetingGround:200-219 is protected static, so it is
            // unreachable from the mod and its body had to be reproduced. If the engine's own method is gone or
            // has changed shape, our copy is a rule nobody else in the game is following any more.
            var native = typeof(PhoenixPoint.Tactical.View.TacticalViewState)
                         .GetMethod("IsTargetingGround", All, null,
                                    new[] { typeof(PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility) }, null);
            if (native == null)
            {
                yield return "L169 premise-changed: TacticalViewState.IsTargetingGround(TacticalAbility) is gone " +
                             "from this build of the game. GroundTargetingExemptsTheOrder is a VERBATIM COPY of " +
                             "its body; with the original deleted or reshaped, the copy is our invention again " +
                             "and must be re-read against whatever replaced it before this law means anything.";
                yield break;
            }

            // ── (a) THE OUTCOME: a free-aim grenade ENDS WITH THE ABILITY EXECUTING ──
            // The enumeration verdict is modelled as the reality that broke: the ability DID publish a list
            // (a grid floor-cast sweep) and the free cursor point matched NOTHING in it. TargetIsOffered's own
            // shape is exemption-first, so the order survives only if the exemption answers.
            bool enumerationRefuses = TacticalCommandSync.ChoiceIsOffered(true, true, false);
            bool grenadeIsOffered =
                TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor,
                                                                   DamageDeliveryType.Parabola) || enumerationRefuses;
            var throwVerdict = TacticalCommandSync.Validate(true, true, true, true, true, true, false, null,
                                                            grenadeIsOffered, 4f, 1f, 10f, 0f);
            if (throwVerdict != null)
                yield return "L169 free-aim-throw-never-executes: a grenade thrown at a free point by a client — " +
                             "actor alive, player-controlled, its faction playing, the game's own gate silent, AP " +
                             "and WP paid, and the client's own arc drawn GREEN by GetShootTarget — is refused " +
                             "with \"" + throwVerdict + "\". The host then never runs it, the reject forces a " +
                             "settle, and CancelActions tears the wind-up out mid-animation: that is the twitch " +
                             "the player reports, five times in 25 seconds.";
            if (enumerationRefuses)
                yield return "L169 outcome-is-vacuous: the enumeration half of arm (a) is no longer refusing, so " +
                             "the arm would stay green with the exemption deleted. It must model the case that " +
                             "actually broke — a published list with no matching member.";

            // ── (b) every free-aim shape the same seam decides, not just the reported one ──
            if (!TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor,
                                                                    DamageDeliveryType.Parabola))
                yield return "L169 grenade-is-held-to-a-list: a thrown weapon (DamageDeliveryType.Parabola) is " +
                             "still held to GetTargets(). That is the reported bug verbatim.";
            if (!TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor,
                                                                    DamageDeliveryType.Cone))
                yield return "L169 cone-is-held-to-a-list: a cone weapon is held to GetTargets(). It is the SAME " +
                             "free-aim path (TacticalViewState:213 covers Cone beside Parabola) and it fails the " +
                             "same way — it was simply not the weapon in the player's hand that evening.";
            if (!TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Position,
                                                                    DamageDeliveryType.DirectLine))
                yield return "L169 free-aim-shot-is-held-to-a-list: a shoot ability whose ORIGIN targets a " +
                             "position — the FPS/free-cam shot, which picks a ray point rather than an offer — is " +
                             "held to GetTargets(). Same root, different weapon.";
            if (!TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor,
                                                                    DamageDeliveryType.Sphere) ||
                !TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor, null))
                yield return "L169 copied-predicate-was-edited: TacticalViewState.IsTargetingGround:213-217 " +
                             "answers TRUE for a Sphere delivery and for a shoot ability with NO weapon at all " +
                             "(its `?.` yields null and control falls to `return true`). Our copy no longer does. " +
                             "A copy that has been 'tidied' is an invention with a citation on it.";

            // ── (c) POSITIVE CONTROL: the hole this gate was built for stays shut ──
            if (TacticalCommandSync.GroundTargetingExemptsTheOrder(false, TargetResult.Position, null) ||
                TacticalCommandSync.GroundTargetingExemptsTheOrder(false, TargetResult.ActorAndPosition, null))
                yield return "L169 exemption-is-not-narrowed-to-a-shoot: an ability that is NOT a ShootAbility but " +
                             "targets a position is exempted from its own published target list. " +
                             "ExitVehicleAbility is exactly that ability, and its list IS the rule " +
                             "(GetTargets:100-113 = CanExit + CanStandAt) — exempting it hands the exit tile back " +
                             "to the client, which is the hole L132 was written to close. The engine narrows it " +
                             "too, at UIStateAbilitySelected:199.";
            var exitTile = TacticalCommandSync.Validate(
                true, true, true, true, true, true, false, null,
                TacticalCommandSync.GroundTargetingExemptsTheOrder(false, TargetResult.Position, null) ||
                    TacticalCommandSync.ChoiceIsOffered(true, true, false),
                4f, 1f, 10f, 0f);
            if (exitTile == null)
                yield return "L169 exit-tile-is-client-authoritative-again: a non-shoot ability that published a " +
                             "target list and whose chosen position is NOT in it is ACCEPTED by the arbiter. A " +
                             "passenger then disembarks onto whatever tile his own peer picked and every other " +
                             "peer writes it down.";

            // ── (d) an AIMED shot still answers to the list it publishes ──
            if (TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor,
                                                                   DamageDeliveryType.DirectLine) ||
                TacticalCommandSync.GroundTargetingExemptsTheOrder(true, TargetResult.Actor,
                                                                   DamageDeliveryType.Melee))
                yield return "L169 aimed-shot-escapes-the-gate: a direct-line or melee shoot ability is exempted " +
                             "even though it names an actor from a list the game really does publish and really " +
                             "does draw its tab-targeting from. The exemption has stopped being about free aim " +
                             "and now covers every attack in the game.";

            // ── (e) the exemption must be the one the host reaches ─────────────
            if (!Program.Callees(offered, cmd.Assembly).Any(c => c.MetadataToken == exempts.MetadataToken))
                yield return "L169 exemption-is-decorative: TargetIsOffered no longer routes through " +
                             "GroundTargetingExemptsTheOrder, so every case above is proved about a function the " +
                             "host does not consult. This is the shape this repo keeps paying for — L132 stayed " +
                             "green for four days while the gate it named refused every client overwatch.";
        }
    }
}
