using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L243 — A REFUSED ORDER DOES NOT MOVE THE ACTING PEER'S SELECTION.
    ///
    /// L186 IS GREEN-WHILE-BROKEN, and this is the law it was missing. L186 asserts that the selection
    /// PROPAGATES — which it did, flawlessly, including propagating a selection the acting peer never made.
    /// Nothing asserted the published selection was the one the acting peer MEANT. Live, 2026-08-08:
    /// <c>02:45:42.816 select → PX_Pistol</c> ACCEPTED, then <c>02:45:43.965 command Reload_AbilityDef</c>,
    /// then <c>.970 CLIENT weapon switch applied → PX_SniperRifle</c> — the host resolved the reload to the
    /// rifle's instance (L240), <c>PreSelectSourceEquipment</c> dutifully selected THAT rifle before
    /// validating, the 0x82 mirror carried it to every peer, and the order was then refused anyway. The player
    /// saw his soldier swap to a gun he cannot hold, on a press that did nothing.
    ///
    /// THE BELT IS NOT WITHDRAWN, because it is not the defect. <c>PreSelectSourceEquipment</c> exists so the
    /// arbiter cannot refuse an order on <c>EquipmentNotSelected</c> — a state
    /// <c>TacticalAbility.Activate</c>:1087-1090 was about to rewrite one line later — and removing it would
    /// resurrect the 2026-07-31 grenade and reload rejects verbatim. With L240 in place it simply has nothing
    /// left to do for a peer's own click: the resolved instance IS the selected weapon's, so the guard sees
    /// <c>IsSelected</c> and stands down. Arm (a) asserts BOTH halves — it must not fire for the selected
    /// weapon, and it must still fire for a genuinely unselected source.
    ///
    /// WHAT THIS LAW ASSERTS.
    ///   (a) EXECUTED — <c>SelectionMoves</c>, the shipped pure decision, run to exhaustion. All four
    ///       polarities, so an implementation that always moves and one that never moves both go red.
    ///   (b) the belt really routes through that decision (a hand-rolled guard beside it would leave arm (a)
    ///       executing a rule nothing consults).
    ///   (c) the host RESOLVES before it pre-selects. Pre-selecting first would pick the weapon by first match
    ///       and then "confirm" it, which is the defect with the fix bolted on backwards.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> moves the selection unconditionally and
    ///       pre-selects before resolving; (a), (b) and (c) must go red on it.
    ///
    /// Falsify: make <c>SelectionMoves</c> return <c>true</c> unconditionally → (a); inline the guard back into
    /// <c>PreSelectSourceEquipment</c> → (b); move the <c>PreSelectSourceEquipment</c> call above the
    /// <c>ResolveAbility</c> call in <c>HandleActivate</c> → (c); empty <see cref="FakeSeam"/> → (d).
    /// </summary>
    internal static class L243_RefusalDoesNotMoveTheSelection
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var preSelect = sync.GetMethod("PreSelectSourceEquipment", AllMembers);
            var hostSite = sync.GetMethod("HandleActivate", AllMembers);
            var selected = typeof(Equipment).GetProperty("IsSelected", BindingFlags.Public | BindingFlags.Instance);
            var usableOff = typeof(TacticalAbility).GetProperty("UsableOnNonSelectedEquipment",
                                BindingFlags.Public | BindingFlags.Instance);
            if (preSelect == null || hostSite == null || selected == null || usableOff == null)
            {
                yield return "L243 premise-changed: TacticalCommandSync.{PreSelectSourceEquipment, " +
                             "HandleActivate}, Equipment.IsSelected or " +
                             "TacticalAbility.UsableOnNonSelectedEquipment no longer resolves. Those are the " +
                             "belt and the two facts it stands down on — with either gone, the arms below " +
                             "assert about a shape the build no longer has.";
                yield break;
            }

            foreach (var v in ScanRule(TacticalCommandSync.SelectionMoves, "SelectionMoves")) yield return v;
            foreach (var v in ScanBelt(preSelect, "PreSelectSourceEquipment")) yield return v;
            foreach (var v in ScanOrder(hostSite, "HandleActivate")) yield return v;

            var fake = typeof(FakeSeam);
            var control = ScanRule(FakeSeam.Rule, "FakeSeam.Rule")
                .Concat(ScanBelt(fake.GetMethod("PreSelect", AllMembers), "FakeSeam.PreSelect"))
                .Concat(ScanOrder(fake.GetMethod("Handle", AllMembers), "FakeSeam.Handle"))
                .ToList();
            foreach (var want in new[] { "refusal-moves-the-selection", "belt-bypasses-the-decision",
                                         "preselect-runs-before-resolving" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L243 control-not-red: FakeSeam commits " + want + " and the scan did not flag " +
                                 "it. That arm cannot tell a selection the acting peer chose from one the host " +
                                 "invented — which is exactly the blind spot that kept L186 green.";
        }

        /// <summary>Arm (a) — the decision, run to exhaustion. Argument order:
        /// (usableOnNonSelectedEquipment, hasSource, sourceIsSelected).</summary>
        private static IEnumerable<string> ScanRule(Func<bool, bool, bool, bool> rule, string label)
        {
            if (rule(false, true, true))
                yield return "L243 refusal-moves-the-selection: " + label + " moves the selection for an ability " +
                             "sourced from the weapon the peer ALREADY has selected. Since L240 that is the " +
                             "ordinary case for every click, so the host would re-select on every order and " +
                             "publish it on the 0x82 settle — including for orders it then REFUSES, which is " +
                             "the 02:45:43.970 \"CLIENT weapon switch applied → PX_SniperRifle\" line verbatim.";
            if (rule(false, false, false))
                yield return "L243 refusal-moves-the-selection: " + label + " moves the selection for an ability " +
                             "with NO source equipment at all. There is nothing to select, so this can only be " +
                             "a write of null or of whatever the belt reaches for next.";
            if (rule(true, true, false))
                yield return "L243 refusal-moves-the-selection: " + label + " moves the selection for a def that " +
                             "declares UsableOnNonSelectedEquipment. The game's own gate exempts exactly those " +
                             "(GetDisabledStateInternal:435), so the belt has nothing to repair and would be " +
                             "yanking a weapon out of a soldier's hands for an ability that never needed it.";
            if (!rule(false, true, false))
                yield return "L243 belt-withdrawn: " + label + " no longer moves the selection for a source the " +
                             "peer genuinely does NOT have selected. That is the case the belt exists for: " +
                             "Validate asks GetDisabledState BEFORE Activate:1087-1090 would select it, and " +
                             "EquipmentNotSelected then refuses an order the native path was one line from " +
                             "making legal — the 2026-07-31 grenade and reload rejects, verbatim. This law " +
                             "removes a spurious selection, it does not remove the belt.";
        }

        /// <summary>Arm (b) — the belt routes through the decision arm (a) executes.</summary>
        private static IEnumerable<string> ScanBelt(MethodBase preSelect, string label)
        {
            if (preSelect == null)
            {
                yield return "L243 belt-bypasses-the-decision: " + label + " does not resolve.";
                yield break;
            }
            if (!Program.Callees(preSelect, typeof(TacticalCommandSync).Assembly)
                        .Any(c => c.Name == "SelectionMoves"))
                yield return "L243 belt-bypasses-the-decision: " + label + " does not consult SelectionMoves, so " +
                             "arm (a) is executing a rule the shipped belt never asks — green decoration over " +
                             "whatever the inlined guard actually does.";
        }

        /// <summary>Arm (c) — resolve first, pre-select second.</summary>
        private static IEnumerable<string> ScanOrder(MethodBase site, string label)
        {
            if (site == null)
            {
                yield return "L243 preselect-runs-before-resolving: " + label + " does not resolve.";
                yield break;
            }
            var seq = Program.Callees(site, typeof(TacticalCommandSync).Assembly).ToList();
            int iResolve = seq.FindIndex(c => c.Name == "ResolveAbility");
            int iPre = seq.FindIndex(c => c.Name == "PreSelectSourceEquipment");
            if (iResolve < 0 || iPre < 0 || iResolve > iPre)
                yield return "L243 preselect-runs-before-resolving: the belt runs at or before the resolution " +
                             "(resolve@" + iResolve + ", preselect@" + iPre + ") in " + label + ". The belt reads " +
                             "the RESOLVED ability's source, so running it first means selecting whatever " +
                             "first-match-by-guid produced and then calling that the peer's choice — the defect " +
                             "with the fix bolted on backwards.";
        }

        /// <summary>THE BROKEN SHAPE, COMPILED: always move the selection, guard inlined, belt before resolve.</summary>
        private sealed class FakeSeam
        {
            internal static bool Rule(bool usableOnNonSelected, bool hasSource, bool sourceIsSelected) => true;

            internal static void PreSelect(TacticalAbility ability)
            {
                var eq = ability.OverrideEquipment ?? ability.EquipmentSource;
                if (eq != null && !eq.IsSelected) ability.TacticalActor.Equipments.SetSelectedEquipment(eq);
            }

            internal static void Handle(TacticalAbility ability) { PreSelect(ability); }
        }
    }
}
