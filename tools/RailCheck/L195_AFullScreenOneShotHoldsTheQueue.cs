using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L195 — A MIRRORED WINDOW IS NOT RELEASED ON TOP OF A FULL-SCREEN ONE-SHOT.
    ///
    /// THE REPORT (2026-08-08). The campaign intro cutscene started, and 0-130 ms later all three
    /// <c>IntroBetterGeo</c> dialogs were released on top of it. That alone would be a pile-up the player
    /// clicks through; what made it unrecoverable is the second half — <c>UIStateGeoCutscene.EnterState</c>'s
    /// <c>SetInputState("Cutscene")</c> is STORED AND DISCARDED while a stale loading override is still held
    /// (<c>RevealInputLock</c>:19-23 documents it verbatim, <c>InputController.SetInputSets</c>:520-527 is the
    /// mechanism), so Escape does nothing and the peer sits through the entire video with three modals
    /// stacked behind it. L196 owns that latch; this law owns the release that put the modals there.
    ///
    /// WHY THE GATE COULD NOT SEE IT. <c>EventPopup.CanCarryWindow</c> asked exactly one question — has the
    /// event system finished loading — because the window it was written for (2026-08-04) was a peer whose
    /// <c>_events</c> was still null. "Ready to carry a window" and "not currently playing an unskippable
    /// video" are different questions, and the drain only ever asked the first.
    ///
    /// ONE TERM, AND IT IS A HOLD. The drain retries every tick, so a window that arrives during a cinematic
    /// is served the moment the cinematic ends — HELD, NOT LOST, the shape L189 already blesses. Keyed on
    /// <c>UIStateGeoCutscene</c> BY ASSIGNABILITY, the same state <c>WindowOrder</c> and
    /// <c>GeoWindowCoverage</c> already declare on, so a mod that subclasses it is covered without this file
    /// naming the mod.
    ///
    /// ARM (b) IS THE NON-VACUITY AND THE SAFETY HALF IN ONE: widen the term to "any view state" and the
    /// drain never runs again. It must answer FALSE for the map, for an ordinary window, and for no state
    /// at all.
    ///
    /// Falsify (each verified RED, then restored): delete the term from <c>CanCarryWindow</c> →
    /// <c>gate-ignores-the-one-shot</c>; make <c>OneShotHoldsWindow</c> return true unconditionally → (b) ×3;
    /// return false unconditionally → (a); pop the held entry before the gate is asked →
    /// <c>failed-carry-drops-the-raise</c>.
    /// </summary>
    internal static class L195_AFullScreenOneShotHoldsTheQueue
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var popup = typeof(EventPopup);
            var mod = popup.Assembly;

            var oneShot = popup.GetMethod("OneShotHoldsWindow", All);
            var canCarry = popup.GetMethod("CanCarryWindow", All);
            var drain = popup.GetMethod("DrainHeldRaises", All);

            if (oneShot == null || canCarry == null || drain == null ||
                oneShot.GetParameters().Length != 1)
            {
                yield return "L195 premise-changed: EventPopup.OneShotHoldsWindow(1 param), CanCarryWindow or " +
                             "DrainHeldRaises no longer resolves. The drain gate has been reshaped and every arm " +
                             "below would pass vacuously while three modals land on top of an unskippable video.";
                yield break;
            }

            Func<Type, bool> holds = t => (bool)oneShot.Invoke(null, new object[] { t });

            // ── (a) THE DECISION, EXECUTED ────────────────────────────────────────────
            if (!holds(typeof(UIStateGeoCutscene)))
                yield return "L195 one-shot-does-not-hold: OneShotHoldsWindow answers FALSE while " +
                             "UIStateGeoCutscene is the current view state. That is the 2026-08-08 report " +
                             "verbatim: the intro started and all three mirrored dialogs were released on top of " +
                             "it inside 130 ms, on a peer whose cutscene skip was already dead.";

            // ── (b) NON-VACUITY + SAFETY: it must hold ONLY the one-shot ──────────────
            foreach (var t in new[] { (Type)null, typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected),
                                      typeof(UIStateGeoscapeEvent), typeof(UIStateResearch) })
                if (holds(t))
                    yield return "L195 gate-holds-everything: OneShotHoldsWindow HELD for " +
                                 (t == null ? "NO current view state at all" : t.Name) + ". A term that is true " +
                                 "off the map, on an ordinary window, or before any view state exists is not a " +
                                 "one-shot test — it is a drain that never runs again, and every mirrored window " +
                                 "for the rest of the session is held with nothing able to release it.";

            // ── (c) THE SEAM: the gate the drain actually asks ────────────────────────
            if (!Program.Callees(canCarry, mod).Any(c => Same(c, oneShot)))
                yield return "L195 gate-ignores-the-one-shot: EventPopup.CanCarryWindow no longer consults " +
                             "OneShotHoldsWindow, so arm (a) proves a pure function nothing reads. Both carriers " +
                             "route through CanCarryWindow (a live raise and the held replay alike, L101's own " +
                             "arm), which is why the term belongs there and not in one of them.";

            // ── (d) HELD, NOT LOST: the gate is asked BEFORE the entry is popped ──────
            var order = Program.CalleeSequence(drain);
            int gateAt = order.FindIndex(m => m.MetadataToken == canCarry.MetadataToken &&
                                              m.Module == canCarry.Module);
            int popAt = order.FindIndex(m => m.Name == "RemoveAt");
            if (gateAt < 0 || (popAt >= 0 && popAt < gateAt))
                yield return "L195 failed-carry-drops-the-raise: DrainHeldRaises pops the held entry before (or " +
                             "without) asking CanCarryWindow. A hold that removes what it cannot place is the " +
                             "2026-08-04 loss with an extra list in it — and re-appending instead would reorder " +
                             "the queue, which is the one ordering these windows have.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
