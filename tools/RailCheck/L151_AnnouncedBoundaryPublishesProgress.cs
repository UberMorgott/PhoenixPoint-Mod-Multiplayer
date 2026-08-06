using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L151 — A PEER WAITING ON ANOTHER PEER'S LOAD SEES THAT LOAD ADVANCE, AND NOTHING WAITS FOR IT.
    ///
    /// THE REPORT (2026-08-06): "when the host is loading, the other players should see the progress of his
    /// loading — right now the client correctly goes to a loading screen, but it just says waiting for the
    /// host." L118 had already built the whole mirror — the host samples its own native bar, publishes it as
    /// a roster row, and the waiting client drives the game's own loading bar from it — but it opened that
    /// publish window from ONE place, <c>OpenTacticalEntryBarrier</c>'s <c>_hostEntryHold</c>. The other two
    /// boundaries announce through <c>BroadcastLoadBoundaryBegin</c> (the lobby PLAY press and the
    /// new-campaign arm, both wired in L143), and through those the host published NOTHING.
    ///
    /// MEASURED, both sides of the same run (client clock runs +7.93 s, aligned on the boundary packet):
    ///   host   22:25:13.567  load boundary (new-campaign): broadcasting EntryTransferBegin
    ///   host   22:25:16.956  live progress bar captured        ← the host's own load is running
    ///   host   22:25:27.734  RosterProgress SEND []            ← FIRST send, 14.2 s later, and empty
    ///   client 22:25:21.499  load boundary BEGUN — dropping the curtain now
    ///   client 22:25:35.680  RosterProgress RECV []            ← 14.2 s of an empty bar
    /// The client's mirror branch was running the whole time and reading <c>_tracker.Get(0)</c>, which
    /// nobody was writing. An empty bar is what the user saw, and "waiting for host" is what it said.
    ///
    /// THE RULE: THE ANNOUNCEMENT AND THE PUBLISH WINDOW ARE THE SAME EVENT. A host that tells every peer
    /// to curtain owes them a number from that instant until the reveal. So the window is armed where the
    /// boundary is announced, not where one particular barrier is opened.
    ///
    /// THE ARMS:
    ///   (a) <c>announce-opens-no-window</c> — EXECUTED on a real coordinator: announcing a boundary must
    ///       ARM the publish window, and it must not have been armed before (a gate that is always open is
    ///       not a gate — it would broadcast through every host load, session or not).
    ///   (b) <c>window-never-closes</c> — EXECUTED: the abort that takes the same curtain back down closes
    ///       it, and the reveal closes it. A window that stays open leaves the host broadcasting roster
    ///       snapshots forever and pins the NEXT boundary's arm-time reset.
    ///   (c) <c>gate-ignores-the-window</c> — the predicate the pump and the broadcast consult must READ
    ///       the flag. Arming something nothing reads is the same silence with an extra field.
    ///   (d) <c>stale-terminal-pins-the-bar</c> — EXECUTED on the real monotone <c>Merge</c>: the previous
    ///       load's terminal <c>(1,100)</c> REJECTS every phase-0 sample, so the announce must clear the
    ///       tracker or the client renders the host row FULL before the host has loaded a thing. This is
    ///       L118 arm (c) restated at the seam L118 did not cover.
    ///   (e) <c>progress-is-a-gate</c> — THE OTHER HALF, EXECUTED BOTH WAYS. Nothing may wait on a progress
    ///       packet: a peer that received ZERO of them must still release (done is event-driven,
    ///       LoadComplete), and a roster at 100% with no done-marks must NOT release. Plus the structural
    ///       half — the barrier release must not read the percent at all.
    ///
    /// Falsify: drop the arm in <c>BroadcastLoadBoundaryBegin</c> → (a); drop either clear → (b); take
    /// <c>_loadBoundaryAnnounced</c> out of <c>HostEntryLoad</c> → (c); drop the <c>_tracker.Reset()</c> at
    /// the announce → (d); make <c>TryReleaseBarrier</c> consult <c>Get</c>, or make <c>AllDone</c> answer
    /// from percent → (e).
    /// </summary>
    internal static class L151_AnnouncedBoundaryPublishesProgress
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var announce = coord.GetMethod("BroadcastLoadBoundaryBegin", All);
            var abort = coord.GetMethod("BroadcastLoadBoundaryAbort", All);
            var lift = coord.GetMethod("PerformDeferredLift", All);
            var release = coord.GetMethod("TryReleaseBarrier", All);
            var gate = coord.GetProperty("HostEntryLoad", All)?.GetGetMethod(true);
            var armed = coord.GetField("_loadBoundaryAnnounced", All);
            var tracker = coord.GetProperty("Tracker", All);

            var trackerT = typeof(RosterProgressTracker);
            var merge = trackerT.GetMethod("Merge", All);
            var get = trackerT.GetMethod("Get", All);

            if (announce == null || abort == null || lift == null || release == null || gate == null ||
                armed == null || tracker == null || merge == null || get == null)
            {
                yield return "L151 premise-changed: SaveTransferCoordinator.{BroadcastLoadBoundaryBegin," +
                             "BroadcastLoadBoundaryAbort,PerformDeferredLift,TryReleaseBarrier,HostEntryLoad," +
                             "_loadBoundaryAnnounced,Tracker} or RosterProgressTracker.{Merge,Get} no longer " +
                             "resolves. The host's publish window has moved and this law is asserting " +
                             "something about a shape it no longer has.";
                yield break;
            }

            foreach (var v in WindowOpensAtTheAnnouncement(coord, announce, abort, lift, armed, tracker)) yield return v;

            // ── (c) the gate must actually consult the window ──────────────
            if (!Program.ReadsField(gate, armed))
                yield return "L151 gate-ignores-the-window: HostEntryLoad does not read _loadBoundaryAnnounced, " +
                             "so the two seams that announce through BroadcastLoadBoundaryBegin — the lobby PLAY " +
                             "press and the new-campaign arm — are outside the publish window again. The pump " +
                             "will not sample and the snapshot gate will not send, and every waiting peer is " +
                             "back on the empty bar measured at 22:25:13.567→22:25:27.734 (14.2 s of silence).";

            foreach (var v in NothingWaitsOnAPercent(release, get)) yield return v;
        }

        /// <summary>ARMS (a), (b) and (d), EXECUTED on a real <see cref="SaveTransferCoordinator"/>. The
        /// coordinator's constructor takes an engine and subscribes to one event — no Unity object is
        /// needed to drive the flag, and a null transport makes every broadcast in these methods a no-op,
        /// so the announce/abort pair runs for real.</summary>
        private static IEnumerable<string> WindowOpensAtTheAnnouncement(
            Type coord, MethodInfo announce, MethodInfo abort, MethodInfo lift, FieldInfo armed, PropertyInfo trackerProp)
        {
            object coordinator = null;
            string buildFailure = null;
            try
            {
                var engine = Activator.CreateInstance(typeof(NetworkEngine), true);
                typeof(NetworkEngine).GetProperty("IsHost", All).GetSetMethod(true).Invoke(engine, new object[] { true });
                coordinator = Activator.CreateInstance(coord, new[] { engine });
            }
            catch (Exception e) { buildFailure = (e.InnerException ?? e).Message; }

            if (coordinator == null)
            {
                yield return "L151 premise-changed: could not build a host SaveTransferCoordinator to execute " +
                             "the publish window against (" + buildFailure + "). The arms " +
                             "below are the only executed evidence this law has.";
                yield break;
            }

            if ((bool)armed.GetValue(coordinator))
                yield return "L151 announce-opens-no-window: the publish window is armed on a coordinator that " +
                             "has announced NOTHING. A window that is always open is not a window: the host " +
                             "would sample and broadcast roster snapshots through any load it ever performs, " +
                             "session or no session.";

            // ARM (d) — the previous load's terminal row must not survive into the new one. Merge is
            // monotone, so prove the rejection on the REAL tracker first, then demand the announce clears it.
            var tracker = (RosterProgressTracker)trackerProp.GetValue(coordinator, null);
            tracker.Merge(0, 1, 100);                       // last load's terminal row for the host slot
            if (tracker.Merge(0, 0, 42))
                yield return "L151 premise-changed: RosterProgressTracker.Merge accepted a phase-0 sample over " +
                             "a phase-1 terminal. Merge is supposed to be monotone on phase — if it is not, " +
                             "arm (d) is asserting a hazard that no longer exists and L118 arm (b) is wrong too.";

            string announceThrew = null;
            try { announce.Invoke(coordinator, new object[] { "law-151" }); }
            catch (Exception e) { announceThrew = (e.InnerException ?? e).Message; }
            if (announceThrew != null)
            {
                yield return "L151 announce-opens-no-window: BroadcastLoadBoundaryBegin threw on a host with no " +
                             "transport (" + announceThrew + "). The announcement runs before " +
                             "anything is connected on the new-campaign seam; it must never depend on a link.";
                yield break;
            }

            if (!(bool)armed.GetValue(coordinator))
                yield return "L151 announce-opens-no-window: the host announced a load boundary — every peer is " +
                             "now sitting on a curtain because of it — and its own publish window stayed SHUT. " +
                             "That is the reported bug exactly: the client curtains, the host loads for 14.2 s, " +
                             "and not one progress row leaves the host (measured 22:25:13.567 → 22:25:27.734).";

            if (tracker.Get(0).percent != 0)
                yield return "L151 stale-terminal-pins-the-bar: the announce left the previous load's terminal " +
                             "row (1,100) in the tracker at slot " + 0 + " (now " + tracker.Get(0).percent + "%). " +
                             "Merge is monotone, so every phase-0 sample the pump is about to publish is " +
                             "REJECTED and each waiting peer renders the host's row FULL from the first frame " +
                             "of a load that has not started — the reported bug inverted, which is not a fix.";

            // ARM (b) — both closers. Abort first (it is the same-curtain undo), then re-arm and reveal.
            abort.Invoke(coordinator, new object[] { "law-151" });
            if ((bool)armed.GetValue(coordinator))
                yield return "L151 window-never-closes: BroadcastLoadBoundaryAbort left the publish window open. " +
                             "The announced load will never happen — the host keeps broadcasting roster " +
                             "snapshots for a load nobody is performing, and the next boundary's arm-time reset " +
                             "is the only thing that would ever clear it.";

            announce.Invoke(coordinator, new object[] { "law-151-again" });
            try { lift.Invoke(coordinator, null); } catch { /* the reveal touches UI; the flag write is what we assert */ }
            if ((bool)armed.GetValue(coordinator))
                yield return "L151 window-never-closes: the reveal (PerformDeferredLift) left the publish window " +
                             "open. The window's whole span is announcement→reveal; leaving it armed after the " +
                             "curtain comes up keeps the snapshot broadcast alive for the rest of the session.";
        }

        /// <summary>ARM (e), EXECUTED BOTH WAYS — NOTHING GATES ON A PROGRESS PACKET. Progress is DISPLAY.
        /// The reveal is released by an EVENT (LoadComplete → MarkDone), so a peer that never received a
        /// single roster snapshot must still release, and a roster reporting 100% everywhere must not.</summary>
        private static IEnumerable<string> NothingWaitsOnAPercent(MethodInfo release, MethodInfo get)
        {
            var slots = new byte[] { 0, 1, 2 };

            var noPacketsEverArrived = new RosterProgressTracker();
            foreach (var s in slots) noPacketsEverArrived.MarkDone(s);   // LoadComplete, the event
            if (!noPacketsEverArrived.AllDone(slots))
                yield return "L151 progress-is-a-gate: every slot reported load-complete and the barrier still " +
                             "will not release, because no progress row was ever merged. Progress is display " +
                             "only — a peer whose snapshots were all dropped (they ride the UNRELIABLE fan-out) " +
                             "would then be stuck on the loading screen forever, which is the one thing the " +
                             "mirrored bar must never be able to cause.";

            var everyoneAtFullPercent = new RosterProgressTracker();
            foreach (var s in slots) everyoneAtFullPercent.Merge(s, 1, 100);   // display says 100%
            if (everyoneAtFullPercent.AllDone(slots))
                yield return "L151 progress-is-a-gate: a roster showing 100% everywhere released the barrier " +
                             "without one LoadComplete. The native bar holds at ~1.0 while the scene is still " +
                             "instantiating (that is why done is reported at OnReachedPlaying and not from a " +
                             "percent), so releasing on the number reveals a world that is not built yet.";

            var mod = typeof(SaveTransferCoordinator).Assembly;
            if (Program.Callees(release, mod).Any(c => c.MetadataToken == get.MetadataToken && c.Module == get.Module))
                yield return "L151 progress-is-a-gate: TryReleaseBarrier consults RosterProgressTracker.Get. The " +
                             "release must read the done-set and nothing else; the moment a percent can decide " +
                             "it, a display value has become a blocker and a lost snapshot can hang the session.";
        }
    }
}
