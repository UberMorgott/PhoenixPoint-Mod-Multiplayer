using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>How a window family's dismissal travels. DEFAULT IS LOCAL and an undeclared family IS
    /// local — a new family needs no code (§A.5).</summary>
    internal enum DismissScope : byte { Local = 0, Global = 1 }

    /// <summary>One pending window, as the journal holds it. <see cref="Payload"/> is the family's own
    /// already-encoded raise payload, carried verbatim — the journal orders windows, it does not know
    /// what is in them.</summary>
    internal sealed class JournalEntry
    {
        internal uint Pos;
        internal string Family;
        internal byte[] Payload;
    }

    /// <summary>
    /// THE WINDOW JOURNAL — one append-only, host-ordered stream of pending windows with a per-peer read
    /// cursor. Claiming a position is the ONLY way a window can exist (LMAX single-entrance, §A.1): any
    /// path that can present a window without one is a bypass and is closed by L520/L521.
    ///
    /// RETENTION IS THE WHOLE POLICY AND IT IS ONE RULE: A PEER'S ENTRY IS DELETED THE MOMENT THAT PEER
    /// HAS READ IT. No cap, no tail-trim, no time-based staleness, no LRU, no compaction pass. The
    /// backlog's length is bounded by what the local player has not looked at, which is a quantity the
    /// player controls. Deletion is PER-PEER; removing an entry a peer has NOT read needs the explicit
    /// host-minted void of <see cref="ApplyVoid"/> (§A.5) — an implicit per-peer timeout makes two peers
    /// diverge (FIX gap-fill, §2.5).
    ///
    /// NOT PERSISTED, EVER (§A.2b). A savegame contains zero journal entries: no codec, no
    /// SerializationData field, no restore path. A reconnecting peer receives only entries appended AFTER
    /// its reconnect and that is INTENDED — do not add a catch-up replay and do not log it as an anomaly.
    /// An AUTOSAVE always proceeds and whatever is unread at that moment is lost (§A.2c); the empty-journal
    /// gate covers PLAYER-INITIATED saves only and reads only <see cref="UnreadCount"/>, i.e. only this
    /// peer's own cursor. It is therefore not a quorum and must never become one.
    ///
    /// PURE C#, no Unity types and no engine call, so RailCheck laws and the RailSim harness both execute
    /// this class directly rather than asserting its shape.
    /// </summary>
    internal static class WindowJournal
    {
        /// <summary>RUNAWAY-RAISER CANARY ONLY (§A.6). Crossing it logs ONE error for the session and the
        /// append CONTINUES. It never drops an entry and never stops the append — it exists to make a
        /// raiser loop visible in a log, nothing more.</summary>
        internal const int RunawayCanaryAt = 4096;

        private static uint _nextPos;                       // host only: the single ordered stream
        private static readonly List<JournalEntry> _unread = new List<JournalEntry>();
        private static bool _canaryLogged;

        private static uint _pendingPos;
        private static string _pendingFamily;

        /// <summary>The declaration table, and the ONLY place a family's scope may be written. No
        /// `if (family == …)` anywhere else in the codebase (§A.5).</summary>
        private static readonly Dictionary<string, DismissScope> FamilyScope =
            new Dictionary<string, DismissScope>(StringComparer.Ordinal)
            {
                // The mission family is the one GLOBAL family: once ANYONE has acted on a mission the
                // decision to deploy is taken, so it is meaningless for the others to accept or refuse.
                { "UIStateGeoMissionBrief", DismissScope.Global },
                { "UIStateRosterDeployment", DismissScope.Global },
            };

        /// <summary>Undeclared ⇒ LOCAL. A new window family needs no code at all.</summary>
        internal static DismissScope ScopeOf(string family) =>
            family != null && FamilyScope.TryGetValue(family, out var scope) ? scope : DismissScope.Local;

        /// <summary>HOST ONLY: claim the next position in the one ordered stream. Monotonic and never
        /// reused within a session. Positions start at 1 so 0 can mean "no position", which is what makes
        /// an unpositioned window detectable rather than silently first.</summary>
        internal static uint MintHostPosition() => ++_nextPos;

        /// <summary>
        /// THE SEAM HAND-OFF. The position is minted at the ONE capture seam — the
        /// <c>QueryStateSwitch</c> postfix — and the family's own publisher runs LATER IN THAT SAME
        /// SYNCHRONOUS CALL STACK, because every publisher is a
        /// postfix on the very native method that just queued the window (<c>GeoscapeView.OpenModal</c>,
        /// <c>OpenModalPersistent</c>, <c>OnGeoscapeEventRaised</c>) or is called from the mint site
        /// itself. So "the position of the window being published right now" is a single value, not a
        /// parameter that would have to be threaded through four call sites — and threading it through
        /// only some of them would create the second mechanism §A.7 exists to delete.
        ///
        /// TAKING CLEARS IT. A publisher that runs with no fresh mint (the mission-brief unicast, whose
        /// host-side native window is SUPPRESSED and therefore never queued) gets 0 and ships no position,
        /// rather than re-shipping the previous window's — a stale position is a duplicate entry.
        /// </summary>
        internal static void SetHostPending(uint pos, string family)
        {
            _pendingPos = pos;
            _pendingFamily = family;
        }

        /// <summary>Consume the pending mint. Returns 0 / null when no window was queued since the last
        /// take, which is the honest answer for a raise the host never queued natively.</summary>
        internal static void TakeHostPending(out uint pos, out string family)
        {
            pos = _pendingPos;
            family = _pendingFamily;
            _pendingPos = 0;
            _pendingFamily = null;
        }

        /// <summary>APPEND AT THE TAIL. Idempotent on <paramref name="pos"/>: a re-delivered raise is the
        /// same entry, not a second window. Ordered by position on insert, so a message that arrives late
        /// lands in the host's order rather than in arrival order — this is the whole of P1's fix.</summary>
        internal static void Append(uint pos, string family, byte[] payload)
        {
            if (pos == 0)
            {
                MpLog.LogError("[Multiplayer][windows] refused a journal append with no position — family '" +
                               (family ?? "<null>") + "'. Claiming a position is the only way a window can " +
                               "exist; this raise bypassed the mint seam");
                return;
            }
            for (int i = 0; i < _unread.Count; i++) if (_unread[i].Pos == pos) return;   // re-delivery
            int at = _unread.Count;
            while (at > 0 && _unread[at - 1].Pos > pos) at--;
            _unread.Insert(at, new JournalEntry { Pos = pos, Family = family, Payload = payload });
            if (_unread.Count >= RunawayCanaryAt && !_canaryLogged)
            {
                _canaryLogged = true;
                MpLog.LogError("[Multiplayer][windows] unread window backlog crossed " + RunawayCanaryAt +
                               " entries — a raiser is looping. NOTHING IS DROPPED and the append " +
                               "continues; this line exists to make the loop visible (once per session)");
            }
        }

        /// <summary>READ ⇒ DELETED. Takes the lowest unread position and removes it in the same call, so
        /// there is no window in which an entry is both read and still present.</summary>
        internal static bool TryRead(out JournalEntry entry)
        {
            if (_unread.Count == 0) { entry = null; return false; }
            entry = _unread[0];
            _unread.RemoveAt(0);
            return true;
        }

        /// <summary>Look at the head without consuming it — the drain gate needs to ask "is the next
        /// window this one?" without spending the read.</summary>
        internal static JournalEntry PeekHead() => _unread.Count == 0 ? null : _unread[0];

        /// <summary>THE HOST-MINTED VOID (§A.5). Removes an entry a peer has NOT read. Returns true when
        /// something was removed, so the caller knows whether it must also close an already-open copy.
        /// A CLIENT NEVER CALLS THIS OFF ITS OWN EVALUATION — only off a received void record.</summary>
        internal static bool ApplyVoid(uint pos)
        {
            for (int i = 0; i < _unread.Count; i++)
                if (_unread[i].Pos == pos) { _unread.RemoveAt(i); return true; }
            return false;
        }

        internal static int UnreadCount => _unread.Count;

        /// <summary>THE SAVE PREDICATE (§A.2b). Reads ONLY this peer's own cursor: no roster, no peer
        /// list, no message, no acknowledgement. An AFK peer blocks only their OWN save.</summary>
        internal static bool LocalJournalEmpty => _unread.Count == 0;

        /// <summary>Session teardown. Positions restart because the journal is session-scoped (§A.2b) —
        /// and because a reconnecting peer receives only what is appended after it rejoins.</summary>
        internal static void Reset()
        {
            _unread.Clear();
            _nextPos = 0;
            _canaryLogged = false;
            _pendingPos = 0;
            _pendingFamily = null;
        }
    }
}
