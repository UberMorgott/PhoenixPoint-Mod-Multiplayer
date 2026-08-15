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
    /// THE DEFECT THAT IS REAL IS THE SECOND GATE, and the 2026-08-15 RCA closed it by DELETION rather
    /// than by agreement. Two independent predicates used to answer "may a window take this peer's screen
    /// right now": the universal one at the game's own single drain
    /// (<see cref="WindowOrder.HoldsForOpenScreen"/>, which already holds the research modal — it is queued
    /// through <c>QueryStateSwitch</c> at priority 99, `Queuerd state switch UIStateGeoModal with priority
    /// 99`), and <see cref="DurableWindowRegistry.MayPresent(bool, Type)"/>, which
    /// <c>ResearchSync.PumpDeferredCompletions</c> consulted before it would even queue one. THE HOST HAS
    /// ONLY THE FIRST — it raises natively — so the two channels queued the same window at different
    /// moments: both clients logged `deferred 2 completed research window(s)` at 13:24:45.450 from inside
    /// <c>UIStateGeoscapeEvent</c> while the host had already queued its own. A window queued at DRAIN time
    /// takes a different position from one queued at RAISE time, which is the order the owner reported —
    /// and a drain from <c>ClientTick</c> also runs with <c>RailOrdinal.Current == 0</c>, so it is keyed
    /// off this peer's own counter (L511 owns that half).
    ///
    /// SO THIS LAW NOW ASSERTS THE SURVIVING SINGLE-GATE POLICY, not the agreement of two. It is not a
    /// weakening: the P13 promise (a review window waits for the map) is still asserted here, EXECUTED,
    /// over every screen and every map state — only the second, redundant predicate is forbidden.
    ///
    /// NOT A QUORUM AND CANNOT BECOME ONE. Every value the gate reads is this peer's own view state.
    ///
    /// THE ARMS (all EXECUTED over the real predicate and the real game types):
    ///   (a) <c>screen-window-not-held</c> — on every screen a player opens, the ONE gate holds the
    ///       priority-99 research modal.
    ///   (b) <c>map-window-held</c>, NON-VACUITY — on every state <c>DurableWindowRegistry.MapStates</c>
    ///       declares as the map, the gate releases it. Arm (a) alone is satisfied by a gate that holds
    ///       everywhere, i.e. by windows that never open at all.
    ///   (c) <c>deferral-waits-for-a-batch</c> — the drain is reachable from the sync TICK as well as from
    ///       the rail apply. A latch drained only by the next rail batch is late by however long the
    ///       rail is quiet, which on a paused geoscape is unbounded.
    ///   (d) <c>second-gate-returned</c> — <c>PumpDeferredCompletions</c> must NOT consult
    ///       <c>DurableWindowRegistry.MayPresent</c> again. One presentation policy, one predicate; a
    ///       second one is free to disagree with the host's and that disagreement IS the order defect.
    ///
    /// <c>DurableWindowRegistry.MayPresent</c> itself stays — it is the first-map-surface latch's
    /// definition of "on the map" (L475), which is a different question from "may a window take the
    /// screen". UIStateLoading is deliberately not asserted: <see cref="WindowOrder"/> counts it as the map
    /// ("no player and no screen to protect") and no player is standing in it.
    ///
    /// Falsify (verified RED, then restored):
    ///   • add <c>"UIStateResearch"</c> to <c>WindowOrder.MapStates</c> → (a)
    ///   • remove <c>"UIStateVehicleSelected"</c> from <c>WindowOrder.MapStates</c> → (b)
    ///   • drop the <c>PumpDeferredCompletions</c> call from <c>ResearchSync.ClientTick</c> → (c)
    ///   • restore the <c>MayPresent</c> pre-gate inside <c>PumpDeferredCompletions</c> → (d)
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

            // ── (a) on a screen the player opened, the ONE gate holds ──────────────────────────────────
            foreach (var screen in screens)
            {
                if (Holds(universal, screen)) continue;
                yield return "L496 screen-window-not-held: on " + screen.Name + " the drain gate RELEASES a " +
                             "research-completed modal onto a screen the player opened. That is P13 broken " +
                             "for the one window family every peer is supposed to see at the same point in " +
                             "its own queue — and since this is now the ONLY gate in front of it (the " +
                             "PumpDeferredCompletions pre-gate was deleted 2026-08-15), nothing else is left " +
                             "to hold it.";
            }

            // ── (b) NON-VACUITY: on the map the gate releases ──────────────────────────────────────────
            foreach (var name in (IEnumerable<Type>)mapStates.GetValue(null))
            {
                if (!Holds(universal, name)) continue;
                yield return "L496 map-window-held: on " + name.Name + " — a state " +
                             "DurableWindowRegistry.MapStates itself declares to be the geoscape map — the " +
                             "drain gate HOLDS the research-completed modal. The map is the one place a " +
                             "notification IS reviewed; a gate that holds everywhere satisfies arm (a) and " +
                             "shows no window ever again.";
                // MayPresent is read here only as the repo's declaration of "this peer is on the map"
                // (L475's first-surface latch). It must still SAY yes there, or the two definitions have
                // drifted and arm (b) is asserting the map against a set nothing else agrees with.
                if (!MayPresent(research, name))
                    yield return "L496 map-declaration-drifted: DurableWindowRegistry.MayPresent refuses " +
                                 name.Name + ", a member of its own MapStates. That set is the first-map-" +
                                 "surface latch's definition of the map (L475); if it stops meaning the map, " +
                                 "arm (b) is checking the drain gate against nothing.";
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

            // ── (d) THE SECOND GATE STAYS DELETED ──────────────────────────────────────────────────────
            if (Program.Callees(pump, mod).Any(c => Same(c, research)))
                yield return "L496 second-gate-returned: ResearchSync.PumpDeferredCompletions consults " +
                             "DurableWindowRegistry.MayPresent again before it will queue the window. The HOST " +
                             "has no such pre-gate — it raises natively — so this peer queues the completion " +
                             "at DRAIN time while the host queued it at RAISE time, the two land in different " +
                             "positions of their own queues, and the window every peer is owed opens in a " +
                             "different order on each (owner's report, 2026-08-14; measured again 2026-08-15 " +
                             "with both clients deferring from UIStateGeoscapeEvent). One presentation policy " +
                             "means ONE predicate: the engine's own drain gate, asserted in arms (a) and (b).";
        }

        private static bool Holds(MethodInfo universal, Type state) =>
            (bool)universal.Invoke(null, new object[] { ResearchModalPriority, typeof(UIStateGeoModal), state });

        private static bool MayPresent(MethodInfo research, Type state) =>
            (bool)research.Invoke(null, new object[] { true, state });

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
