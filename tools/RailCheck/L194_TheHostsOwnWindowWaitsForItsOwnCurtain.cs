using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L194 — A HOST-LOCAL RAISE IS QUEUED ONLY AFTER THIS HOST'S OWN REVEAL.
    ///
    /// THE REPORT (2026-08-08, three peers, one campaign start). The HOST saw three intro dialogs
    /// (<c>IntroBetterGeo_0/1/2</c>) and THEN the intro cutscene. Both CLIENTS saw the cutscene and THEN the
    /// three dialogs. Contents identical, order inverted — and all four requests are priority 0, so
    /// <c>GeoscapeViewSwitchQuery</c> serves them in INSERT order and insert order was the only difference.
    ///
    /// WHY THE TWO ROLES INSERTED DIFFERENTLY. A client cannot carry a window while it is still at the
    /// loading screen, so its three mirrored raises sat in <c>EventPopup._held</c> and were replayed AFTER
    /// the reveal — i.e. after the cutscene had been queued. The host's geoscape is fully live behind its own
    /// curtain, and <c>GeoscapeView.Update</c> keeps calling <c>ProcessQueriedStateSwitch</c> throughout it:
    /// the curtain hides the screen, it does not stop the queue. So the host queued AND SERVED all three
    /// before the cutscene existed. <c>DrainHeldRaises</c> made that explicit — it EXEMPTED the host outright
    /// (<c>|| engine.IsHost</c> → <c>_held.Clear()</c>) — while nothing anywhere held a host-local raise.
    ///
    /// THE FIX IS THE WAIT THE HOST ALREADY IMPOSES ON EVERYONE ELSE, taken out of state it already owns:
    /// <c>SaveTransferCoordinator.Revealed</c>. A raise that arrives behind the curtain is PARKED and
    /// re-issued through the GAME'S OWN handler (<c>GeoscapeView.OnGeoscapeEventRaised</c>, re-invoked, never
    /// re-implemented — the game itself calls it out of band at <c>ToMarketplace</c>:737) once the curtain
    /// lifts. Every peer then inserts its dialogs after its own reveal, and the intro is first everywhere.
    ///
    /// NOT A QUORUM (P13), and arm (e) keeps it that way. Every term is this peer's own: its session flag,
    /// its role, its own reveal. That reveal is released by the load barrier
    /// (<c>AllDone(GetRosterSlots())</c> → <c>RevealAll</c>), which ends by itself and SHRINKS when a peer
    /// drops — a wait on a LOAD, never on a human ACTING.
    ///
    /// L189 covers the receiver's half of this shape and cannot see any of it: it is about a push the
    /// RECEIVING peer could not take. This is about a window the PRODUCING peer took too early.
    ///
    /// Falsify (each verified RED, then restored): restore <c>|| engine.IsHost</c> in
    /// <c>DrainHeldRaises</c> → <c>host-exempt-from-its-own-hold</c>; delete the prefix → <c>raise-is-never-
    /// parked</c>; invert <c>HostRaiseWaitsForReveal</c> either way → (a); let the postfix broadcast a parked
    /// raise → <c>parked-raise-is-mirrored-early</c>; re-implement the queueing instead of re-invoking the
    /// game's handler → <c>replay-is-not-the-games-own-raise</c>.
    /// </summary>
    internal static class L194_TheHostsOwnWindowWaitsForItsOwnCurtain
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var popup = typeof(EventPopup);
            var mod = popup.Assembly;

            var waits = popup.GetMethod("HostRaiseWaitsForReveal", All);
            var hold = popup.GetMethod("HoldHostRaiseBehindCurtain", All);
            var drainHost = popup.GetMethod("DrainHostCurtained", All);
            var drain = popup.GetMethod("DrainHeldRaises", All);
            var broadcast = mod.GetType("Multiplayer.Network.Sync.EventRaiseBroadcast");
            var prefix = broadcast?.GetMethod("Prefix", All);
            var postfix = broadcast?.GetMethod("Postfix", All);

            if (waits == null || hold == null || drainHost == null || drain == null ||
                prefix == null || postfix == null || waits.GetParameters().Length != 3)
            {
                yield return "L194 premise-changed: EventPopup.{HostRaiseWaitsForReveal(3 params)," +
                             "HoldHostRaiseBehindCurtain,DrainHostCurtained,DrainHeldRaises} or " +
                             "EventRaiseBroadcast.{Prefix,Postfix} no longer resolves. The host-side curtain " +
                             "hold has been reshaped and every arm below would pass vacuously — which is " +
                             "exactly how the inverted intro order survived a green harness for a whole session.";
                yield break;
            }

            // ── (a) THE DECISION, EXECUTED ────────────────────────────────────────────
            Func<bool, bool, bool, bool> ask = (s, h, r) =>
                (bool)waits.Invoke(null, new object[] { s, h, r });

            if (!ask(true, true, false))
                yield return "L194 curtained-host-queues-immediately: a HOST in a session that has NOT revealed " +
                             "does not wait. Its window is then queued — and SERVED, the curtain does not stop " +
                             "GeoscapeView.Update — before the cutscene every client queues at ITS reveal, which " +
                             "is the 2026-08-08 report verbatim: host dialogs→cutscene, clients cutscene→dialogs.";
            if (ask(true, true, true))
                yield return "L194 revealed-host-still-waits: a host that HAS revealed is still made to wait, so " +
                             "its parked raises never drain and the windows are lost rather than reordered — the " +
                             "one outcome strictly worse than the bug this law is about.";
            if (ask(true, false, false))
                yield return "L194 client-double-held: a CLIENT is sent down the host park. Its raises are " +
                             "mirrored and already wait in EventPopup._held behind CanCarryWindow; a second hold " +
                             "keyed on a flag the client's own drain does not consult holds a window with " +
                             "nothing able to release it.";
            if (ask(false, true, false))
                yield return "L194 solo-host-waits-forever: a peer with no active session waits for a reveal that " +
                             "will never come. Solo has no curtain barrier at all, so this is every geoscape " +
                             "event in a single-player campaign, gone.";

            // ── (b) THE SEAMS: parked at the raise, drained at the reveal ─────────────
            if (!Program.Callees(prefix, mod).Any(c => Same(c, hold)))
                yield return "L194 raise-is-never-parked: EventRaiseBroadcast.Prefix no longer reaches " +
                             "HoldHostRaiseBehindCurtain, so arm (a) proves a decision nothing consults and the " +
                             "host queues its window behind its own curtain exactly as before.";
            if (!Program.Callees(drain, mod).Any(c => Same(c, drainHost)))
                yield return "L194 host-exempt-from-its-own-hold: EventPopup.DrainHeldRaises no longer reaches " +
                             "DrainHostCurtained. That is the original defect's own line — the drain walked away " +
                             "on IsHost and cleared the list — so a parked raise would be parked FOREVER, or " +
                             "never parked at all.";
            if (!Program.Callees(drainHost, mod).Any(c => Same(c, waits)))
                yield return "L194 drain-does-not-ask-the-reveal: DrainHostCurtained releases parked raises " +
                             "without consulting HostRaiseWaitsForReveal, so they land back in the queue while " +
                             "the host is still curtained and the park bought nothing.";

            // ── (c) THE REPLAY IS THE GAME'S OWN RAISE, not a second implementation ───
            var replayCalls = Program.CalleeSequence(drainHost);
            if (!replayCalls.Any(c => c.Name == "Invoke" || c.Name == "OnGeoscapeEventRaised"))
                yield return "L194 replay-is-not-the-games-own-raise: DrainHostCurtained no longer re-invokes " +
                             "GeoscapeView.OnGeoscapeEventRaised. Native decides the DISPLAY priority there " +
                             "(:2044 event-triggered 10, :2049/:2057 supersede 15) and picks the marketplace " +
                             "state; a hand-rolled QueryStateSwitch would drift from all of it the first time " +
                             "either side changed, and would queue every parked window at the wrong rank.";
            if (Program.Callees(drainHost, typeof(GeoscapeView).Assembly)
                       .Any(c => c.Name == "QueryStateSwitch"))
                yield return "L194 replay-is-not-the-games-own-raise: DrainHostCurtained queues the window " +
                             "ITSELF (QueryStateSwitch) instead of handing the event back to the game's own " +
                             "handler. See above — the priority rule is native's and must stay native's.";

            // ── (d) A PARKED RAISE IS NOT MIRRORED EARLY ──────────────────────────────
            // Harmony runs a postfix even when the prefix skipped the body. Broadcasting there would ship the
            // window to every client while the host still has not queued it — the SAME inversion, mirrored.
            if (!prefix.GetParameters().Any(p => p.Name == "__state") ||
                !postfix.GetParameters().Any(p => p.Name == "__state"))
                yield return "L194 parked-raise-is-mirrored-early: the Prefix/Postfix pair no longer shares a " +
                             "__state. Harmony runs the postfix even when the prefix returned false, so without " +
                             "it the host broadcasts a raise it has NOT queued: the clients insert the dialog " +
                             "before their cutscene while the host still has not inserted it at all, and the " +
                             "order is inverted the other way round.";

            // ── (e) NO QUORUM: the decision reads this peer and nothing else ──────────
            var peerTypes = new[] { "SessionManager", "PingTable", "PeerListEntry", "LobbyController",
                                    "RosterProgressTracker" };
            foreach (var c in Program.Callees(waits, mod))
                if (c.DeclaringType != null && peerTypes.Contains(c.DeclaringType.Name))
                    yield return "L194 hold-reads-a-peer: HostRaiseWaitsForReveal calls " + c.DeclaringType.Name +
                                 "." + c.Name + ". The moment this predicate reads another peer, \"the host's " +
                                 "window waits for its own curtain\" becomes \"the host's window waits for " +
                                 "somebody else\" — a wait on a PERSON, which P13 forbids outright (L84/L91).";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
