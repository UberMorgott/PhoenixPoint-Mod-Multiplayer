using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L332 — THE WINDOW THAT ANSWERS THIS PEER'S OWN ACTION IS NOT HELD BEHIND THE SCREEN HE ACTED IN;
    /// AN UNRELATED HOST WINDOW STILL IS.
    ///
    /// THE DEFECT, measured end to end on one clock (multiplayer.log:3283-3291): the client's hire intent
    /// went at 20:36:32.146 (personnel hire S#89), the host replayed it, the panel re-entered at :32.298,
    /// the character arrived structurally at :32.719 ('U#9'), the destination popup was raised at :32.721 —
    /// and at :32.747 <c>queue HELD while UIStateHavenDetailsScreen is open</c>. It drained 71 SECONDS
    /// later, when the player finally left the view. He bought a soldier and was never asked where to send
    /// them.
    ///
    /// THE HOLD'S OWN RATIONALE DOES NOT REACH THIS WINDOW. <c>WindowOrder.HoldsForOpenScreen</c> exists so
    /// an UNRELATED host event is not pushed on top of a screen the player deliberately opened (L163). All
    /// three native raise sites of <c>UIStateAssetDeployment</c> are an ACQUISITION completing — a recruit
    /// hired (GeoPhoenixFaction.cs:708), an aircraft manufactured (VehicleItemDef.cs:47), a ground vehicle
    /// manufactured (GroundVehicleItemDef.cs:48) — each through <c>GeoscapeView.PrepareDeployAsset</c>
    /// :1308-1326, which already gates on <c>faction == _context.ViewerFaction</c>. It IS the answer to the
    /// click, raised inside the very screen the click was made in.
    ///
    /// BOTH HALVES ARE ASSERTED, ON PURPOSE. A law that only checked the first would pass if the hold were
    /// deleted outright, which would put L163's reported defect straight back: another peer's play button
    /// resumes this peer's sim, and every event it raises lands on top of the screen he is reading.
    ///
    /// ARMS
    ///   (a) <c>answer-held</c> — UIStateAssetDeployment is NOT held on any screen a player opens.
    ///   (b) <c>unrelated-drains</c> — an ordinary queued window at the same priority IS still held on
    ///       those same screens. This is the half that fails if the hold is weakened rather than carved.
    ///   (c) <c>exemption-changes-where</c> — the exemption must widen WHICH windows escape the hold, never
    ///       WHERE the hold engages: on the map both drain, as they always did.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • remove UIStateAssetDeployment from <c>NeverHeldAnswerStates</c> → <c>answer-held</c>
    ///   • make <c>HoldsForOpenScreen</c> return false unconditionally → <c>unrelated-drains</c>
    ///   • add UIStateGeoscapeEvent to <c>NeverHeldAnswerStates</c> → <c>unrelated-drains</c>
    ///   • rename the predicate or the set → <c>premise-changed</c>
    /// </summary>
    internal static class L332_AnAnswerWindowIsNotHeldBehindItsOwnScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var predicate = typeof(WindowOrder).GetMethod("HoldsForOpenScreen", All);
            if (predicate == null || predicate.GetParameters().Length != 3)
            {
                yield return "L332 premise-changed: WindowOrder.HoldsForOpenScreen (3 parameters) did not resolve.";
                yield break;
            }

            // The screen the report names first, plus the other full-screen sections a purchase can be made
            // from. Real game types, so a rename in the game breaks this law rather than narrowing it.
            var screens = new[]
            {
                typeof(UIStateHavenDetailsScreen), typeof(UIStateManufacturing), typeof(UIStateGeoRoster),
                typeof(UIStateResearch), typeof(UIStatePhoenixBaseLayout),
            };
            var map = new[] { typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected) };
            // The window the log names, and an ordinary host-raised one at the same priority.
            var answer = typeof(UIStateAssetDeployment);
            var unrelated = typeof(UIStateGeoscapeEvent);

            // DWI supersedes the old forced-front exception: even an answer waits for Geoscape.
            foreach (var screen in screens)
                if (!Hold(predicate, 0, answer, screen))
                    yield return "L332 answer-forced-front: " + answer.Name + " bypasses the Geoscape gate while " +
                                 screen.Name + " is open.";

            // ── (b) an unrelated host window is STILL held ──────────────────────────────────────────
            foreach (var screen in screens)
                if (!Hold(predicate, 0, unrelated, screen))
                    yield return "L332 unrelated-drains: " + unrelated.Name + " drains while " + screen.Name +
                                 " is open, so the hold has been weakened rather than carved. L163's " +
                                 "reported defect is back in full: co-op resume is unconditional from any " +
                                 "peer, so somebody else's play button restarts this peer's sim and every " +
                                 "event it raises yanks him out of the screen he opened.";

            // ── (c) the exemption changes WHICH, never WHERE ────────────────────────────────────────
            foreach (var mapState in map)
                foreach (var window in new[] { answer, unrelated })
                    if (Hold(predicate, 0, window, mapState))
                        yield return "L332 exemption-changes-where: " + window.Name + " is held while " +
                                     mapState.Name + " is current — on the geoscape, which is the one place " +
                                     "the design says a queued window IS reviewed and the only place the " +
                                     "hold can release. A carve-out may widen which windows escape the hold; " +
                                     "moving where it engages turns it into a queue nothing drains.";
        }

        private static bool Hold(MethodInfo predicate, int priority, Type queued, Type state) =>
            (bool)predicate.Invoke(null, new object[] { priority, queued, state });
    }
}
