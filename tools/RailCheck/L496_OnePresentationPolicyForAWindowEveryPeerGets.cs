using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L496 — A WINDOW EVERY PEER GETS IS GATED BY ONE POLICY, NOT BY EACH SUBSYSTEM'S PRIVATE COPY OF IT.
    ///
    /// THE REPORT (owner, 2026-08-14): "the host saw a research-completed window and dismissed it; the ally
    /// saw the SAME window about a minute and a half later."
    ///
    /// WHAT THE LOGS SHOW, and it is why this law asserts a POLICY and not a latency. Client
    /// multiplayer.log, one clock: `ResearchSync CLIENT deferred 2 completed research window(s)` at
    /// 23:55:58.919 — i.e. the completions were ALREADY in this peer's mirror at that instant — and
    /// `presented complete PX_Alien_Fishman_ResearchDef` / `..._Mindfragger_...` at 23:56:11.960, the same
    /// millisecond as `sets=[SetVehicleSelectedControls]`. The pump added no delay of its own: it released
    /// on the frame the player walked back onto the map. The gap is therefore PRESENTATION and it is
    /// whatever time each peer spends inside a screen — which is P13 working, not a defect.
    ///
    /// THE DEFECT THAT IS REAL IS THE SECOND GATE. Two independent predicates answer "may a window take
    /// this peer's screen right now": the universal one at the game's own single drain
    /// (<see cref="WindowOrder.HoldsForOpenScreen"/>, which already holds the research modal — it is queued
    /// through <c>QueryStateSwitch</c> at priority 99, `Queuerd state switch UIStateGeoModal with priority
    /// 99`), and <see cref="DurableWindowRegistry.MayPresent(bool, Type)"/>, which
    /// <c>ResearchSync.PumpDeferredCompletions</c> consults before it will even queue one. Two predicates
    /// that must agree and are free not to is exactly how one peer's window becomes arbitrarily later than
    /// another's: widen one set, or narrow the other, and the research window alone starts waiting for a
    /// view transition no other window waits for. This law nails them together on every state a player can
    /// actually be standing in, in BOTH directions.
    ///
    /// NOT A QUORUM AND CANNOT BECOME ONE. Every value either predicate reads is this peer's own view
    /// state. The law asserts they agree with each other, never that a peer waits for another peer.
    ///
    /// THE ARMS (all EXECUTED over both real predicates and the real game types):
    ///   (a) <c>screen-policies-disagree</c> — on every screen a player opens, BOTH gates hold.
    ///   (b) <c>map-policies-disagree</c>, NON-VACUITY — on every state <c>DurableWindowRegistry.MapStates</c>
    ///       declares as the map, BOTH gates release. Arm (a) alone is satisfied by a pair that holds
    ///       everywhere, i.e. by windows that never open at all.
    ///   (c) <c>deferral-waits-for-a-batch</c> — the drain is reachable from the sync TICK as well as from
    ///       the rail apply. A deferral drained only by the next rail batch is late by however long the
    ///       rail is quiet, which on a paused geoscape is unbounded.
    ///
    /// UIStateLoading is deliberately not asserted: <see cref="WindowOrder"/> counts it as the map ("no
    /// player and no screen to protect") and the research gate does not, and no player is standing in it.
    /// Naming that difference here would freeze a transient state instead of the two screens' rule.
    ///
    /// Falsify (verified RED, then restored):
    ///   • add <c>UIStateResearch</c> to <c>DurableWindowRegistry.MapStates</c> → (a)
    ///   • remove <c>UIStateVehicleSelected</c> from <c>DurableWindowRegistry.MapStates</c> → (b)
    ///   • add <c>"UIStateResearch"</c> to <c>WindowOrder.MapStates</c> → (a)
    ///   • drop the <c>PumpDeferredCompletions</c> call from <c>ResearchSync.ClientTick</c> → (c)
    /// </summary>
    internal static class L496_OnePresentationPolicyForAWindowEveryPeerGets
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary><c>GeoscapeView.OnFactionResearchCompleted</c>:1987-1990 queues the completed modal at
        /// 99 — the rank the host's own log line names, so the universal gate is asked the same question
        /// the game asks it.</summary>
        private const int ResearchModalPriority = 99;

        internal static IEnumerable<string> Check()
        {
            var universal = typeof(WindowOrder).GetMethod("HoldsForOpenScreen", All);
            var research = typeof(DurableWindowRegistry).GetMethod("MayPresent",
                All, null, new[] { typeof(bool), typeof(Type) }, null);
            var mapStates = typeof(DurableWindowRegistry).GetField("MapStates", All);
            var tick = typeof(ResearchSync).GetMethod("ClientTick", All);
            var pump = typeof(ResearchSync).GetMethod("PumpDeferredCompletions", All);
            if (universal == null || research == null || mapStates == null || tick == null || pump == null)
            {
                yield return "L496 premise-changed: WindowOrder.HoldsForOpenScreen / " +
                             "DurableWindowRegistry.MayPresent(bool, Type) / DurableWindowRegistry.MapStates / " +
                             "ResearchSync.ClientTick / ResearchSync.PumpDeferredCompletions did not resolve. " +
                             "One of the two presentation gates has been renamed or removed, so every arm below " +
                             "would pass vacuously — and the failure is silent: one peer's shared window simply " +
                             "starts opening at a different moment from everybody else's.";
                yield break;
            }

            var screens = new[]
            {
                typeof(UIStateResearch), typeof(UIStateManufacturing), typeof(UIStatePhoenixBaseLayout),
                typeof(UIStateDiplomacy), typeof(UIStateGeoscapeLog), typeof(UIStateEditSoldier),
                typeof(UIStateGeoRoster), typeof(UIStateRosterRecruits), typeof(UIStateInterception),
            };

            // ── (a) on a screen the player opened, BOTH gates hold ─────────────────────────────────────
            foreach (var screen in screens)
            {
                bool universalHolds = Holds(universal, screen);
                bool researchHolds = !MayPresent(research, screen);
                if (universalHolds && researchHolds) continue;
                yield return "L496 screen-policies-disagree: on " + screen.Name + " the universal drain gate " +
                             (universalHolds ? "HOLDS" : "RELEASES") + " a research-completed modal while " +
                             "DurableWindowRegistry.MayPresent " + (researchHolds ? "HOLDS" : "RELEASES") +
                             " it. Two predicates answering the same question — may a window take this peer's " +
                             "screen — must give the same answer, or the one window every peer is supposed to " +
                             "see becomes the one window whose moment is decided per subsystem. The owner's " +
                             "2026-08-14 report is that shape: the host had dismissed a research window the " +
                             "ally had not been shown yet.";
            }

            // ── (b) NON-VACUITY: on the map BOTH gates release ─────────────────────────────────────────
            foreach (var name in (IEnumerable<Type>)mapStates.GetValue(null))
            {
                bool universalHolds = Holds(universal, name);
                bool researchHolds = !MayPresent(research, name);
                if (!universalHolds && !researchHolds) continue;
                yield return "L496 map-policies-disagree: on " + name.Name + " — a state " +
                             "DurableWindowRegistry.MapStates itself declares to be the geoscape map — the " +
                             "universal gate " + (universalHolds ? "HOLDS" : "RELEASES") + " and the research " +
                             "gate " + (researchHolds ? "HOLDS" : "RELEASES") + ". The map is the one place " +
                             "both are supposed to let a window through; a pair that agrees only by holding " +
                             "everywhere satisfies arm (a) and shows no window ever again.";
            }

            // ── (c) the drain does not wait for the next rail batch ────────────────────────────────────
            var mod = typeof(ResearchSync).Assembly;
            if (!Program.Callees(tick, mod).Any(c => Same(c, pump)))
                yield return "L496 deferral-waits-for-a-batch: ResearchSync.ClientTick does not call " +
                             "PumpDeferredCompletions, so a completion latched while this peer was inside a " +
                             "screen is released only by the NEXT rail batch. The geoscape clock can be paused " +
                             "by any peer and the rail then goes quiet for as long as it stays paused, so that " +
                             "is a wait with no bound — and the player's own walk back onto the map, which is " +
                             "the event the release is supposed to answer, produces no rail traffic at all.";
        }

        private static bool Holds(MethodInfo universal, Type state) =>
            (bool)universal.Invoke(null, new object[] { ResearchModalPriority, typeof(UIStateGeoModal), state });

        private static bool MayPresent(MethodInfo research, Type state) =>
            (bool)research.Invoke(null, new object[] { true, state });

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
