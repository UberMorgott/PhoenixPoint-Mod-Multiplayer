using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L521 — THE APPEND IS SCREEN-INDEPENDENT AND THE HOST'S POSITION IS THE ONLY ORDER.
    ///
    /// Creation, queueing and publication of a window never depend on which screen a peer is in, and never
    /// on a GeoscapeView existing at all. Only DISPLAY is postponed (§A.4). The one structural dependency
    /// before this work was that publication rode the QueryStateSwitch postfix and therefore needed a live
    /// GeoscapeView (src/Rail/GeoWindowCoverage.cs:663-676): no view → no postfix → no publication. The
    /// journal exists with or without a view, so the append is where that dependency dies.
    ///
    /// EXECUTED, never asserted. Every arm calls the real WindowJournal with no game, no level and no
    /// view — which is the strongest possible statement of "this needs no screen", because the law is
    /// running in a process that has none.
    ///
    /// ROLES SEPARATED (§C.3). Arm (a) executes the HOST role (MintHostPosition) and arms (b)-(d) execute
    /// the CLIENT role (Append of positions this process did not mint), in separate journal generations
    /// divided by Reset. L507's blind spot was running both roles in one undivided process; P1 was a
    /// HOST-ONLY fault — two host windows sharing one back-filled ordinal — so it gets its own arm.
    ///
    /// Falsify (compile-valid src mutations, each named): `MintHostPosition() => 1;` → (a); replace the
    /// ordered insert in Append with `_unread.Add` → (b); delete the `pos == 0` refusal → (c); make
    /// TryRead peek instead of remove → (d).
    /// </summary>
    internal static class L521_TheAppendIsScreenIndependent
    {
        internal static IEnumerable<string> Check()
        {
            WindowJournal.Reset();

            // (a) HOST ROLE: two windows minted back to back get two DIFFERENT, INCREASING positions.
            uint first = WindowJournal.MintHostPosition();
            uint second = WindowJournal.MintHostPosition();
            if (first == 0 || second <= first)
                yield return "L521 host-positions-tie: two windows minted in a row got positions " + first +
                             " and " + second + ". They must be distinct and increasing. A tie is exactly " +
                             "P1: on 2026-08-15 the host's research and event shared one back-filled " +
                             "RailOrdinal and fell back to insert order, and both clients — the CORRECT " +
                             "peers — presented the other way round.";

            // (b) CLIENT ROLE, fresh generation: positions arriving OUT OF ORDER present IN order.
            WindowJournal.Reset();
            WindowJournal.Append(2, "UIStateGeoscapeEvent", new byte[] { 2 });
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            var order = new List<string>();
            JournalEntry entry;
            while (WindowJournal.TryRead(out entry)) order.Add(entry.Family);
            if (order.Count != 2 || order[0] != "UIStateGeoModal")
                yield return "L521 arrival-order-wins: entries appended as 2 then 1 presented as [" +
                             string.Join(",", order.ToArray()) + "]. The HOST's position decides, never " +
                             "arrival — the measured inter-channel skew was 363 ms, which no client-side " +
                             "settle can be tuned to cover.";

            // (c) NO POSITION, NO WINDOW — the LMAX single-entrance property, executed.
            WindowJournal.Reset();
            WindowJournal.Append(0, "UIStateGeoModal", new byte[] { 0 });
            if (WindowJournal.UnreadCount != 0)
                yield return "L521 unpositioned-window-accepted: a window with position 0 entered the " +
                             "journal. Claiming a position is the ONLY way a window can exist; an entry " +
                             "with no position sorts first by accident and is a bypass by construction.";

            // (d) READ IS DELETE — the whole retention policy, executed rather than described.
            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            JournalEntry read;
            WindowJournal.TryRead(out read);
            if (WindowJournal.UnreadCount != 0 || !WindowJournal.LocalJournalEmpty)
                yield return "L521 read-did-not-delete: after reading the only entry the journal still " +
                             "reports " + WindowJournal.UnreadCount + " unread. Read ⇒ deleted is the " +
                             "entire retention policy — there is no cap, no trim and no staleness pass, " +
                             "so an entry that survives its own read is an unbounded leak.";

            // POSITIVE CONTROL: the journal must be capable of holding something, or every arm above is a
            // statement about an object that does nothing.
            WindowJournal.Reset();
            WindowJournal.Append(7, "UIStateGeoModal", new byte[] { 7 });
            if (WindowJournal.UnreadCount != 1 || WindowJournal.LocalJournalEmpty)
                yield return "L521 positive-control: a valid append did not land, so every arm above " +
                             "passed against an inert journal and proved nothing.";
            WindowJournal.Reset();
        }
    }
}
