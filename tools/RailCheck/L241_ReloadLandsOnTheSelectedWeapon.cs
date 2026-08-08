using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Entities;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L241 — A RELOAD LANDS ON THE WEAPON IN HIS HANDS, AND A BROKEN ARM STILL REFUSES THE RIFLE.
    ///
    /// THE REPORT (2026-08-08, one soldier with broken arms who could not hold his sniper rifle): pressing
    /// RELOAD on the pistol made him SWAP to the primary rifle and then failed — he could never reload at all.
    /// Five refusals in a row, client 3, each one <c>Недостаточно свободных рук</c> =
    /// <c>AbilityDisabledState.NotEnoughHands</c> (<c>TacticalAbility.GetDisabledStateInternal</c>:419, over
    /// <c>Equipment.HasEnoughHandsToUse</c>). The sixth was accepted at 02:46:18 only because an inventory move
    /// had removed the rifle from his slots.
    ///
    /// It was never a hands rule and never a lock: the host resolved <c>Reload_AbilityDef</c> by guid alone,
    /// <c>GetAbilityFiltered</c> returned the FIRST of the several instances that def mints (one per item that
    /// can be reloaded), and the gate was therefore asked about the RIFLE while the peer was holding the
    /// PISTOL. L240 fixes the resolution; this law asserts the OUTCOME that fix exists for, so that a later
    /// "simplification" of either site cannot quietly restore the refusal.
    ///
    /// IT MAY NOT BE FIXED BY RELAXING THE RULE. The rifle a broken arm cannot hold must STAY refused — arm
    /// (a) runs both halves for exactly that reason: the pistol is accepted AND the rifle still produces a
    /// refusal carrying the literal <c>NotEnoughHands</c>.
    ///
    /// WHAT THIS LAW ASSERTS.
    ///   (a) EXECUTED, end to end over the two SHIPPED pure functions — <c>CandidateMatchesSelection</c> picks
    ///       the instance, <c>Validate</c> answers for it. Rifle-and-pistol, pistol selected, broken arms: the
    ///       command is ACCEPTED (<c>Validate</c> returns null) and the rifle branch still refuses with
    ///       <c>NotEnoughHands</c> in the sentence. Both polarities, so the arm is its own control.
    ///   (b) the gate is asked about the RESOLVED instance: <c>HandleActivate</c> resolves BEFORE it calls
    ///       <c>GetDisabledState</c>, and never asks <c>GetAbilityFiltered</c> itself.
    ///   (c) the game-side premise, read from the real assembly — <c>AbilityDisabledState.NotEnoughHands</c>,
    ///       <c>Equipment.HasEnoughHandsToUse</c>, <c>ReloadAbility : TacticalAbility</c> (NOT a
    ///       <c>ShootAbility</c>, so no <c>is ShootAbility</c> narrowing ever covers it), and the charges the
    ///       reload spends being the RESOLVED equipment's own (<c>CommonItemData.CurrentCharges</c> vs
    ///       <c>TacticalAbility.GetRequiredCharges</c>) — which is why resolving the wrong instance spends or
    ///       refuses charges on the wrong gun.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> resolves by first match and its Validate arm
    ///       is fed the rifle's answer; both (a) and (b) must go red on it.
    ///
    /// EVERY STATE NAME HERE IS A STRING LITERAL. <c>AbilityDisabledState.ToString</c>:182-185 localizes
    /// through <c>GameUtl.Game()</c> and throws <c>SecurityException</c> outside a running game — a law that
    /// called it would crash rather than judge.
    ///
    /// Falsify: revert either resolution site to first-match-by-guid → (a) via the control shape and (b);
    /// make <c>Validate</c> swallow <c>abilityDisabledReason</c> → (a); empty <see cref="FakeSeam"/> → (d).
    /// </summary>
    internal static class L241_ReloadLandsOnTheSelectedWeapon
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The gate's own name for the refusal, as a LITERAL. Never AbilityDisabledState.ToString().</summary>
        private const string Hands = "NotEnoughHands";

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var hostSite = sync.GetMethod("HandleActivate", AllMembers);
            var validate = sync.GetMethod("Validate", AllMembers);
            if (hostSite == null || validate == null)
            {
                yield return "L241 premise-changed: TacticalCommandSync.HandleActivate or Validate no longer " +
                             "resolves — the arbiter this law reads has moved and every arm below would be " +
                             "asserting about a shape the build does not have.";
                yield break;
            }

            // ── (c) THE GAME-SIDE PREMISE ────────────────────────────────────
            if (typeof(AbilityDisabledState).GetField(Hands, BindingFlags.Public | BindingFlags.Static) == null)
                yield return "L241 hands-premise-gone: AbilityDisabledState." + Hands + " no longer exists. That " +
                             "is the exact refusal a broken-armed soldier's rifle produces, and the one this " +
                             "law asserts must NOT reach a pistol reload — with the name gone, the assertion is " +
                             "about nothing.";
            if (typeof(Equipment).GetMethod("HasEnoughHandsToUse", BindingFlags.Public | BindingFlags.Instance) == null)
                yield return "L241 hands-premise-gone: Equipment.HasEnoughHandsToUse no longer exists. It is the " +
                             "test GetDisabledStateInternal:419 runs on the ability's OWN equipment, which is " +
                             "why resolving the wrong instance answers about the wrong gun.";
            if (!typeof(TacticalAbility).IsAssignableFrom(typeof(ReloadAbility)) ||
                typeof(ShootAbility).IsAssignableFrom(typeof(ReloadAbility)))
                yield return "L241 reload-premise-gone: ReloadAbility is no longer a plain TacticalAbility. It " +
                             "never derived from ShootAbility, which is why the `ability is ShootAbility` " +
                             "narrowing added in 8e62eac could not have caused this bug — re-read that before " +
                             "blaming the gate filter again.";
            if (typeof(CommonItemData).GetProperty("CurrentCharges", BindingFlags.Public | BindingFlags.Instance) == null ||
                typeof(TacticalAbility).GetMethod("GetRequiredCharges", BindingFlags.Public | BindingFlags.Instance) == null)
                yield return "L241 charges-premise-gone: CommonItemData.CurrentCharges or " +
                             "TacticalAbility.GetRequiredCharges no longer resolves. Charges are read off the " +
                             "ABILITY'S OWN equipment (GetDisabledStateInternal:439-441), so the instance the " +
                             "rail resolves decides which magazine is filled — the assertion that the pistol's " +
                             "charges rise rests on that.";

            // ── (a) THE WHOLE REFUSAL, EXECUTED ──────────────────────────────
            foreach (var v in ScanOutcome(TacticalCommandSync.CandidateMatchesSelection, "the shipped resolver"))
                yield return v;

            // ── (b) THE GATE IS ASKED ABOUT THE RESOLVED INSTANCE ────────────
            foreach (var v in ScanOrder(hostSite, "HandleActivate")) yield return v;

            // ── (d) POSITIVE CONTROL, EXECUTED ───────────────────────────────
            var control = ScanOutcome(FakeSeam.Rule, "FakeSeam.Rule")
                .Concat(ScanOrder(typeof(FakeSeam).GetMethod("Validate", AllMembers), "FakeSeam.Validate"))
                .ToList();
            foreach (var want in new[] { "reload-refused-for-the-wrong-weapon", "gate-asked-before-resolving" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L241 control-not-red: FakeSeam commits " + want + " and the scan did not flag " +
                                 "it, so that arm cannot tell a pistol reload that works from one refused on the " +
                                 "rifle's behalf.";
        }

        /// <summary>Arm (a) — the log of 02:45:43, replayed through the shipped decisions. Two weapons, the
        /// pistol selected, arms broken so only the two-handed rifle fails the hands test.</summary>
        private static IEnumerable<string> ScanOutcome(Func<bool, object, object, bool> resolver, string label)
        {
            object rifle = new object(), pistol = new object();
            object selected = pistol;
            // SLOT ORDER, primary first — this is the walk GetAbilityFiltered:211-221 does, and taking the
            // first candidate the rule accepts is exactly how ResolveAbility's preferred pass runs. When no
            // candidate is preferred it falls back to first-match, which is the pre-fix behaviour.
            object[] candidates = { rifle, pistol };
            object picked = null;
            foreach (var c in candidates)
                if (resolver(true, c, selected)) { picked = c; break; }
            if (picked == null) picked = candidates[0];
            bool picksPistol = ReferenceEquals(picked, pistol);
            bool picksRifle = ReferenceEquals(picked, rifle);
            // The gate answers about the instance that was resolved, and about nothing else.
            string disabled = picksPistol ? null : Hands;
            string refusal = TacticalCommandSync.Validate(
                actorFound: true, actorAlive: true, actorIsPlayerControlled: true, factionIsPlayingTurn: true,
                abilityFound: true, abilityIsRider: true, actorBusy: false, abilityDisabledReason: disabled,
                targetIsOffered: true, actionPoints: 4f, actionPointCost: 1f, willPoints: 4f, willPointCost: 0f);

            if (refusal != null)
                yield return "L241 reload-refused-for-the-wrong-weapon: with " + label + ", a soldier holding " +
                             "the PISTOL has his reload refused — \"" + refusal + "\"" +
                             (picksRifle ? " — because the resolver answered with the RIFLE's instance" : "") +
                             ". That is the 2026-08-08 report verbatim: five reloads refused with " + Hands +
                             " while the weapon in his hands needed one hand and was perfectly reloadable.";

            // AND THE RULE ITSELF STAYS. The rifle must still refuse — this arm is what stops a future
            // "fix" that simply stops asking the gate.
            string rifleRefusal = TacticalCommandSync.Validate(
                actorFound: true, actorAlive: true, actorIsPlayerControlled: true, factionIsPlayingTurn: true,
                abilityFound: true, abilityIsRider: true, actorBusy: false, abilityDisabledReason: Hands,
                targetIsOffered: true, actionPoints: 4f, actionPointCost: 1f, willPoints: 4f, willPointCost: 0f);
            if (rifleRefusal == null || rifleRefusal.IndexOf(Hands, StringComparison.Ordinal) < 0)
                yield return "L241 broken-arms-were-relaxed-away: Validate no longer refuses an ability the " +
                             "game's own gate answered " + Hands + " for (it returned " +
                             (rifleRefusal == null ? "ACCEPTED" : "\"" + rifleRefusal + "\"") + "). A soldier " +
                             "with broken arms genuinely cannot hold his rifle; making the symptom disappear by " +
                             "dropping the gate's answer is the one repair this arc forbids.";
        }

        /// <summary>Arm (b) — resolve, THEN ask the gate; and never ask the raw first-match lookup.</summary>
        private static IEnumerable<string> ScanOrder(MethodBase site, string label)
        {
            if (site == null)
            {
                yield return "L241 gate-asked-before-resolving: " + label + " does not resolve.";
                yield break;
            }
            var seq = Program.Callees(site, typeof(TacticalCommandSync).Assembly).ToList();
            int iResolve = seq.FindIndex(c => c.Name == "ResolveAbility");
            var game = Program.Callees(site, typeof(ActorComponent).Assembly).ToList();
            int iGate = game.FindIndex(c => c.Name == "GetDisabledState");
            if (iResolve < 0)
                yield return "L241 gate-asked-before-resolving: " + label + " does not call ResolveAbility " +
                             "(resolve@" + iResolve + ", gate@" + iGate + "). The gate is then asked about " +
                             "whichever instance shares the guid first — the rifle — so a pistol reload is " +
                             "refused for hands the pistol never needed.";
            if (game.Any(c => c.Name == "GetAbilityFiltered"))
                yield return "L241 gate-asked-before-resolving: " + label + " still resolves with " +
                             "GetAbilityFiltered, which returns the FIRST match. Reload_AbilityDef mints one " +
                             "instance per reloadable item, so the gate answers about the primary.";
        }

        /// <summary>THE BROKEN SHAPE, COMPILED — first-match resolution and a gate asked without resolving.</summary>
        private sealed class FakeSeam
        {
            internal static bool Rule(bool defMatches, object source, object selected) => defMatches;

            internal static AbilityDisabledState Validate(TacticalAbility ability) => ability.GetDisabledState();
        }
    }
}
