using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Transport;

namespace RailCheck
{
    /// <summary>
    /// L120 — WHEN SOMEBODY GOES, EVERYONE IS TOLD, AND TOLD THE TRUTH.
    ///
    /// The ask (developer, 2026-08-05): "There should be a small notice to everyone if someone leaves — say
    /// they press Alt+F4, or the X, or go to the main menu via Escape, or quit the game… And the same must
    /// be shown if he was dropped by a network timeout."
    ///
    /// WHY IT IS TWO NOTICES AND NOT ONE. Earlier the same morning, commit b48756e collapsed all six peer-
    /// removal paths into a per-peer PAUSE: an involuntary loss keeps the roster row, the slot, the
    /// permissions and the guid binding, so the peer resumes its own seat when it comes back, and L84 forbids
    /// taking a seat away outside a VOLUNTARY leave. That makes "gone" and "left" two different facts. Only
    /// the leaver itself can establish the first one — its own ClientLeave packet, sent from the quit path —
    /// and everything else the host can observe is silence, which means "lost connection", not "left". A
    /// single merged notice would be a guess presented to four other players as news, and the guess would be
    /// wrong for exactly the case that matters: a player whose wifi blinked and who is coming back.
    ///
    /// THE ARMS:
    ///   (a) PREMISE — the notice family resolves at all.
    ///   (b) EXECUTED: THE TWO NOTICES ARE DIFFERENT CLAIMS. The formatters are run, and the leave line must
    ///       say "left" while the silence line must NOT — a law that only checked "a notice was sent" would
    ///       stay green through the exact failure this feature exists to avoid.
    ///   (c) EACH PATH EMITS ITS OWN, AND ONLY ITS OWN: the voluntary funnel (HandleLeave, reached only by
    ///       the leaver's own packet) formats the LEAVE line; the involuntary funnel (PausePeer, where every
    ///       socket death, stalled write and heartbeat timeout lands) formats the CONNECTION-LOST line; the
    ///       resume edge closes the loop. Crossed wires here are the whole bug.
    ///   (d) IT REACHES EVERY REMAINING PEER'S SCREEN, followed link by link across both roles: the host
    ///       fans the notice out AND paints its own (it receives none of its own packets), and a client
    ///       paints it ON ARRIVAL. Plus an EXECUTED codec round-trip, because a notice that loses its flag
    ///       on the wire arrives as an ordinary chat line into a log nobody has open — silent, which is this
    ///       repo's dominant bug class.
    ///   (e) A BACKLOG IS NOT NEWS: the history replay must not paint anything.
    ///   (f) THE ARM THAT MATTERS: the notice path REMOVES NOBODY and COUNTS NOBODY. Announcing is not
    ///       deciding. The moment the notice path frees a seat it has become a kick (L84), and the moment it
    ///       reads how many peers are left it has become a quorum (L91, L119).
    ///   (h) EXECUTED: EXACTLY ONE NOTICE PER DEPARTURE PER PEER, AND IT NAMES THE PLAYER. Every other arm
    ///       asks WHICH notice; none asked HOW MANY, and the answer was two — SessionNotifier announced the
    ///       same transport event HostLeaveHandler already announces, so a client whose host went away got
    ///       "— X left —" and "Host ended the session", both native prompts in tactical. The count is
    ///       DERIVED: run every formatter, keep the lines that claim a departure, and demand exactly one
    ///       announcer per departure FACT (the leaver's farewell, the host's silence). The name half is
    ///       executed too — a farewell arriving after the roster row is gone must still name the player
    ///       rather than "a player", which is the one case where the notice makes people count heads.
    ///   (g) THE FAREWELL IS ACTUALLY FLUSHED. Alt+F4 and the window X are the common case, and
    ///       DirectTransport only ENQUEUES for a background writer thread the process teardown then kills —
    ///       so the quit path must also drain, or the most common voluntary leave in the game is reported to
    ///       everybody else as a lost connection.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • make FormatConnectionLostNotice say "left"        → notices-not-distinguishable
    ///   • point HandleLeave at FormatConnectionLostNotice   → wrong-notice-for-the-path
    ///   • point PausePeer at FormatLeaveNotice              → wrong-notice-for-the-path
    ///   • drop the ShowToast call from HandleChat           → notice-not-on-screen
    ///   • drop IsNotice from the chat codec                 → notice-lost-on-the-wire
    ///   • let ReplayChatHistoryTo re-send notices           → backlog-replayed-as-news
    ///   • call RemoveClient from PausePeer                  → notice-path-removes-a-peer
    ///   • read ClientCount from BroadcastChat               → notice-path-counts-peers
    ///   • drop engine.Shutdown() from OnApplicationQuit     → farewell-not-flushed
    ///   • re-add SessionNotifier's own "— X left —" handler → two-notices-per-departure
    ///   • drop LeaverName's last-known fallback             → notice-does-not-name-the-player
    /// </summary>
    internal static class L120_LeaveNotice
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var session = typeof(SessionManager);
            var mod = session.Assembly;

            var pause = session.GetMethod("PausePeer", All);
            var resume = session.GetMethod("ResumePeer", All);
            var handleLeave = session.GetMethod("HandleLeave", All);
            var handleChat = session.GetMethod("HandleChat", All);
            var notice = session.GetMethod("SystemNotice", All);
            var broadcastChat = session.GetMethod("BroadcastChat", All);
            var replay = session.GetMethod("ReplayChatHistoryTo", All);
            var removeClient = session.GetMethod("RemoveClient", All);
            var clientCount = session.GetMethod("get_ClientCount", All);
            var toast = typeof(SessionNotifier).GetMethod("ShowToast", All);
            var quit = mod.GetType("Multiplayer.UI.MultiplayerUI")?.GetMethod("OnApplicationQuit", All);
            var shutdown = typeof(NetworkEngine).GetMethod("Shutdown", All);
            var sendLeave = session.GetMethod("SendClientLeave", All);
            var sendHostGone = session.GetMethod("SendHostDisconnected", All);

            var fLeave = typeof(SessionLifecycle).GetMethod("FormatLeaveNotice", All);
            var fLost = typeof(SessionLifecycle).GetMethod("FormatConnectionLostNotice", All);
            var fBack = typeof(SessionLifecycle).GetMethod("FormatReconnectedNotice", All);

            if (pause == null || resume == null || handleLeave == null || handleChat == null ||
                notice == null || broadcastChat == null || replay == null || removeClient == null ||
                toast == null || quit == null || shutdown == null || sendLeave == null ||
                sendHostGone == null || fLeave == null || fLost == null || fBack == null)
            {
                yield return "L120 premise-changed: the leave-notice family no longer resolves " +
                             "(SessionLifecycle.FormatLeaveNotice/FormatConnectionLostNotice/" +
                             "FormatReconnectedNotice, SessionManager.PausePeer/ResumePeer/HandleLeave/" +
                             "HandleChat/SystemNotice/BroadcastChat/ReplayChatHistoryTo/RemoveClient/" +
                             "SendClientLeave/SendHostDisconnected, SessionNotifier.ShowToast, " +
                             "MultiplayerUI.OnApplicationQuit, NetworkEngine.Shutdown). Every arm below " +
                             "would pass vacuously, so 'everyone is told, and told the truth' is UNCHECKED " +
                             "rather than satisfied";
                yield break;
            }

            // ═══ (b) EXECUTED: THE TWO NOTICES ARE DIFFERENT CLAIMS ═══
            const string who = "Zephyr";
            string left = SessionLifecycle.FormatLeaveNotice(who);
            string lost = SessionLifecycle.FormatConnectionLostNotice(who);
            string back = SessionLifecycle.FormatReconnectedNotice(who);

            foreach (var pair in new[] { new[] { "leave", left }, new[] { "connection-lost", lost },
                                         new[] { "reconnected", back } })
                if (string.IsNullOrEmpty(pair[1]) || pair[1].IndexOf(who, StringComparison.Ordinal) < 0)
                    yield return "L120 notice-does-not-name-the-player: the " + pair[0] + " notice came out " +
                                 "as '" + (pair[1] ?? "null") + "' and does not contain the player's name. " +
                                 "'Somebody left' in a five-player session tells four people to go and count " +
                                 "heads, which is the one thing the notice exists to save them from";

            if (lost.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
                yield return "L120 notices-not-distinguishable: the CONNECTION-LOST notice ('" + lost + "') " +
                             "claims the player LEFT. It did not — nothing was heard from it, its seat, slot, " +
                             "permissions and identity binding are all still held for it (L84), and it is " +
                             "expected back. Reporting silence as a departure is a guess handed to every " +
                             "other player as news, and it is wrong exactly when it matters: the player whose " +
                             "connection blinked and who is about to reconnect";
            if (left.IndexOf("left", StringComparison.OrdinalIgnoreCase) < 0)
                yield return "L120 notices-not-distinguishable: the VOLUNTARY-LEAVE notice ('" + left + "') " +
                             "no longer says the player left. This is the ONE path where 'left' is a fact " +
                             "rather than an inference — it is emitted off the leaver's OWN farewell packet — " +
                             "so if even this one hedges, the two cases have collapsed into one and the " +
                             "distinction the feature was asked for is gone";
            if (string.Equals(left, lost, StringComparison.Ordinal))
                yield return "L120 notices-not-distinguishable: the voluntary-leave and connection-lost " +
                             "notices are the SAME string ('" + left + "'). Two different facts, one sentence: " +
                             "the remaining players cannot tell a player who quit from a player who is coming " +
                             "back, and the second one's empty seat looks abandoned for the rest of the session";

            // ═══ (c) EACH PATH EMITS ITS OWN, AND ONLY ITS OWN ═══
            foreach (var v in Emits(handleLeave, fLeave, fLost, mod, "the VOLUNTARY leave funnel",
                                    "reached only by the leaver's own ClientLeave packet"))
                yield return v;
            foreach (var v in Emits(pause, fLost, fLeave, mod, "the INVOLUNTARY silence funnel",
                                    "where a dead socket, a stalled write and the 20 s heartbeat timeout all land"))
                yield return v;
            if (!Reaches(resume, fBack, mod))
                yield return "L120 no-reconnect-notice: SessionManager.ResumePeer does not format the " +
                             "reconnected notice, so the session heals itself in silence. The connection-lost " +
                             "line is deliberately NOT final ('holding their seat'); without its counterpart " +
                             "every other screen keeps reading as one player down for the rest of the battle";

            // ═══ (d) IT REACHES EVERY REMAINING PEER'S SCREEN ═══
            if (!Reaches(pause, notice, mod) || !Reaches(resume, notice, mod) || !Reaches(handleLeave, notice, mod))
                yield return "L120 notice-not-emitted: one of PausePeer / ResumePeer / HandleLeave no longer " +
                             "goes through SessionManager.SystemNotice. SystemNotice is the single carrier " +
                             "that both fans the line out to every peer AND marks it as a thing to paint; " +
                             "posting a plain SystemChat instead leaves the event in a log that a player in " +
                             "the geoscape or mid-battle has no way to see";
            if (!Reaches(notice, broadcastChat, mod))
                yield return "L120 notice-not-emitted: SystemNotice does not reach BroadcastChat, so the " +
                             "notice never leaves the host at all";
            if (!Reaches(broadcastChat, toast, mod))
                yield return "L120 notice-not-on-screen: BroadcastChat does not paint the notice locally, so " +
                             "the HOST — the one peer that observed the event first — is the one peer that " +
                             "never sees it. It receives none of its own packets; law 11's missing half again";
            if (!Reaches(handleChat, toast, mod))
                yield return "L120 notice-not-on-screen: HandleChat does not paint an arriving notice, so a " +
                             "CLIENT learns that somebody left only if it happens to have the lobby chat " +
                             "panel open — which, in the geoscape or in a battle, it never does";

            // EXECUTED: the flag has to survive the wire, or every notice arrives as an ordinary chat line
            // and the whole feature fails silently on every peer except the host.
            var round = MessageSerializer.DeserializeChat(MessageSerializer.SerializeChat(
                new ChatMessageData { SenderSteamId = 0, SenderNick = "", Text = left, IsSystem = true, IsNotice = true }));
            if (!round.IsNotice || !round.IsSystem || round.Text != left)
                yield return "L120 notice-lost-on-the-wire: a notice does not survive the chat codec " +
                             "round-trip (isNotice=" + round.IsNotice + " isSystem=" + round.IsSystem +
                             " text='" + round.Text + "'). It would arrive on every client as an ordinary " +
                             "system chat line — filed into a log nobody has open, with no error anywhere";
            var plain = MessageSerializer.DeserializeChat(MessageSerializer.SerializeChat(
                new ChatMessageData { SenderSteamId = 0, SenderNick = "", Text = "x", IsSystem = true }));
            if (plain.IsNotice)
                yield return "L120 notice-lost-on-the-wire: an ORDINARY system chat line comes back as a " +
                             "notice, so every 'host set save' and 'a player is waiting' line would be " +
                             "thrown on screen as a prompt. The kind byte must separate the two";

            // ═══ (e) A BACKLOG IS NOT NEWS ═══
            if (Reaches(replay, toast, mod) || Reaches(replay, broadcastChat, mod))
                yield return "L120 backlog-replayed-as-news: ReplayChatHistoryTo paints or re-broadcasts the " +
                             "history it sends to a late joiner. That peer would be hit with every leave and " +
                             "reconnect of the whole session at once — as native prompts, in tactical — for " +
                             "events that were over before it arrived";

            // ═══ (f) THE ARM THAT MATTERS: ANNOUNCING IS NOT DECIDING ═══
            var disconnectPeer = typeof(ITransport).GetMethod("DisconnectPeer", All);
            foreach (var m in new[] { pause, resume, notice, broadcastChat, handleChat })
            {
                if (Reaches(m, removeClient, mod) ||
                    (disconnectPeer != null && Reaches(m, disconnectPeer, mod)))
                    yield return "L120 notice-path-removes-a-peer: " + m.Name + " takes a seat away while " +
                                 "announcing. Telling four players that somebody went quiet and THROWING THAT " +
                                 "PLAYER OUT are opposite acts: L84 allows a removal only on a voluntary " +
                                 "leave, and the pause exists precisely so the peer still has a seat to come " +
                                 "back to. A notice that kicks is the kick this repo removed, wearing a label";
                if (clientCount != null && Reaches(m, clientCount, mod))
                    yield return "L120 notice-path-counts-peers: " + m.Name + " reads SessionManager." +
                                 "ClientCount. An announcement is about ONE named peer and needs no headcount; " +
                                 "a headcount on this path is the first half of 'and if only one is left, " +
                                 "then…', which is a quorum (L91, L119) growing out of a status line";
            }

            // ═══ (g) THE FAREWELL IS ACTUALLY FLUSHED ═══
            if (!Reaches(quit, sendLeave, mod) || !Reaches(quit, sendHostGone, mod))
                yield return "L120 farewell-not-sent: MultiplayerUI.OnApplicationQuit no longer emits both " +
                             "farewells (client ClientLeave / host HostDisconnected). Alt+F4 and the window X " +
                             "never run the menu-quit path, so without this the peer simply stops answering " +
                             "and everyone else is told it LOST CONNECTION — for the most common way a " +
                             "player actually leaves a game";
            // ═══ (h) EXACTLY ONE NOTICE PER DEPARTURE, AND IT NAMES THE PLAYER ═══
            //
            // Every arm above asks WHICH notice a path emits. None of them asks HOW MANY, and the answer was
            // two: SessionNotifier held a second announcer on the same transport event HostLeaveHandler
            // already answers ("— X left —" plus "Host ended the session", both native prompts in tactical),
            // dead on the host by construction and wrong on the client on every path it could still reach.
            // A count is the outcome a player actually experiences, so it is derived here rather than
            // declared: EXECUTE every notice formatter the mod owns, keep the ones whose text CLAIMS A
            // DEPARTURE, and demand that exactly one method in the whole mod both formats such a line and
            // puts it on a screen. A second one is a second modal, whatever it is called.
            var probe = "Zephyr";
            var departureFormatters = new List<MethodBase>();
            foreach (var f in typeof(SessionLifecycle).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.ReturnType != typeof(string)) continue;
                var ps = f.GetParameters();
                if (ps.Length == 0 || ps.Length > 2 ||
                    !ps.All(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(bool))) continue;
                foreach (var args in Combinations(ps, probe))
                {
                    string text = null;
                    try { text = f.Invoke(null, args) as string; } catch { }
                    // "left" is the DEPARTURE claim; "lost connection" is deliberately a different fact and
                    // has its own single announcer, so both count as departure lines for the count below.
                    if (text != null && (text.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         text.IndexOf("lost connection", StringComparison.OrdinalIgnoreCase) >= 0))
                    { departureFormatters.Add(f); break; }
                }
            }
            if (departureFormatters.Count == 0)
                yield return "L120 departure-formatters-blind: EXECUTING every SessionLifecycle formatter " +
                             "produced no line that claims a departure at all, yet FormatLeaveNotice and " +
                             "FormatConnectionLostNotice are right there. The sweep this count rests on is " +
                             "broken, so 'exactly one notice per departure' is a statement about the empty set";
            else
            {
                var sinks = new[] { toast, notice, session.GetMethod("SystemChat", All) }
                            .Where(s => s != null).ToArray();
                var announcers = ModTypes(mod)
                    .SelectMany(t => { try { return t.GetMethods(All).Cast<MethodBase>(); }
                                       catch { return Enumerable.Empty<MethodBase>(); } })
                    .Where(m => { try { return m.GetMethodBody() != null; } catch { return false; } })
                    .Where(m => departureFormatters.Any(f => Reaches(m, f, mod)) &&
                                sinks.Any(s => Reaches(m, s, mod)))
                    .Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name)
                    .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
                // One per FACT, and there are exactly two facts: the leaver said so, or the host heard
                // nothing. Anything past that is the same departure announced twice on the same screen.
                if (announcers.Count != 2)
                    yield return "L120 two-notices-per-departure: " + announcers.Count + " method(s) in the mod " +
                                 "both format a departure line and put it on a screen — " +
                                 string.Join(", ", announcers) + ". There are exactly TWO departure facts (the " +
                                 "leaver's own farewell, and silence the host observed) and therefore exactly two " +
                                 "announcers may exist; a third is one player leaving and two prompts arriving, " +
                                 "which in tactical are two native modals stacked on the same screen. Fewer than " +
                                 "two means a departure nobody is told about";
            }
            // EXECUTED: the name. A farewell can arrive after the roster row is gone (a returning peer's
            // stale-rejoin prune, a drop the host handled first), and the notice then reads "— a player left
            // the game —" to everyone still in the session — the one case where it tells four people to go
            // and count heads instead.
            var resolve = typeof(SessionLifecycle).GetMethod("LeaverName", All);
            if (resolve == null)
                yield return "L120 leaver-name-unresolved: SessionLifecycle.LeaverName is gone, so the leave " +
                             "notice is back to naming the peer from the roster row alone — and the row is " +
                             "exactly what a late farewell no longer has";
            else if (SessionLifecycle.LeaverName(null, probe) != probe ||
                     SessionLifecycle.LeaverName("", probe) != probe ||
                     SessionLifecycle.LeaverName("Ayla", probe) != "Ayla" ||
                     SessionLifecycle.FormatLeaveNotice(SessionLifecycle.LeaverName(null, probe))
                         .IndexOf(probe, StringComparison.Ordinal) < 0)
                yield return "L120 notice-does-not-name-the-player: with the roster row already purged the " +
                             "leave notice does not fall back to the last name that peer was known by (it came " +
                             "out as '" + SessionLifecycle.FormatLeaveNotice(SessionLifecycle.LeaverName(null, probe)) +
                             "'), or a LIVE row stopped winning over the cached name — a renamed player would " +
                             "then be announced under the name they left behind";

            if (!Reaches(quit, shutdown, mod))
                yield return "L120 farewell-not-flushed: OnApplicationQuit sends the farewell but never drains " +
                             "the transport. DirectTransport.Send only ENQUEUES the frame for that peer's " +
                             "writer thread, and every writer is IsBackground — the process teardown that " +
                             "follows kills it where it stands, so the ClientLeave dies in the queue. " +
                             "NetworkEngine.Shutdown is the drain that already exists (flush-then-close per " +
                             "peer, then Join under one bounded grace budget); without it the notice everyone " +
                             "gets is a lost connection for a player who deliberately quit";
        }

        /// <summary>The path must format ITS OWN notice and must not be able to reach the other one — a
        /// crossed pair is not a missing notice, it is a confident wrong one.</summary>
        private static IEnumerable<string> Emits(MethodBase path, MethodBase mine, MethodBase theirs,
                                                 Assembly mod, string what, string why)
        {
            if (!Reaches(path, mine, mod))
                yield return "L120 wrong-notice-for-the-path: " + path.Name + " is " + what + " (" + why +
                             ") and no longer formats its own notice via SessionLifecycle." + mine.Name +
                             ". Whatever it announces now is not the fact it is in a position to know";
            if (Reaches(path, theirs, mod))
                yield return "L120 wrong-notice-for-the-path: " + path.Name + " is " + what + " (" + why +
                             ") yet reaches SessionLifecycle." + theirs.Name + " — the OTHER path's notice. " +
                             "It would report a fact it cannot establish: an observer cannot know a silent " +
                             "peer left, and a peer that sent its own farewell did not lose its connection";
        }

        /// <summary>Every type the mod actually loaded — a half-loadable assembly must narrow the sweep, not
        /// abort the law.</summary>
        private static Type[] ModTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            catch { return new Type[0]; }
        }

        /// <summary>Argument sets for EXECUTING a formatter headless: the probe name for every string slot,
        /// both values for every bool one (a "connected" flag decides whether the line is a departure at
        /// all, and this law must see the departure half of it).</summary>
        private static IEnumerable<object[]> Combinations(ParameterInfo[] ps, string probe)
        {
            var slots = ps.Select(p => p.ParameterType == typeof(bool)
                                       ? new object[] { false, true } : new object[] { probe }).ToArray();
            if (slots.Length == 1)
                foreach (var a in slots[0]) yield return new[] { a };
            else
                foreach (var a in slots[0])
                    foreach (var b in slots[1])
                        yield return new[] { a, b };
        }

        private static bool Reaches(MethodBase from, MethodBase target, Assembly mod) =>
            from != null && target != null &&
            Program.Callees(from, mod).Any(c => c.MetadataToken == target.MetadataToken &&
                                                c.Module == target.Module);
    }
}
